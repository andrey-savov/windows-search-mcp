namespace WsSearchMcp.Security;

/// <summary>
/// Records when each client last authenticated, in usage.json, so <c>client list</c> can show it
/// without the server writing to the key store. Writes are throttled per client.
/// </summary>
public sealed class ClientUsageTracker
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly ServerPaths _paths;
    private readonly Dictionary<string, DateTimeOffset> _lastWritten = new();
    private readonly object _gate = new();

    public ClientUsageTracker(ServerPaths paths) => _paths = paths;

    public void Touch(string kid, DateTimeOffset now)
    {
        try
        {
            lock (_gate)
            {
                if (_lastWritten.TryGetValue(kid, out var last) && now - last < MinInterval) return;
                _lastWritten[kid] = now;
                var all = Load();
                all[kid] = now;
                _paths.EnsureSecureDirectory();
                ServerPaths.WriteJsonAtomic(_paths.UsageFile, all);
            }
        }
        catch (IOException) { /* usage tracking is best-effort */ }
        catch (UnauthorizedAccessException) { }
    }

    public Dictionary<string, DateTimeOffset> Load()
    {
        try
        {
            return ServerPaths.ReadJson<Dictionary<string, DateTimeOffset>>(_paths.UsageFile)
                   ?? new Dictionary<string, DateTimeOffset>();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return new Dictionary<string, DateTimeOffset>();
        }
    }
}
