using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using WsSearchMcp.Security;
using Xunit;

namespace WsSearchMcp.Tests.Security;

public class ClientNameTests
{
    [Theory]
    [InlineData("laptop-claude-code")]
    [InlineData("Work VS Code")]
    [InlineData("a")]
    [InlineData("Büro_PC.1")]
    public void Valid_names_are_accepted(string name)
    {
        Assert.True(ClientNames.TryNormalize(name, out var n, out var err), err);
        Assert.Equal(name.Trim(), n);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a,b")]
    [InlineData("a=b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("quote\"s")]
    public void Invalid_names_are_rejected(string name)
    {
        Assert.False(ClientNames.TryNormalize(name, out _, out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void Names_are_trimmed_and_length_limited()
    {
        Assert.True(ClientNames.TryNormalize("  padded  ", out var n, out _));
        Assert.Equal("padded", n);
        Assert.True(ClientNames.TryNormalize(new string('x', 64), out _, out _));
        Assert.False(ClientNames.TryNormalize(new string('x', 65), out _, out _));
    }
}

public class ClientTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static (InMemoryClientStore store, MintResult minted) MintOne(string name = "Test Client", TimeSpan? life = null)
    {
        var store = new InMemoryClientStore();
        return (store, ClientTokens.Mint(store, name, life ?? TimeSpan.FromDays(30), Now));
    }

    [Fact]
    public void Minted_token_validates()
    {
        var (store, m) = MintOne();
        var r = ClientTokens.Validate(m.Token, store, Now.AddMinutes(1));
        Assert.True(r.Ok, r.Failure);
        Assert.Equal("Test Client", r.Client!.Name);
    }

    [Fact]
    public void Store_holds_only_public_key_material()
    {
        var (store, m) = MintOne();
        using var cert = X509CertificateLoader.LoadCertificate(store.List().Single().CertificateDer);
        Assert.False(cert.HasPrivateKey);
        // No private scalar (32 bytes of P-256 d) can be exported from a public-only certificate.
        Assert.Null(cert.GetECDsaPrivateKey());
        Assert.DoesNotContain(m.Token.Split('.')[2], Convert.ToBase64String(store.List().Single().CertificateDer));
    }

    [Fact]
    public void Each_mint_uses_a_distinct_key()
    {
        var store = new InMemoryClientStore();
        var a = ClientTokens.Mint(store, "a", null, Now);
        var b = ClientTokens.Mint(store, "b", null, Now);
        Assert.NotEqual(a.Client.Fingerprint, b.Client.Fingerprint);
        // A token for one client is not accepted under the other's kid.
        var parts = a.Token.Split('.');
        var header = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[0])).Replace(a.Client.Kid, b.Client.Kid);
        var forged = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header)) + "." + parts[1] + "." + parts[2];
        Assert.False(ClientTokens.Validate(forged, store, Now).Ok);
    }

    [Fact]
    public void Duplicate_names_are_rejected_case_insensitively()
    {
        var (store, _) = MintOne("Laptop");
        var ex = Assert.Throws<InvalidOperationException>(() => ClientTokens.Mint(store, "LAPTOP", null, Now));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void Invalid_name_is_rejected_at_mint()
    {
        Assert.Throws<ArgumentException>(() => ClientTokens.Mint(new InMemoryClientStore(), "bad,name", null, Now));
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var (store, m) = MintOne(life: TimeSpan.FromDays(1));
        Assert.True(ClientTokens.Validate(m.Token, store, Now.AddHours(23)).Ok);
        var r = ClientTokens.Validate(m.Token, store, Now.AddDays(2));
        Assert.False(r.Ok);
        Assert.Contains("expired", r.Failure);
    }

    [Fact]
    public void Never_expiring_client_has_no_expiry()
    {
        var store = new InMemoryClientStore();
        var m = ClientTokens.Mint(store, "forever", null, Now);
        Assert.Null(m.Client.Expires);
        Assert.True(ClientTokens.Validate(m.Token, store, Now.AddYears(30)).Ok);
    }

    [Fact]
    public void Revoked_client_is_rejected()
    {
        var (store, m) = MintOne();
        Assert.True(store.Remove("test client"));
        var r = ClientTokens.Validate(m.Token, store, Now);
        Assert.False(r.Ok);
        Assert.Contains("revoked", r.Failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("a.b.c")]
    [InlineData("....")]
    public void Malformed_tokens_are_rejected_without_throwing(string? token)
    {
        Assert.False(ClientTokens.Validate(token, new InMemoryClientStore(), Now).Ok);
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var (store, m) = MintOne();
        var parts = m.Token.Split('.');
        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1])).Replace("Test Client", "Admin");
        var tampered = parts[0] + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload)) + "." + parts[2];
        Assert.False(ClientTokens.Validate(tampered, store, Now).Ok);
    }

    [Fact]
    public void Truncated_or_wrong_signature_is_rejected()
    {
        var (store, m) = MintOne();
        var parts = m.Token.Split('.');
        Assert.False(ClientTokens.Validate(parts[0] + "." + parts[1] + "." + Base64Url.EncodeToString(new byte[64]), store, Now).Ok);
        Assert.False(ClientTokens.Validate(parts[0] + "." + parts[1] + "." + Base64Url.EncodeToString(new byte[10]), store, Now).Ok);
        Assert.False(ClientTokens.Validate(parts[0] + "." + parts[1] + ".", store, Now).Ok);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    [InlineData("RS256")]
    public void Other_algorithms_are_rejected(string alg)
    {
        var (store, m) = MintOne();
        var parts = m.Token.Split('.');
        var header = JsonSerializer.Serialize(new { alg, typ = "JWT", kid = m.Client.Kid });
        var token = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header)) + "." + parts[1] + "." + parts[2];
        var r = ClientTokens.Validate(token, store, Now);
        Assert.False(r.Ok);
        Assert.Contains("alg", r.Failure);

        // Unsigned "alg: none" form.
        var unsigned = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header)) + "." + parts[1] + ".";
        Assert.False(ClientTokens.Validate(unsigned, store, Now).Ok);
    }

    [Fact]
    public void Wrong_audience_is_rejected_even_with_a_valid_signature()
    {
        var store = new InMemoryClientStore();
        var (token, _) = ForgeSigned(store, aud: "someone-else", expUnix: null);
        var r = ClientTokens.Validate(token, store, Now);
        Assert.False(r.Ok);
        Assert.Contains("audience", r.Failure);
    }

    [Fact]
    public void Token_exp_claim_is_enforced_independently_of_client_expiry()
    {
        var store = new InMemoryClientStore();
        var (token, _) = ForgeSigned(store, aud: ClientTokens.Audience, expUnix: Now.AddMinutes(-1).ToUnixTimeSeconds());
        var r = ClientTokens.Validate(token, store, Now);
        Assert.False(r.Ok);
        Assert.Contains("expired", r.Failure);
    }

    [Fact]
    public void Oversized_token_is_rejected()
    {
        Assert.False(ClientTokens.Validate(new string('a', ClientTokens.MaxTokenLength + 1), new InMemoryClientStore(), Now).Ok);
    }

    /// <summary>Registers a client and signs an arbitrary payload with its key (test-only).</summary>
    private static (string token, ClientRecord record) ForgeSigned(InMemoryClientStore store, string aud, long? expUnix)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var kid = Guid.NewGuid().ToString("N");
        var req = new CertificateRequest($"CN=forged, OU={kid}", key, HashAlgorithmName.SHA256);
        using var cert = req.CreateSelfSigned(Now.AddDays(-1), Now.AddDays(30));
        var record = new ClientRecord(kid, "forged", Now.AddDays(-1), Now.AddDays(30), cert.Export(X509ContentType.Cert));
        store.Add(record);

        var header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "JWT", kid }));
        var claims = new Dictionary<string, object> { ["sub"] = "forged", ["aud"] = aud };
        if (expUnix is { } e) claims["exp"] = e;
        var payload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(claims));
        var sig = key.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return (header + "." + payload + "." + Base64Url.EncodeToString(sig), record);
    }
}
