using WsSearchMcp.Security;

namespace WsSearchMcp.Files;

public enum DenyReason
{
    None,
    InvalidPath,
    DotComponent,
    HiddenOrSystem,
    ProtectedRoot,
    CredentialName,
    PrivateKey,
}

/// <summary>Result of a policy check. <see cref="Detail"/> is for server logs only, never sent to clients.</summary>
public readonly record struct PolicyDecision(DenyReason Reason, string? Detail = null)
{
    public bool Allowed => Reason == DenyReason.None;

    public static PolicyDecision Allow { get; } = new(DenyReason.None);

    /// <summary>Short, path-free description that is safe to show to the client.</summary>
    public string Category => Reason switch
    {
        DenyReason.None => "allowed",
        DenyReason.InvalidPath => "invalid path",
        DenyReason.DotComponent => "dot-prefixed file or folder name",
        DenyReason.HiddenOrSystem => "hidden or system file or folder",
        DenyReason.ProtectedRoot => "protected location",
        DenyReason.CredentialName => "credential or key file",
        DenyReason.PrivateKey => "private key file",
        _ => "denied",
    };
}

/// <summary>
/// The absolute deny list for file retrieval. It is structural and does not consult the Windows Search
/// index: a file being indexed never makes it readable. Denied, at any depth:
/// <list type="number">
/// <item>any path component starting with '.' (folders and files: .ssh, .git, .venv, .npmrc, ...);</item>
/// <item>any component that is Hidden or System on the real file system (AppData, ProgramData,
/// $Recycle.Bin, ...), checked up the whole path except the volume / share root;</item>
/// <item>anything under a protected root (the user's AppData, other users' AppData, ProgramData, Windows,
/// the server's own configuration folder), even if that folder is not hidden;</item>
/// <item>credential-style names (id_rsa*, *.pfx, *.p12, *.ppk, *.kdbx, NTUSER.DAT*);</item>
/// <item>.pem / .key files whose first 4 KiB contain "PRIVATE KEY".</item>
/// </list>
/// </summary>
public sealed class FileAccessPolicy
{
    public const int KeySniffBytes = 4096;

    private static readonly string[] CredentialExtensions = { ".pfx", ".p12", ".ppk", ".kdbx" };
    private static readonly string[] SshKeyPrefixes = { "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519" };
    private static readonly string[] SniffedExtensions = { ".pem", ".key" };

    private readonly string[] _protectedRoots;
    private readonly string[] _exemptBases;
    private readonly string? _usersRoot;

    /// <param name="protectedRoots">Folders (and everything below them) that are always denied.</param>
    public FileAccessPolicy(IEnumerable<string> protectedRoots)
        : this(protectedRoots, Array.Empty<string>(), null) { }

    /// <param name="exemptBases">
    /// Test seam: folders whose own location is not judged by the structural rules (only what lies
    /// below them is), so tests can build fixtures under a temp folder that sits inside AppData.
    /// Production policies have none.
    /// </param>
    /// <param name="usersRoot">Parent of the user profiles (C:\Users); every profile's AppData is protected.</param>
    internal FileAccessPolicy(IEnumerable<string> protectedRoots, IEnumerable<string> exemptBases, string? usersRoot)
    {
        _protectedRoots = protectedRoots.Select(TrimRoot).Where(r => r.Length > 0).ToArray();
        _exemptBases = exemptBases.Select(TrimRoot).Where(r => r.Length > 0).ToArray();
        _usersRoot = usersRoot is null ? null : TrimRoot(usersRoot);
    }

    /// <summary>The production policy for the current user.</summary>
    public static FileAccessPolicy CreateDefault(ServerPaths serverPaths)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>
        {
            Path.Combine(profile, "AppData"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), // ProgramData
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            serverPaths.Root,
        };
        var usersRoot = Path.GetDirectoryName(profile.TrimEnd('\\'));
        return new FileAccessPolicy(roots.Where(r => !string.IsNullOrWhiteSpace(r)), Array.Empty<string>(), usersRoot);
    }

    /// <summary>
    /// Normalizes a path for policy decisions: absolute, no <c>\\?\</c> prefix, no <c>..</c>, no
    /// alternate data stream or device path.
    /// </summary>
    public static PolicyDecision TryNormalize(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path)) return Invalid("empty path");
        if (path.Contains('\0')) return Invalid("NUL in path");

        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        else if (p.StartsWith(@"\\.\", StringComparison.Ordinal)) return Invalid("device path");

        if (!Path.IsPathFullyQualified(p)) return Invalid("path must be absolute");
        if (p.Split('\\').Any(s => s == "..")) return Invalid("'..' in path");

        string full;
        try { full = Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid("unparseable path");
        }

        var root = Path.GetPathRoot(full) ?? "";
        var searchFrom = root.StartsWith(@"\\", StringComparison.Ordinal) ? 0 : Math.Min(2, full.Length);
        if (full.IndexOf(':', searchFrom) >= 0) return Invalid("alternate data stream");

        normalized = full.Length > root.Length ? full.TrimEnd('\\') : full;
        return PolicyDecision.Allow;
    }

    /// <summary>Rules that only look at the path text (no file system access). Run before the index query.</summary>
    public PolicyDecision CheckPathText(string? path)
    {
        var d = TryNormalize(path, out var full);
        if (!d.Allowed) return d;

        foreach (var root in _protectedRoots)
            if (IsUnder(full, root))
                return new PolicyDecision(DenyReason.ProtectedRoot, $"under protected root '{root}'");

        // Every user profile's AppData, not just the current user's.
        if (_usersRoot is not null && IsUnder(full, _usersRoot))
        {
            var segs = full[(_usersRoot.Length)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length >= 2 && segs[1].Equals("AppData", StringComparison.OrdinalIgnoreCase))
                return new PolicyDecision(DenyReason.ProtectedRoot, "under another profile's AppData");
        }

        var components = JudgedComponents(full, out _);
        foreach (var c in components)
            if (c.StartsWith('.'))
                return new PolicyDecision(DenyReason.DotComponent, $"component '{c}'");

        var name = Path.GetFileName(full);
        if (name.StartsWith("NTUSER.DAT", StringComparison.OrdinalIgnoreCase))
            return new PolicyDecision(DenyReason.CredentialName, name);
        var ext = Path.GetExtension(name);
        if (CredentialExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return new PolicyDecision(DenyReason.CredentialName, name);
        if (SshKeyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            && !ext.Equals(".pub", StringComparison.OrdinalIgnoreCase))
            return new PolicyDecision(DenyReason.CredentialName, name);

        return PolicyDecision.Allow;
    }

    /// <summary>
    /// Denies if the file or any folder above it is Hidden or System on the real file system. The
    /// volume / share root (C:\ is Hidden+System) is not judged. An attribute that cannot be read is a denial.
    /// </summary>
    public PolicyDecision CheckAttributes(string? path)
    {
        var d = TryNormalize(path, out var full);
        if (!d.Allowed) return d;

        var root = (Path.GetPathRoot(full) ?? "").TrimEnd('\\');
        var stop = ExemptBaseFor(full) ?? root;

        // GetDirectoryName("C:\x") is "C:\" (trailing separator); compare without it so the root is never judged.
        for (var cur = full; cur is not null && cur.TrimEnd('\\').Length > stop.Length && IsUnder(cur, stop); cur = Path.GetDirectoryName(cur))
        {
            FileAttributes a;
            try { a = File.GetAttributes(cur); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return new PolicyDecision(DenyReason.HiddenOrSystem, $"attributes of '{cur}' unreadable");
            }
            if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                return new PolicyDecision(DenyReason.HiddenOrSystem, $"'{cur}' is {(a & (FileAttributes.Hidden | FileAttributes.System))}");
        }
        return PolicyDecision.Allow;
    }

    /// <summary>For .pem/.key files: denies if the first 4 KiB contain "PRIVATE KEY". Leaves the stream position unchanged.</summary>
    public PolicyDecision CheckContent(string? path, Stream stream)
    {
        var ext = Path.GetExtension(path ?? "");
        if (!SniffedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return PolicyDecision.Allow;

        var saved = stream.Position;
        try
        {
            stream.Position = 0;
            var head = new byte[KeySniffBytes];
            var n = 0;
            while (n < head.Length)
            {
                var r = stream.Read(head, n, head.Length - n);
                if (r == 0) break;
                n += r;
            }
            if (LooksLikePrivateKey(head.AsSpan(0, n)))
                return new PolicyDecision(DenyReason.PrivateKey, Path.GetFileName(path));
            return PolicyDecision.Allow;
        }
        finally { stream.Position = saved; }
    }

    public static bool LooksLikePrivateKey(ReadOnlySpan<byte> head) =>
        head.IndexOf("PRIVATE KEY"u8) >= 0;

    /// <summary>All rules for a path that has been opened: text, then attributes, then (if given) content.</summary>
    public PolicyDecision Check(string? path, Stream? openedStream = null)
    {
        var d = CheckPathText(path);
        if (!d.Allowed) return d;
        d = CheckAttributes(path);
        if (!d.Allowed) return d;
        return openedStream is null ? PolicyDecision.Allow : CheckContent(path, openedStream);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static PolicyDecision Invalid(string detail) => new(DenyReason.InvalidPath, detail);

    private static string TrimRoot(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        try { return Path.GetFullPath(p).TrimEnd('\\'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }

    /// <summary>True if <paramref name="path"/> equals <paramref name="root"/> or lies below it (case-insensitive).</summary>
    private static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || (path.Length > root.Length
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (path[root.Length] == '\\' || root.EndsWith('\\')));

    private string? ExemptBaseFor(string full) =>
        _exemptBases.Where(b => IsUnder(full, b)).OrderByDescending(b => b.Length).FirstOrDefault();

    /// <summary>The path components the structural rules judge: everything below the volume/share root and any exempt base.</summary>
    private string[] JudgedComponents(string full, out string stop)
    {
        var root = (Path.GetPathRoot(full) ?? "").TrimEnd('\\');
        stop = ExemptBaseFor(full) ?? root;
        return full.Length <= stop.Length
            ? Array.Empty<string>()
            : full[stop.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }
}
