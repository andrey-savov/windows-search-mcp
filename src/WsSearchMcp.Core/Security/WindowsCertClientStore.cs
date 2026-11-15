using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace WsSearchMcp.Security;

/// <summary>
/// Keeps client public keys as public-key-only certificates in a dedicated per-user Windows
/// certificate store (CurrentUser\WsSearchMcp.Clients by default). Visible in certmgr.msc;
/// protected by the user profile.
/// </summary>
public sealed class WindowsCertClientStore : IClientStore
{
    public const string DefaultStoreName = "WsSearchMcp.Clients";

    private static readonly Regex KidInSubject = new(@"OU=([0-9a-fA-F]{32})", RegexOptions.Compiled);

    // Certificates for clients whose tokens never expire are given this validity.
    private static readonly TimeSpan NeverValidity = TimeSpan.FromDays(365.25 * 100);
    private static readonly TimeSpan NeverThreshold = TimeSpan.FromDays(365.25 * 50);

    private readonly string _storeName;

    public WindowsCertClientStore(string storeName = DefaultStoreName) => _storeName = storeName;

    public string StoreName => _storeName;

    public static TimeSpan ValidityFor(DateTimeOffset created, DateTimeOffset? expires) =>
        expires is { } e ? e - created : NeverValidity;

    public IReadOnlyList<ClientRecord> List()
    {
        using var store = Open();
        var result = new List<ClientRecord>();
        foreach (var cert in store.Certificates)
        {
            using (cert)
            {
                var m = KidInSubject.Match(cert.Subject);
                if (!m.Success) continue;
                var created = new DateTimeOffset(cert.NotBefore.ToUniversalTime(), TimeSpan.Zero);
                var notAfter = new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero);
                DateTimeOffset? expires = notAfter - created > NeverThreshold ? null : notAfter;
                result.Add(new ClientRecord(
                    m.Groups[1].Value.ToLowerInvariant(),
                    cert.GetNameInfo(X509NameType.SimpleName, false),
                    created,
                    expires,
                    cert.Export(X509ContentType.Cert)));
            }
        }
        return result;
    }

    public void Add(ClientRecord record)
    {
        using var store = Open();
        using var cert = X509CertificateLoader.LoadCertificate(record.CertificateDer);
        store.Add(cert);
    }

    public bool Remove(string name)
    {
        using var store = Open();
        var removed = false;
        foreach (var cert in store.Certificates.ToArray())
        {
            using (cert)
            {
                if (!KidInSubject.IsMatch(cert.Subject)) continue;
                if (!ClientNames.Equal(cert.GetNameInfo(X509NameType.SimpleName, false), name)) continue;
                store.Remove(cert);
                removed = true;
            }
        }
        return removed;
    }

    /// <summary>Removes every client (used by tests and uninstall).</summary>
    public void DeleteAll()
    {
        using var store = Open();
        foreach (var cert in store.Certificates.ToArray())
        {
            store.Remove(cert);
            cert.Dispose();
        }
    }

    /// <summary>Removes every client and deletes the (now empty) store itself from the registry.</summary>
    public void DeleteStore()
    {
        DeleteAll();
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\SystemCertificates\" + _storeName, throwOnMissingSubKey: false);
    }

    private X509Store Open()
    {
        var store = new X509Store(_storeName, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite | OpenFlags.IncludeArchived);
        return store;
    }
}
