using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WsSearchMcp.Security;
using Xunit;

namespace WsSearchMcp.Tests.Security;

public class WindowsCertClientStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // A throwaway per-test store so the user's real client store is never touched.
    private readonly WindowsCertClientStore _store = new("WsSearchMcp.Tests." + Guid.NewGuid().ToString("N"));

    public void Dispose() => _store.DeleteStore();

    [Fact]
    public void Add_list_remove_round_trip()
    {
        Assert.Empty(_store.List());

        var m = ClientTokens.Mint(_store, "Work VS Code", TimeSpan.FromDays(90), Now);
        var listed = Assert.Single(_store.List());
        Assert.Equal("Work VS Code", listed.Name);
        Assert.Equal(m.Client.Kid, listed.Kid);
        Assert.NotNull(listed.Expires);
        Assert.Equal(m.Client.Fingerprint, listed.Fingerprint);

        Assert.True(ClientTokens.Validate(m.Token, _store, Now.AddMinutes(1)).Ok);

        Assert.True(_store.Remove("work vs code"));
        Assert.Empty(_store.List());
        Assert.False(ClientTokens.Validate(m.Token, _store, Now.AddMinutes(1)).Ok);
        Assert.False(_store.Remove("work vs code"));
    }

    [Fact]
    public void Never_expiring_client_round_trips_as_null_expiry()
    {
        var m = ClientTokens.Mint(_store, "forever", null, Now);
        Assert.Null(Assert.Single(_store.List()).Expires);
        Assert.True(ClientTokens.Validate(m.Token, _store, Now.AddYears(20)).Ok);
    }

    [Fact]
    public void Stored_certificates_have_no_private_key()
    {
        ClientTokens.Mint(_store, "pubonly", null, Now);
        using var store = new X509Store(_store.StoreName, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var cert = Assert.Single(store.Certificates);
        Assert.False(cert.HasPrivateKey);
    }
}

public class CachingClientStoreTests
{
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan span) => _ticks += span.Ticks;
    }

    [Fact]
    public void Out_of_process_changes_appear_after_the_ttl_without_a_restart()
    {
        var inner = new InMemoryClientStore();
        var time = new ManualTime();
        var cache = new CachingClientStore(inner, TimeSpan.FromSeconds(3), time);
        var m = ClientTokens.Mint(inner, "a", null, DateTimeOffset.UtcNow);

        Assert.Single(cache.List());
        inner.Remove("a"); // e.g. the admin tool revoking in another process
        Assert.Single(cache.List()); // still cached
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty(cache.List());
        Assert.False(ClientTokens.Validate(m.Token, cache, DateTimeOffset.UtcNow).Ok);
    }

    [Fact]
    public void Changes_through_the_cache_are_visible_immediately()
    {
        var cache = new CachingClientStore(new InMemoryClientStore(), TimeSpan.FromHours(1));
        Assert.Empty(cache.List());
        ClientTokens.Mint(cache, "x", null, DateTimeOffset.UtcNow);
        Assert.Single(cache.List());
        cache.Remove("x");
        Assert.Empty(cache.List());
    }
}

public class CertificateManagerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Default_sans_include_localhost_and_loopback()
    {
        var sans = CertificateManager.DefaultSans();
        Assert.Contains("localhost", sans);
        Assert.Contains("127.0.0.1", sans);
        Assert.Contains("::1", sans);
        Assert.Contains(Dns.GetHostName(), sans, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(sans.Count, sans.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Self_signed_certificate_carries_default_and_extra_sans()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wanted = CertificateManager.DefaultSans().Concat(new[] { "box.example.test", "192.0.2.7" });
        using var cert = CertificateManager.CreateSelfSigned(key, wanted, Now);
        var info = CertificateManager.Describe(cert);

        Assert.Contains("box.example.test", info.SubjectAlternativeNames);
        Assert.Contains("192.0.2.7", info.SubjectAlternativeNames);
        Assert.Contains("localhost", info.SubjectAlternativeNames);
        Assert.Contains("127.0.0.1", info.SubjectAlternativeNames);

        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(eku.EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");
        Assert.False(cert.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
    }

    [Fact]
    public void Expiry_helpers()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var soon = CertificateManager.CreateSelfSigned(key, new[] { "localhost" }, Now, TimeSpan.FromDays(10));
        var info = CertificateManager.Describe(soon);
        Assert.True(info.Remaining(Now) < CertificateManager.ExpiryWarning);
        Assert.True(info.Remaining(Now) > TimeSpan.Zero);
        Assert.True(info.Remaining(Now.AddDays(11)) < TimeSpan.Zero);
    }

    [Fact]
    public void Public_export_never_contains_a_private_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = CertificateManager.CreateSelfSigned(key, new[] { "localhost" }, Now);
        using var loaded = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        Assert.False(loaded.HasPrivateKey);
        Assert.DoesNotContain("PRIVATE KEY", cert.ExportCertificatePem());
    }
}

public class ServerPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wssearch-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void Config_round_trips_and_directory_is_restricted()
    {
        var paths = new ServerPaths(_root);
        new ServerConfig { CertThumbprint = "ABC", AllowedOrigins = { "https://x.test" } }.Save(paths);
        var loaded = ServerConfig.Load(paths);
        Assert.Equal("ABC", loaded.CertThumbprint);
        Assert.Equal(new[] { "https://x.test" }, loaded.AllowedOrigins);

        var rules = new DirectoryInfo(_root).GetAccessControl()
            .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
        Assert.True(new DirectoryInfo(_root).GetAccessControl().AreAccessRulesProtected);
        var sids = rules.Select(r => r.IdentityReference.Value).ToHashSet();
        Assert.Equal(2, sids.Count); // current user + SYSTEM only
        Assert.Contains("S-1-5-18", sids);
    }

    [Fact]
    public void Usage_tracker_throttles_and_persists()
    {
        var paths = new ServerPaths(_root);
        var tracker = new ClientUsageTracker(paths);
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.Touch("k1", t0);
        tracker.Touch("k1", t0.AddSeconds(10)); // throttled
        Assert.Equal(t0, tracker.Load()["k1"]);
        tracker.Touch("k1", t0.AddMinutes(2));
        Assert.Equal(t0.AddMinutes(2), new ClientUsageTracker(paths).Load()["k1"]);
    }
}
