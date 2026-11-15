using System.Net;
using WsSearchMcp.Http;
using WsSearchMcp.Security;

const string Usage = """
    ws-search-mcp-http — authenticated HTTPS MCP server for Windows Search

    Usage: ws-search-mcp-http [--port N] [--bind ADDR] [--max-file-mb N]

      --port N      TCP port (default 8765)
      --bind ADDR   "any" (all interfaces, default), "localhost" (loopback only: 127.0.0.1 and ::1),
                    or a specific IP address
      --max-file-mb N   per-call cap of the get_file tool in MiB (default 200, or maxFileBytes in config.json).
                    A reply holds the bytes plus their base64/JSON forms in memory, so raise with care.

    Run as the Windows user whose search index should be served. Manage clients and the TLS
    certificate with ws-search-mcp-admin.
    """;

var port = McpHttpServerOptions.DefaultPort;
var bind = "any";
long? maxFileBytes = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-h" or "--help" or "/?":
            Console.WriteLine(Usage);
            return 0;
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p) && p is > 0 and < 65536:
            port = p; i++;
            break;
        case "--max-file-mb" when i + 1 < args.Length && long.TryParse(args[i + 1], out var mb) && mb is > 0 and <= 1_000_000:
            maxFileBytes = mb * 1024 * 1024; i++;
            break;
        case "--bind" when i + 1 < args.Length:
            bind = args[++i];
            if (!McpHttpServerOptions.IsValidBind(bind))
            {
                Console.Error.WriteLine($"Invalid --bind '{bind}': use \"any\", \"localhost\" or an IP address.");
                return 2;
            }
            break;
        default:
            Console.Error.WriteLine($"Unknown or invalid argument '{args[i]}'.\n\n{Usage}");
            return 2;
    }
}

var paths = new ServerPaths();
var config = ServerConfig.Load(paths);
using var cert = CertificateManager.Find(config.CertThumbprint);
if (cert is null)
{
    Console.Error.WriteLine(config.CertThumbprint is null
        ? "No TLS certificate is configured. Run: ws-search-mcp-admin cert init"
        : $"Configured TLS certificate {config.CertThumbprint} was not found in CurrentUser\\My. Run: ws-search-mcp-admin cert init (or cert use <thumbprint>)");
    return 2;
}
if (!cert.HasPrivateKey)
{
    Console.Error.WriteLine("The configured TLS certificate has no private key available to this user.");
    return 2;
}

var info = CertificateManager.Describe(cert);
var now = DateTimeOffset.UtcNow;
if (info.NotAfter <= now)
{
    Console.Error.WriteLine($"The TLS certificate expired on {info.NotAfter:u}. Run: ws-search-mcp-admin cert renew");
    return 2;
}
if (info.Remaining(now) < CertificateManager.ExpiryWarning)
    Console.Error.WriteLine($"WARNING: the TLS certificate expires in {(int)info.Remaining(now).TotalDays} days ({info.NotAfter:u}). Run: ws-search-mcp-admin cert renew");

var store = new CachingClientStore(new WindowsCertClientStore(), TimeSpan.FromSeconds(3));
var fileCap = maxFileBytes ?? config.MaxFileBytes ?? McpHttpServerOptions.DefaultMaxFileBytes;
if (fileCap <= 0)
{
    Console.Error.WriteLine("maxFileBytes in config.json must be greater than 0.");
    return 2;
}

var conversion = new WsSearchMcp.Http.Conversion.ConversionOptions();
if (config.MaxConvertBytes is { } maxConvert)
{
    if (maxConvert <= 0)
    {
        Console.Error.WriteLine("maxConvertBytes in config.json must be greater than 0.");
        return 2;
    }
    conversion = new WsSearchMcp.Http.Conversion.ConversionOptions
    {
        MaxInputBytes = maxConvert,
        MaxExpandedBytes = Math.Max(conversion.MaxExpandedBytes, maxConvert * 4),
    };
}
if (config.ConvertTimeoutSeconds is { } timeoutSeconds)
{
    if (timeoutSeconds <= 0)
    {
        Console.Error.WriteLine("convertTimeoutSeconds in config.json must be greater than 0.");
        return 2;
    }
    conversion = new WsSearchMcp.Http.Conversion.ConversionOptions
    {
        MaxInputBytes = conversion.MaxInputBytes,
        MaxExpandedBytes = conversion.MaxExpandedBytes,
        CacheBytes = conversion.CacheBytes,
        Timeout = TimeSpan.FromSeconds(timeoutSeconds),
    };
}

var app = McpHttpServer.Build(new McpHttpServerOptions
{
    Conversion = conversion,
    Store = store,
    Certificate = cert,
    Port = port,
    Bind = bind,
    AllowedOrigins = config.AllowedOrigins,
    Usage = new ClientUsageTracker(paths),
    Paths = paths,
    MaxFileBytes = fileCap,
});

Console.WriteLine($"ws-search-mcp-http: https://{(bind == "any" ? "<host>" : bind)}:{port}/  " +
                  $"(cert {info.Thumbprint}, expires {info.NotAfter:yyyy-MM-dd}, {store.List().Count} client(s), get_file cap {fileCap / (1024 * 1024)} MiB, running as {Environment.UserDomainName}\\{Environment.UserName})");

try
{
    await app.RunAsync();
}
catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
{
    Console.Error.WriteLine($"Cannot listen on port {port}: {ex.InnerException.Message}");
    return 1;
}
return 0;
