using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace WsSearchMcp.Security;

/// <summary>Per-user server configuration, kept in a directory only the user (and SYSTEM) can access.</summary>
public sealed class ServerPaths
{
    public string Root { get; }
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string UsageFile => Path.Combine(Root, "usage.json");

    public ServerPaths(string? root = null)
    {
        Root = root
            ?? Environment.GetEnvironmentVariable("WS_SEARCH_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ws-search-mcp");
    }

    /// <summary>Creates the directory if needed and restricts it to the current user and SYSTEM.</summary>
    public void EnsureSecureDirectory()
    {
        var dir = Directory.CreateDirectory(Root);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        foreach (var sid in new[] { user, system })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        dir.SetAccessControl(security);
    }

    /// <summary>Atomically writes JSON to <paramref name="path"/> (temp file + replace).</summary>
    public static void WriteJsonAtomic<T>(string path, T value)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    public static T? ReadJson<T>(string path)
    {
        if (!File.Exists(path)) return default;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}

public sealed class ServerConfig
{
    /// <summary>Thumbprint of the server TLS certificate in CurrentUser\My.</summary>
    public string? CertThumbprint { get; set; }

    /// <summary>Browser origins allowed to call the server. Empty by default (all browser origins rejected).</summary>
    public List<string> AllowedOrigins { get; set; } = new();

    /// <summary>Per-call cap for the get_file tool in bytes. Unset = the server default (200 MiB).</summary>
    public long? MaxFileBytes { get; set; }

    /// <summary>Largest file get_file will convert to text (PDF/Office), in bytes. Unset = 50 MiB.</summary>
    public long? MaxConvertBytes { get; set; }

    /// <summary>Wall-clock limit of one get_file text conversion, in seconds. Unset = 60.</summary>
    public int? ConvertTimeoutSeconds { get; set; }

    public static ServerConfig Load(ServerPaths paths) =>
        ServerPaths.ReadJson<ServerConfig>(paths.ConfigFile) ?? new ServerConfig();

    public void Save(ServerPaths paths)
    {
        paths.EnsureSecureDirectory();
        ServerPaths.WriteJsonAtomic(paths.ConfigFile, this);
    }
}
