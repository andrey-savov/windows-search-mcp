using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WsSearchMcp.Security;

public sealed record CertificateInfo(
    string Thumbprint, string Subject, IReadOnlyList<string> SubjectAlternativeNames,
    DateTimeOffset NotBefore, DateTimeOffset NotAfter, bool HasPrivateKey)
{
    public TimeSpan Remaining(DateTimeOffset now) => NotAfter - now;
}

/// <summary>Lifecycle of the server's TLS certificate (self-signed by default, CA-issued optionally).</summary>
public static class CertificateManager
{
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(365);
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(30);

    /// <summary>localhost, machine name, FQDN, loopback addresses and current non-loopback IPs.</summary>
    public static IReadOnlyList<string> DefaultSans()
    {
        var sans = new List<string> { "localhost" };
        try
        {
            var host = Dns.GetHostName();
            sans.Add(host);
            try
            {
                var fqdn = Dns.GetHostEntry(host).HostName;
                if (!string.IsNullOrWhiteSpace(fqdn)) sans.Add(fqdn);
            }
            catch (SocketException) { /* no DNS: machine name alone */ }
        }
        catch (SocketException) { }

        sans.Add("127.0.0.1");
        sans.Add("::1");
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast) continue;
                    sans.Add(ip.ToString());
                }
            }
        }
        catch (NetworkInformationException) { }

        return Dedupe(sans);
    }

    public static IReadOnlyList<string> Dedupe(IEnumerable<string> names) =>
        names.Select(n => n.Trim()).Where(n => n.Length > 0)
             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Builds a self-signed server certificate around <paramref name="key"/>.</summary>
    public static X509Certificate2 CreateSelfSigned(
        ECDsa key, IEnumerable<string> sans, DateTimeOffset now, TimeSpan? validity = null)
    {
        var request = new CertificateRequest("CN=ws-search-mcp", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth

        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in Dedupe(sans))
        {
            if (IPAddress.TryParse(name, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(name);
        }
        request.CertificateExtensions.Add(san.Build());

        return request.CreateSelfSigned(now.AddMinutes(-5), now + (validity ?? DefaultValidity));
    }

    public static CertificateInfo Describe(X509Certificate2 cert)
    {
        var names = new List<string>();
        var ext = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (ext is not null)
        {
            names.AddRange(ext.EnumerateDnsNames());
            names.AddRange(ext.EnumerateIPAddresses().Select(i => i.ToString()));
        }
        return new CertificateInfo(
            cert.Thumbprint, cert.Subject, names,
            new DateTimeOffset(cert.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            cert.HasPrivateKey);
    }

    /// <summary>
    /// Creates a self-signed certificate with a persisted, non-exportable CNG key and installs it in
    /// CurrentUser\My. Returns the installed certificate.
    /// </summary>
    public static X509Certificate2 InstallNewSelfSigned(IEnumerable<string> sans, DateTimeOffset now)
    {
        var keyName = "ws-search-mcp-" + Guid.NewGuid().ToString("N");
        var keyParams = new CngKeyCreationParameters
        {
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
        };
        using var cngKey = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, keyParams);
        using var ecdsa = new ECDsaCng(cngKey);
        using var selfSigned = CreateSelfSigned(ecdsa, sans, now);

        // Re-associate the persisted key with the public certificate so the store keeps the link.
        using var publicOnly = X509CertificateLoader.LoadCertificate(selfSigned.Export(X509ContentType.Cert));
        var withKey = publicOnly.CopyWithPrivateKey(ecdsa);

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(withKey);
        return withKey;
    }

    public static X509Certificate2? Find(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return null;
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint.Replace(" ", ""), validOnly: false);
        return found.Count > 0 ? found[0] : null;
    }

    /// <summary>Removes a certificate from CurrentUser\My and deletes its persisted key (best effort).</summary>
    public static void Remove(string thumbprint)
    {
        using var cert = Find(thumbprint);
        if (cert is null) return;
        try
        {
            using var key = cert.GetECDsaPrivateKey();
            if (key is ECDsaCng cng) cng.Key.Delete();
        }
        catch (CryptographicException) { }

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Remove(cert);
    }
}
