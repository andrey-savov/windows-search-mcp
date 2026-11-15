using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WsSearchMcp.Files;
using WsSearchMcp.Http.Conversion;
using WsSearchMcp.Security;

namespace WsSearchMcp.Http;

public sealed class McpHttpServerOptions
{
    public required IClientStore Store { get; init; }
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>TCP port; 8765 by default.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// "any" (all interfaces, IPv4 + IPv6), "localhost" (loopback only, IPv4 + IPv6), or a specific IP address.
    /// </summary>
    public string Bind { get; init; } = "any";

    /// <summary>True if <paramref name="bind"/> is "any", "localhost" or an IP address literal.</summary>
    public static bool IsValidBind(string? bind) =>
        bind is not null
        && (bind.Equals("any", StringComparison.OrdinalIgnoreCase)
            || bind.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(bind, out _));

    /// <summary>Browser origins that may call the server. Empty by default: any request with an Origin header is rejected.</summary>
    public IReadOnlyCollection<string> AllowedOrigins { get; init; } = Array.Empty<string>();

    public ClientUsageTracker? Usage { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// Per-call cap of the get_file tool in bytes (200 MiB by default). Raise it for larger single reads;
    /// note that a reply holds the bytes plus their base64/JSON forms in memory at once.
    /// </summary>
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;

    /// <summary>Location of the server's own config folder (always denied to get_file). Default: the per-user folder.</summary>
    public ServerPaths? Paths { get; init; }

    /// <summary>Overrides the get_file deny list (tests only). Default: <see cref="FileAccessPolicy.CreateDefault"/>.</summary>
    public FileAccessPolicy? FilePolicy { get; init; }

    /// <summary>Overrides the Windows Search lookup used by get_file (tests only). Default: the real index.</summary>
    public IndexedFileResolver? FileResolver { get; init; }

    /// <summary>Limits for converting PDF/Office files to text (input size, expansion, timeout, cache).</summary>
    public ConversionOptions Conversion { get; init; } = new();

    /// <summary>Overrides the converters (tests only). Default: PDF and Office Open XML.</summary>
    public IReadOnlyList<IContentConverter>? Converters { get; init; }

    public const long DefaultMaxFileBytes = 200L * 1024 * 1024;
    public const int DefaultPort = 8765;
    public const long MaxRequestBodyBytes = 1024 * 1024;
}

/// <summary>
/// HTTPS-only, stateless MCP Streamable HTTP host. Every request is authenticated with a client
/// bearer token before it reaches the MCP endpoint; there is no plaintext listener.
/// </summary>
public static class McpHttpServer
{
    /// <summary><c>HttpContext.Items</c> key holding the authenticated <see cref="ClientRecord"/>.</summary>
    public const string ClientItemKey = "ws-search.client";

    public static WebApplication Build(McpHttpServerOptions options)
    {
        if (options.MaxFileBytes <= 0)
            throw new ArgumentException("MaxFileBytes must be positive.", nameof(options));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(), // flags are parsed by Program, never by host configuration
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = McpHttpServerOptions.MaxRequestBodyBytes;
            void UseTls(ListenOptions lo) => lo.UseHttps(options.Certificate);

            if (!McpHttpServerOptions.IsValidBind(options.Bind))
                throw new ArgumentException($"Invalid bind '{options.Bind}': use 'any', 'localhost' or an IP address.", nameof(options));

            if (string.Equals(options.Bind, "any", StringComparison.OrdinalIgnoreCase))
                kestrel.ListenAnyIP(options.Port, UseTls);
            else if (string.Equals(options.Bind, "localhost", StringComparison.OrdinalIgnoreCase))
                kestrel.ListenLocalhost(options.Port, UseTls); // 127.0.0.1 and ::1
            else
                kestrel.Listen(IPAddress.Parse(options.Bind), options.Port, UseTls);
        });

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(options.FilePolicy ?? FileAccessPolicy.CreateDefault(options.Paths ?? new ServerPaths()));
        builder.Services.AddSingleton(options.FileResolver ?? IndexedFileResolver.CreateDefault());
        builder.Services.AddSingleton(options.Converters is { } converters
            ? new ConversionService(converters, options.Conversion, options.MaxFileBytes)
            : ConversionService.CreateDefault(options.Conversion, options.MaxFileBytes));

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithToolsFromAssembly(typeof(WindowsSearchTool).Assembly) // search + property tools (shared with stdio)
            .WithTools<FileTool>();                                    // get_file: HTTPS only, never registered by stdio

        var app = builder.Build();
        var log = app.Logger;
        var allowedOrigins = new HashSet<string>(options.AllowedOrigins, StringComparer.OrdinalIgnoreCase);

        app.Use(async (ctx, next) =>
        {
            var remote = ctx.Connection.RemoteIpAddress;

            // DNS-rebinding / cross-site protection: browsers always send Origin.
            if (ctx.Request.Headers.TryGetValue("Origin", out var origin) && !allowedOrigins.Contains(origin.ToString()))
            {
                log.LogWarning("Rejected request from {Remote}: browser origin not allowed", remote);
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            string? token = null;
            var header = ctx.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = header[7..].Trim();

            var now = options.Time.GetUtcNow();
            var result = ClientTokens.Validate(token, options.Store, now);
            if (!result.Ok)
            {
                // The reason is one of a fixed set of strings; the presented token is never logged.
                log.LogWarning("Authentication failed from {Remote}: {Reason}", remote, result.Failure);
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            ctx.Items[ClientItemKey] = result.Client;
            options.Usage?.Touch(result.Client!.Kid, now);
            log.LogInformation("Client '{Client}' {Method} {Path}", result.Client!.Name, ctx.Request.Method, ctx.Request.Path);
            await next();
        });

        app.MapMcp();
        return app;
    }
}
