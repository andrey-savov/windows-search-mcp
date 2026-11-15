using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace WsSearchMcp.Security;

public sealed record MintResult(string Token, ClientRecord Client);

public sealed record TokenValidation(ClientRecord? Client, string? Failure)
{
    public bool Ok => Client is not null;
    public static TokenValidation Success(ClientRecord c) => new(c, null);
    public static TokenValidation Fail(string reason) => new(null, reason);
}

/// <summary>
/// Client token format: a compact ES256 JWS. At mint time a fresh P-256 key pair is generated, the
/// token is signed with the private key, the public key is handed to the store, and the private key
/// is discarded. The server can therefore verify tokens but can never forge one.
/// </summary>
public static class ClientTokens
{
    public const string Audience = "ws-search-mcp";
    public const int MaxTokenLength = 2048;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(365);

    /// <summary>Mints a client, stores its public key, and returns the token (shown once).</summary>
    /// <param name="lifetime">Token lifetime; <c>null</c> means the token never expires.</param>
    /// <param name="permissions">Extra permissions (see <see cref="ClientPermissions"/>); <c>null</c> = search only.</param>
    public static MintResult Mint(IClientStore store, string name, TimeSpan? lifetime, DateTimeOffset now,
        IEnumerable<string>? permissions = null)
    {
        if (!ClientNames.TryNormalize(name, out var normalized, out var error))
            throw new ArgumentException(error, nameof(name));
        var perms = (permissions ?? Array.Empty<string>())
            .Select(p => p.Trim().ToLowerInvariant()).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        var unknown = perms.FirstOrDefault(p => !ClientPermissions.IsKnown(p));
        if (unknown is not null)
            throw new ArgumentException($"Unknown permission '{unknown}'.", nameof(permissions));
        if (lifetime is { } l && l <= TimeSpan.Zero)
            throw new ArgumentException("Lifetime must be positive.", nameof(lifetime));
        if (store.FindByName(normalized) is not null)
            throw new InvalidOperationException(
                $"A client named '{normalized}' already exists. Choose a different name, or revoke the existing client first.");

        var kid = Guid.NewGuid().ToString("N");
        // Whole seconds: certificates and JWT timestamps have one-second resolution.
        now = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        DateTimeOffset? expires = lifetime is { } life ? now + life : null;

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // Permissions ride in the (public) certificate subject as extra OU=perm:<name> RDNs.
        var subject = $"CN={normalized}, OU={kid}" + string.Concat(perms.Select(p => $", OU={ClientPermissions.SubjectPrefix}{p}"));
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        var validity = WindowsCertClientStore.ValidityFor(now, expires);
        byte[] certDer;
        using (var cert = request.CreateSelfSigned(now, now + validity))
            certDer = cert.Export(X509ContentType.Cert); // public key only

        var header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["alg"] = "ES256", ["typ"] = "JWT", ["kid"] = kid,
        });
        var payloadClaims = new Dictionary<string, object>
        {
            ["sub"] = normalized, ["aud"] = Audience, ["iat"] = now.ToUnixTimeSeconds(),
        };
        if (expires is { } exp) payloadClaims["exp"] = exp.ToUnixTimeSeconds();
        var payload = JsonSerializer.SerializeToUtf8Bytes(payloadClaims);

        var signingInput = Base64Url.EncodeToString(header) + "." + Base64Url.EncodeToString(payload);
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var token = signingInput + "." + Base64Url.EncodeToString(signature);

        var record = new ClientRecord(kid, normalized, now, expires, certDer) { Permissions = perms };
        store.Add(record);
        return new MintResult(token, record);
        // `key` (the private key) is disposed here and never persisted.
    }

    /// <summary>Verifies a bearer token against the store. Never throws on malformed input.</summary>
    public static TokenValidation Validate(string? token, IClientStore store, DateTimeOffset now)
    {
        try
        {
            if (string.IsNullOrEmpty(token)) return TokenValidation.Fail("missing token");
            if (token.Length > MaxTokenLength) return TokenValidation.Fail("token too long");

            var parts = token.Split('.');
            if (parts.Length != 3) return TokenValidation.Fail("malformed token");

            using var header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0]));
            var h = header.RootElement;
            if (h.ValueKind != JsonValueKind.Object) return TokenValidation.Fail("malformed header");
            if (!h.TryGetProperty("alg", out var alg) || alg.ValueKind != JsonValueKind.String || alg.GetString() != "ES256")
                return TokenValidation.Fail("unsupported alg");
            if (!h.TryGetProperty("kid", out var kidEl) || kidEl.ValueKind != JsonValueKind.String)
                return TokenValidation.Fail("missing kid");

            var client = store.FindByKid(kidEl.GetString()!);
            if (client is null) return TokenValidation.Fail("unknown or revoked client");

            var signature = Base64Url.DecodeFromChars(parts[2]);
            if (signature.Length != 64) return TokenValidation.Fail("bad signature");

            using (var cert = X509CertificateLoader.LoadCertificate(client.CertificateDer))
            using (var pub = cert.GetECDsaPublicKey())
            {
                if (pub is null) return TokenValidation.Fail("client key unusable");
                var ok = pub.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature,
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                if (!ok) return TokenValidation.Fail("bad signature");
            }

            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            var p = payload.RootElement;
            if (p.ValueKind != JsonValueKind.Object) return TokenValidation.Fail("malformed payload");
            if (!p.TryGetProperty("aud", out var aud) || aud.ValueKind != JsonValueKind.String || aud.GetString() != Audience)
                return TokenValidation.Fail("wrong audience");
            if (p.TryGetProperty("exp", out var expEl))
            {
                if (expEl.ValueKind != JsonValueKind.Number || !expEl.TryGetInt64(out var exp))
                    return TokenValidation.Fail("malformed exp");
                if (DateTimeOffset.FromUnixTimeSeconds(exp) <= now) return TokenValidation.Fail("token expired");
            }
            if (client.IsExpired(now)) return TokenValidation.Fail("client expired");

            return TokenValidation.Success(client);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or CryptographicException or InvalidOperationException)
        {
            return TokenValidation.Fail("malformed token");
        }
    }
}
