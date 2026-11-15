using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using WsSearchMcp.Http;
using WsSearchMcp.Security;
using Xunit;

namespace WsSearchMcp.Tests;

/// <summary>
/// In-process HTTPS server on an ephemeral port with a throwaway certificate and an in-memory
/// client store. Only list_properties is dispatched — it needs no Windows Search service/COM.
/// </summary>
public sealed class HttpServerFixture : IAsyncLifetime
{
    public InMemoryClientStore Store { get; } = new();
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
    }

    public string Mint(string name) =>
        ClientTokens.Mint(Store, name, TimeSpan.FromDays(1), DateTimeOffset.UtcNow).Token;

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
        var r = await _s.PostAsync(huge, token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
    }
}
