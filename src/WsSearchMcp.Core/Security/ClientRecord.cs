using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WsSearchMcp.Security;

/// <summary>
/// A minted client as kept by the server: only public material. <see cref="CertificateDer"/> is a
/// public-key-only X.509 certificate that wraps the client's public key.
/// </summary>
public sealed record ClientRecord(
    string Kid,
    string Name,
    DateTimeOffset Created,
    DateTimeOffset? Expires,
    byte[] CertificateDer)
{
    /// <summary>Short fingerprint (SHA-256 of the public key, first 8 bytes, hex) for display.</summary>
    public string Fingerprint
    {
        get
        {
            using var cert = X509CertificateLoader.LoadCertificate(CertificateDer);
            return Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()), 0, 8);
        }
    }

    public bool IsExpired(DateTimeOffset now) => Expires is { } e && e <= now;
}
