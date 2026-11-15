using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using WsSearchMcp.Security;

namespace WsSearchMcp.Admin;

/// <summary>Command handling for ws-search-mcp-admin. Separated from Program so it can be tested.</summary>
public static class AdminCli
{
    public const string Usage = """
        ws-search-mcp-admin — manage clients and the TLS certificate of ws-search-mcp-http

        Run as the same Windows user as ws-search-mcp-http (both use that user's stores).

        Clients:
          client mint <name> [--expires 365d|never]   Create a client; prints its token ONCE
          client list [--json]                        List current (unrevoked) clients
          client revoke <name>                        Revoke a client (takes effect immediately)

        Server TLS certificate:
          cert init [--san <dns-or-ip>]... [--force]  Create a self-signed certificate
          cert show                                   Show thumbprint, SANs and expiry
          cert export <path>                          Write the PUBLIC certificate (.pem or .cer) for client trust
          cert renew [--san <dns-or-ip>]...           Replace the certificate (keeps SANs unless --san given)
          cert use <thumbprint>                       Use a CA-issued certificate from CurrentUser\My

        Client names: 1-64 characters; letters, digits, spaces, '-', '_' and '.'.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error,
        IClientStore store, ServerPaths paths, TimeProvider time)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "/?" or "help")
            {
                (args.Length == 0 ? error : output).WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }
            var rest = args.Skip(2).ToArray();
            return (args[0], args.Length > 1 ? args[1] : "") switch
            {
                ("client", "mint") => Mint(rest, output, error, store, time),
                ("client", "list") => List(rest, output, error, store, paths, time),
                ("client", "revoke") => Revoke(rest, output, error, store),
                ("cert", "init") => CertInit(rest, output, error, paths, time),
                ("cert", "show") => CertShow(output, error, paths, time),
                ("cert", "export") => CertExport(rest, output, error, paths),
                ("cert", "renew") => CertRenew(rest, output, error, paths, time),
                ("cert", "use") => CertUse(rest, output, error, paths, time),
                _ => Fail(error, $"Unknown command '{string.Join(' ', args.Take(2))}'.\n\n{Usage}"),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return Fail(error, ex.Message);
        }
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        return 2;
    }

    // ---- clients ---------------------------------------------------------------------------

    private static int Mint(string[] args, TextWriter output, TextWriter error, IClientStore store, TimeProvider time)
    {
        string? name = null;
        TimeSpan? lifetime = ClientTokens.DefaultLifetime;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--expires" && i + 1 < args.Length)
            {
                if (!TryParseLifetime(args[++i], out lifetime))
                    return Fail(error, "Invalid --expires value. Use e.g. 90d, 365d or never.");
            }
            else if (name is null && !args[i].StartsWith("--")) name = args[i];
            else return Fail(error, $"Unexpected argument '{args[i]}'.");
        }
        if (name is null) return Fail(error, "Usage: client mint <name> [--expires 365d|never]");

        var result = ClientTokens.Mint(store, name, lifetime, time.GetUtcNow());

        // Everything except the token goes to stderr so `mint` output can be captured cleanly.
        error.WriteLine($"Created client '{result.Client.Name}' (key {result.Client.Fingerprint}, " +
                        $"{(result.Client.Expires is { } e ? $"expires {e:yyyy-MM-dd}" : "never expires")}).");
        error.WriteLine("Token (shown once — it cannot be recovered; store it securely):");
        output.WriteLine(result.Token);
        return 0;
    }

    public static bool TryParseLifetime(string text, out TimeSpan? lifetime)
    {
        lifetime = null;
        if (text.Equals("never", StringComparison.OrdinalIgnoreCase)) return true;
        var m = Regex.Match(text, @"^(\d{1,5})d$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        var days = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (days is < 1 or > 36500) return false;
        lifetime = TimeSpan.FromDays(days);
        return true;
    }

    private static int List(string[] args, TextWriter output, TextWriter error,
        IClientStore store, ServerPaths paths, TimeProvider time)
    {
        var json = args.Contains("--json");
        if (args.Any(a => a != "--json")) return Fail(error, "Usage: client list [--json]");

        var now = time.GetUtcNow();
        var usage = new ClientUsageTracker(paths).Load();
        var clients = store.List().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        if (json)
        {
            var rows = clients.Select(c => new
            {
                name = c.Name,
                kid = c.Kid,
                created = c.Created,
                expires = c.Expires,
                expired = c.IsExpired(now),
                lastUsed = usage.TryGetValue(c.Kid, out var lu) ? (DateTimeOffset?)lu : null,
                fingerprint = c.Fingerprint,
            });
            output.WriteLine(JsonSerializer.Serialize(rows, ServerPaths.JsonOptions));
            return 0;
        }

        if (clients.Count == 0)
        {
            output.WriteLine("No clients.");
            return 0;
        }

        var table = clients.Select(c => new[]
        {
            c.Name,
            c.Created.ToString("yyyy-MM-dd"),
            c.Expires is { } e ? e.ToString("yyyy-MM-dd") + (c.IsExpired(now) ? " EXPIRED" : "") : "never",
            usage.TryGetValue(c.Kid, out var last) ? last.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never",
            c.Fingerprint,
        }).ToList();
        var headers = new[] { "NAME", "CREATED", "EXPIRES", "LAST USED", "KEY" };
        var widths = headers.Select((h, i) => Math.Max(h.Length, table.Max(r => r[i].Length))).ToArray();
        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();
        output.WriteLine(Line(headers));
        foreach (var row in table) output.WriteLine(Line(row));
        return 0;
    }

    private static int Revoke(string[] args, TextWriter output, TextWriter error, IClientStore store)
    {
        if (args.Length != 1) return Fail(error, "Usage: client revoke <name>");
        var existing = store.FindByName(args[0].Trim());
        if (existing is null || !store.Remove(existing.Name))
            return Fail(error, $"No client named '{args[0].Trim()}'. Use 'client list' to see current clients.");
        output.WriteLine($"Revoked client '{existing.Name}'.");
        return 0;
    }

    // ---- certificate -----------------------------------------------------------------------

    private static List<string> ParseSans(string[] args, out bool force, out string? bad)
    {
        var sans = new List<string>();
        force = false;
        bad = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--san" && i + 1 < args.Length) sans.Add(args[++i]);
            else if (args[i] == "--force") force = true;
            else bad = args[i];
        }
        return sans;
    }

    private static int CertInit(string[] args, TextWriter output, TextWriter error, ServerPaths paths, TimeProvider time)
    {
        var extra = ParseSans(args, out var force, out var bad);
        if (bad is not null) return Fail(error, $"Unexpected argument '{bad}'.");

        var config = ServerConfig.Load(paths);
        using (var existing = CertificateManager.Find(config.CertThumbprint))
        {
            if (existing is not null && !force)
                return Fail(error, $"A certificate is already configured ({existing.Thumbprint}). Use 'cert renew' to replace it, or 'cert init --force'.");
        }

        var sans = CertificateManager.Dedupe(CertificateManager.DefaultSans().Concat(extra));
        using var cert = CertificateManager.InstallNewSelfSigned(sans, time.GetUtcNow());
        config.CertThumbprint = cert.Thumbprint;
        config.Save(paths);
        Describe(output, CertificateManager.Describe(cert), time);
        output.WriteLine();
        output.WriteLine("Clients must trust this certificate. Export it with:");
        output.WriteLine("  ws-search-mcp-admin cert export server.pem");
        return 0;
    }

    private static int CertShow(TextWriter output, TextWriter error, ServerPaths paths, TimeProvider time)
    {
        var config = ServerConfig.Load(paths);
        using var cert = CertificateManager.Find(config.CertThumbprint);
        if (cert is null)
            return Fail(error, "No TLS certificate is configured. Run: ws-search-mcp-admin cert init");
        Describe(output, CertificateManager.Describe(cert), time);
        return 0;
    }

    private static int CertExport(string[] args, TextWriter output, TextWriter error, ServerPaths paths)
    {
        if (args.Length != 1) return Fail(error, "Usage: cert export <path.pem|path.cer>");
        var config = ServerConfig.Load(paths);
        using var cert = CertificateManager.Find(config.CertThumbprint);
        if (cert is null)
            return Fail(error, "No TLS certificate is configured. Run: ws-search-mcp-admin cert init");

        // Public certificate only; the private key is non-exportable and never written.
        if (args[0].EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
            File.WriteAllText(args[0], cert.ExportCertificatePem());
        else
            File.WriteAllBytes(args[0], cert.Export(X509ContentType.Cert));
        output.WriteLine($"Wrote public certificate {cert.Thumbprint} to {Path.GetFullPath(args[0])}");
        return 0;
    }

    private static int CertRenew(string[] args, TextWriter output, TextWriter error, ServerPaths paths, TimeProvider time)
    {
        var extra = ParseSans(args, out _, out var bad);
        if (bad is not null) return Fail(error, $"Unexpected argument '{bad}'.");

        var config = ServerConfig.Load(paths);
        using var old = CertificateManager.Find(config.CertThumbprint);
        IEnumerable<string> sans;
        if (extra.Count > 0) sans = CertificateManager.DefaultSans().Concat(extra);
        else if (old is not null) sans = CertificateManager.Describe(old).SubjectAlternativeNames;
        else sans = CertificateManager.DefaultSans();

        using var cert = CertificateManager.InstallNewSelfSigned(CertificateManager.Dedupe(sans), time.GetUtcNow());
        var oldThumbprint = old?.Thumbprint;
        config.CertThumbprint = cert.Thumbprint;
        config.Save(paths);
        if (oldThumbprint is not null && !string.Equals(oldThumbprint, cert.Thumbprint, StringComparison.OrdinalIgnoreCase))
            CertificateManager.Remove(oldThumbprint);

        Describe(output, CertificateManager.Describe(cert), time);
        output.WriteLine();
        output.WriteLine("Restart ws-search-mcp-http to use it, and re-export/re-trust the certificate on clients:");
        output.WriteLine("  ws-search-mcp-admin cert export server.pem");
        return 0;
    }

    private static int CertUse(string[] args, TextWriter output, TextWriter error, ServerPaths paths, TimeProvider time)
    {
        if (args.Length != 1) return Fail(error, "Usage: cert use <thumbprint>");
        using var cert = CertificateManager.Find(args[0]);
        if (cert is null) return Fail(error, $"No certificate with thumbprint {args[0]} in CurrentUser\\My.");
        if (!cert.HasPrivateKey) return Fail(error, "That certificate has no private key available to this user.");
        var info = CertificateManager.Describe(cert);
        if (info.NotAfter <= time.GetUtcNow()) return Fail(error, $"That certificate expired on {info.NotAfter:u}.");

        var config = ServerConfig.Load(paths);
        config.CertThumbprint = cert.Thumbprint;
        config.Save(paths);
        Describe(output, info, time);
        output.WriteLine();
        output.WriteLine("Restart ws-search-mcp-http to use it.");
        return 0;
    }

    private static void Describe(TextWriter output, CertificateInfo info, TimeProvider time)
    {
        var days = (int)info.Remaining(time.GetUtcNow()).TotalDays;
        output.WriteLine($"Thumbprint: {info.Thumbprint}");
        output.WriteLine($"Subject:    {info.Subject}");
        output.WriteLine($"Names:      {string.Join(", ", info.SubjectAlternativeNames)}");
        output.WriteLine($"Valid:      {info.NotBefore:yyyy-MM-dd} to {info.NotAfter:yyyy-MM-dd} ({(days >= 0 ? $"{days} days left" : "EXPIRED")})");
    }
}
