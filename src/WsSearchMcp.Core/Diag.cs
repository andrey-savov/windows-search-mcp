namespace WsSearchMcp;

/// <summary>
/// Best-effort diagnostic logging, off unless the WS_SEARCH_DIAG environment
/// variable is set. When enabled, appends to ws-search-diag.log next to the
/// executable. Used to investigate the process/COM context across MCP hosts
/// (e.g. Claude Code vs Claude Desktop). Never throws.
/// </summary>
internal static class Diag
{
    private static readonly bool Enabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WS_SEARCH_DIAG"));

    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "ws-search-diag.log");

    private static readonly object Gate = new();

    public static void Log(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:O} pid={Environment.ProcessId} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never affect tool behavior.
        }
    }
}
