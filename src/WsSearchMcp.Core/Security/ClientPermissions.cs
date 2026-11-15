namespace WsSearchMcp.Security;

/// <summary>
/// Permissions a client can be minted with. They are stored server-side with the client's public
/// key (not in the token), so the server record is authoritative and tokens issued before a
/// permission existed simply do not have it.
/// </summary>
public static class ClientPermissions
{
    /// <summary>May retrieve file contents with the HTTPS-only <c>get_file</c> tool.</summary>
    public const string Files = "files";

    public static readonly IReadOnlyList<string> All = new[] { Files };

    /// <summary>Prefix of the certificate-subject RDN that carries a permission: <c>OU=perm:files</c>.</summary>
    public const string SubjectPrefix = "perm:";

    public static bool IsKnown(string permission) =>
        All.Contains(permission, StringComparer.OrdinalIgnoreCase);

    /// <summary>"search" or "search+files" — the ACCESS column of <c>client list</c>.</summary>
    public static string Describe(IEnumerable<string> permissions) =>
        string.Join("+", new[] { "search" }.Concat(permissions.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)));
}
