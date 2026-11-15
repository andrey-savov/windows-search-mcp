using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using WsSearchMcp.Files;
using WsSearchMcp.Http;
using WsSearchMcp.Security;
using WsSearchMcp.Tests.Files;
using Xunit;

namespace WsSearchMcp.Tests;

/// <summary>
/// In-process HTTPS server on an ephemeral port with a throwaway certificate and an in-memory
/// client store. Only list_properties is dispatched — it needs no Windows Search service/COM.
/// </summary>
public sealed class HttpServerFixture : IAsyncLifetime
{
    public InMemoryClientStore Store { get; } = new();

    /// <summary>Files served by get_file in the integration tests; every file here counts as "indexed".</summary>
    public TempTree Files { get; } = new();
    public int Port { get; private set; }
    public string Base => $"https://localhost:{Port}/";
    public HttpClient Http { get; private set; } = null!;
    private WebApplication _app = null!;
    private X509Certificate2 _cert = null!;

    public async Task InitializeAsync()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var ephemeral = CertificateManager.CreateSelfSigned(key, new[] { "localhost", "127.0.0.1" }, DateTimeOffset.UtcNow);
        // Round-trip through PKCS#12 so Schannel gets a usable (non-ephemeral) key handle.
        _cert = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx, "t"), "t");

        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _app = McpHttpServer.Build(new McpHttpServerOptions
        {
            Store = Store,
            Certificate = _cert,
            Port = Port,
            Bind = "127.0.0.1",
            AllowedOrigins = new[] { "https://allowed.example" },
            FilePolicy = Files.Policy(),
            FileResolver = new FakeIndex().Resolver,
        });
        await _app.StartAsync();

        var thumb = _cert.Thumbprint;
        Http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == thumb,
        });
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _cert.Dispose();
        Files.Dispose();
    }

    public string Mint(string name, bool files = false) =>
        ClientTokens.Mint(Store, name, TimeSpan.FromDays(1), DateTimeOffset.UtcNow,
            files ? new[] { ClientPermissions.Files } : null).Token;

    public async Task<HttpResponseMessage> PostAsync(string json, string? token, string? origin = null, string? authScheme = "Bearer")
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Base)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null) req.Headers.TryAddWithoutValidation("Authorization", $"{authScheme} {token}");
        if (origin is not null) req.Headers.TryAddWithoutValidation("Origin", origin);
        return await Http.SendAsync(req);
    }

    public const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";
}

public class HttpServerTests : IClassFixture<HttpServerFixture>
{
    private readonly HttpServerFixture _s;
    public HttpServerTests(HttpServerFixture s) => _s = s;

    [Fact]
    public async Task No_token_is_401_with_bearer_challenge()
    {
        var r = await _s.PostAsync(HttpServerFixture.Initialize, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains(r.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    public async Task Malformed_token_is_401(string token) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _s.PostAsync(HttpServerFixture.Initialize, token)).StatusCode);

    [Fact]
    public async Task Wrong_scheme_is_401()
    {
        var token = _s.Mint("scheme-test");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _s.PostAsync(HttpServerFixture.Initialize, token, authScheme: "Basic")).StatusCode);
    }

    [Fact]
    public async Task Token_signed_by_an_unregistered_key_is_401()
    {
        // Mint against a different store: well-formed, validly signed, but unknown to this server.
        var foreign = ClientTokens.Mint(new InMemoryClientStore(), "foreign", null, DateTimeOffset.UtcNow).Token;
        Assert.Equal(HttpStatusCode.Unauthorized, (await _s.PostAsync(HttpServerFixture.Initialize, foreign)).StatusCode);
    }

    [Fact]
    public async Task Tampered_token_is_401()
    {
        var token = _s.Mint("tamper-test");
        var parts = token.Split('.');
        var bad = parts[0] + "." + parts[1] + "." + new string(parts[2].Reverse().ToArray());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _s.PostAsync(HttpServerFixture.Initialize, bad)).StatusCode);
    }

    [Fact]
    public async Task Revocation_takes_effect_without_restarting_the_server()
    {
        var token = _s.Mint("revoke-me");
        Assert.Equal(HttpStatusCode.OK, (await _s.PostAsync(HttpServerFixture.Initialize, token)).StatusCode);

        Assert.True(_s.Store.Remove("revoke-me"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _s.PostAsync(HttpServerFixture.Initialize, token)).StatusCode);
    }

    [Fact]
    public async Task Valid_token_can_initialize_list_tools_and_call_a_tool()
    {
        var token = _s.Mint("happy-path");

        var init = await _s.PostAsync(HttpServerFixture.Initialize, token);
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        Assert.Contains("\"result\"", await init.Content.ReadAsStringAsync());

        var list = await _s.PostAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", token);
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains("find_files", listBody);
        Assert.Contains("list_properties", listBody);
        Assert.Contains("list_properties_by_family", listBody);

        var call = await _s.PostAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_properties","arguments":{"filter":"System.DateModified","maxResults":3}}}""",
            token);
        var callBody = await call.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
        Assert.Contains("System.DateModified", callBody);
    }

    [Fact]
    public async Task Browser_origin_is_rejected_unless_allow_listed()
    {
        var token = _s.Mint("origin-test");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _s.PostAsync(HttpServerFixture.Initialize, token, origin: "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _s.PostAsync(HttpServerFixture.Initialize, token, origin: "https://allowed.example")).StatusCode);
    }

    [Fact]
    public async Task Plain_http_to_the_port_does_not_work()
    {
        using var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var url = $"http://localhost:{_s.Port}/";
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var r = await plain.PostAsync(url, new StringContent(HttpServerFixture.Initialize, Encoding.UTF8, "application/json"));
            // If anything answered at all it must not be a successful MCP response.
            r.EnsureSuccessStatusCode();
        });
    }

    [Fact]
    public async Task Untrusted_certificate_is_not_accepted_by_default_clients()
    {
        using var strict = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAsync<HttpRequestException>(() => strict.GetAsync(_s.Base));
    }

    [Fact]
    public async Task Oversized_body_is_refused()
    {
        var token = _s.Mint("big-body");
        var huge = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"x\":\"" +
                   new string('a', (int)McpHttpServerOptions.MaxRequestBodyBytes + 1024) + "\"}}";
        // Kestrel answers 413, but it may also drop the connection while the client is still sending the
        // oversized body. Either way the request must not be processed.
        try
        {
            var r = await _s.PostAsync(huge, token);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        }
        catch (HttpRequestException) { /* connection closed by the server */ }
    }
}

public class HttpServerBindTests
{
    [Theory]
    [InlineData("any")]
    [InlineData("ANY")]
    [InlineData("localhost")]
    [InlineData("LocalHost")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    public void Valid_bind_values_are_accepted(string bind) => Assert.True(McpHttpServerOptions.IsValidBind(bind));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("example.com")]
    [InlineData("ultra")]
    [InlineData("999.1.1.1")]
    public void Other_bind_values_are_rejected(string? bind) => Assert.False(McpHttpServerOptions.IsValidBind(bind));

    [Fact]
    public async Task Invalid_bind_is_refused_when_building_the_server()
    {
        using var cert = MakeCert();
        var options = new McpHttpServerOptions { Store = new InMemoryClientStore(), Certificate = cert, Port = FreePort(), Bind = "ultra" };
        // The listener is configured when the host is built/started, so the error surfaces by then at the latest.
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            var app = McpHttpServer.Build(options);
            await app.StartAsync();
        });
    }

    [Fact]
    public async Task Localhost_bind_answers_on_loopback_names_and_addresses()
    {
        using var cert = MakeCert();
        var port = FreePort();
        var app = McpHttpServer.Build(new McpHttpServerOptions
        {
            Store = new InMemoryClientStore(), Certificate = cert, Port = port, Bind = "localhost",
        });
        await app.StartAsync();
        try
        {
            var thumb = cert.Thumbprint;
            using var http = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == thumb,
            }) { Timeout = TimeSpan.FromSeconds(10) };

            var hosts = new List<string> { "localhost", "127.0.0.1" };
            if (Socket.OSSupportsIPv6) hosts.Add("[::1]");
            foreach (var host in hosts)
            {
                // No token: reaching the auth middleware (401) proves the TLS listener answered on that address.
                var r = await http.GetAsync($"https://{host}:{port}/");
                Assert.True(HttpStatusCode.Unauthorized == r.StatusCode, $"{host} returned {(int)r.StatusCode}");
            }
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Localhost_bind_is_not_reachable_via_a_non_loopback_address()
    {
        var external = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (external is null) return; // no network interface to probe from

        using var cert = MakeCert();
        var port = FreePort();
        var app = McpHttpServer.Build(new McpHttpServerOptions
        {
            Store = new InMemoryClientStore(), Certificate = cert, Port = port, Bind = "localhost",
        });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            }) { Timeout = TimeSpan.FromSeconds(5) };
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync($"https://{external}:{port}/"));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static X509Certificate2 MakeCert()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var ephemeral = CertificateManager.CreateSelfSigned(key, new[] { "localhost", "127.0.0.1", "::1" }, DateTimeOffset.UtcNow);
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx, "t"), "t");
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
