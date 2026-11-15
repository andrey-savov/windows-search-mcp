using System.Net;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using WsSearchMcp.Http;
using WsSearchMcp.Tests.Files;
using Xunit;

namespace WsSearchMcp.Tests;

/// <summary>get_file end to end through the HTTPS server: auth middleware → client permission → tool.</summary>
public class HttpFileToolTests : IClassFixture<HttpServerFixture>
{
    private readonly HttpServerFixture _s;
    public HttpFileToolTests(HttpServerFixture s) => _s = s;

    private static JsonElement Result(string body)
    {
        // The Streamable HTTP reply is an SSE event ("data: {json}") or plain JSON.
        var line = body.Split('\n').FirstOrDefault(l => l.StartsWith("data:"));
        var json = line is null ? body : line["data:".Length..].Trim();
        return JsonDocument.Parse(json).RootElement.GetProperty("result").Clone();
    }

    private async Task<(HttpStatusCode status, JsonElement result)> CallAsync(string token, string path, long offset = 0, long length = 0, string? accept = null)
    {
        var args = accept is null
            ? JsonSerializer.Serialize(new { path, offset, length })
            : JsonSerializer.Serialize(new { path, offset, length, accept });
        var r = await _s.PostAsync(
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"get_file","arguments":""" + args + "}}", token);
        var body = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, r.IsSuccessStatusCode ? Result(body) : default);
    }

    private static bool IsError(JsonElement result) =>
        result.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;

    private static string ErrorText(JsonElement result) =>
        result.GetProperty("content")[0].GetProperty("text").GetString()!;

    [Fact]
    public async Task Tool_list_has_get_file_alongside_the_three_shared_tools()
    {
        var token = _s.Mint("list-tools");
        var r = await _s.PostAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", token);
        var tools = Result(await r.Content.ReadAsStringAsync()).GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "find_files", "get_file", "list_properties", "list_properties_by_family" }, tools);
    }

    [Fact]
    public async Task Client_without_file_permission_gets_a_tool_error_not_the_content()
    {
        var f = _s.Files.Text("plain.txt", "top secret body");
        var (status, result) = await CallAsync(_s.Mint("search-only"), f);
        Assert.Equal(HttpStatusCode.OK, status); // an MCP tool error is still a successful HTTP exchange
        Assert.True(IsError(result));
        Assert.Contains("not granted file access", ErrorText(result));
        Assert.DoesNotContain("top secret body", result.GetRawText());
    }

    [Fact]
    public async Task Client_with_file_permission_receives_the_file_text()
    {
        var f = _s.Files.Text(@"docs\hello.txt", "hello over https – wörld");
        var (status, result) = await CallAsync(_s.Mint("reader", files: true), f);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(IsError(result));
        var content = result.GetProperty("content");
        using var meta = JsonDocument.Parse(content[0].GetProperty("text").GetString()!);
        Assert.Equal(JsonValueKind.Null, meta.RootElement.GetProperty("nextOffset").ValueKind);
        Assert.Equal("hello over https – wörld", content[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Binary_content_arrives_as_a_base64_resource_matching_the_bytes()
    {
        var bytes = Enumerable.Range(0, 500).Select(i => (byte)(i * 13)).ToArray();
        var f = _s.Files.Bytes("blob.bin", bytes);
        var (_, result) = await CallAsync(_s.Mint("blob-reader", files: true), f, accept: "application/octet-stream");
        Assert.False(IsError(result));
        var res = result.GetProperty("content")[1];
        Assert.Equal("resource", res.GetProperty("type").GetString());
        Assert.Equal(bytes, Convert.FromBase64String(res.GetProperty("resource").GetProperty("blob").GetString()!));
    }

    [Fact]
    public async Task Chunks_can_be_requested_with_offset_and_length()
    {
        var f = _s.Files.Text("long.txt", "0123456789");
        var token = _s.Mint("pager", files: true);
        var (_, a) = await CallAsync(token, f, 0, 4);
        var (_, b) = await CallAsync(token, f, 4, 4);
        Assert.Equal("0123", a.GetProperty("content")[1].GetProperty("text").GetString());
        Assert.Equal("4567", b.GetProperty("content")[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Binary_file_with_the_default_accept_is_refused_with_guidance()
    {
        var f = _s.Files.Bytes("opaque.bin", new byte[] { 0, 1, 2, 3, 255 });
        var (status, result) = await CallAsync(_s.Mint("default-accept", files: true), f);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(IsError(result));
        Assert.Contains("cannot be delivered", ErrorText(result));
        Assert.Contains("accept", ErrorText(result));
    }

    [Fact]
    public async Task A_pdf_arrives_as_text_by_default_and_as_a_resource_when_accepted()
    {
        var pdf = DocumentFixtures.Pdf("Hello over https");
        var f = _s.Files.Bytes("doc.pdf", pdf);
        var token = _s.Mint("pdf-reader", files: true);

        var (_, text) = await CallAsync(token, f);
        Assert.False(IsError(text));
        using (var meta = JsonDocument.Parse(text.GetProperty("content")[0].GetProperty("text").GetString()!))
            Assert.Equal("converted-text", meta.RootElement.GetProperty("deliveredAs").GetString());
        Assert.Contains("Hello over https", text.GetProperty("content")[1].GetProperty("text").GetString());

        var (_, native) = await CallAsync(token, f, accept: "application/pdf");
        Assert.False(IsError(native));
        var res = native.GetProperty("content")[1];
        Assert.Equal("resource", res.GetProperty("type").GetString());
        Assert.Equal("application/pdf", res.GetProperty("resource").GetProperty("mimeType").GetString());
        Assert.Equal(pdf, Convert.FromBase64String(res.GetProperty("resource").GetProperty("blob").GetString()!));
    }

    [Fact]
    public async Task Tool_schema_exposes_accept_as_a_plain_string_and_the_description_carries_the_fallback()
    {
        var token = _s.Mint("schema-reader");
        var r = await _s.PostAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", token);
        var tools = Result(await r.Content.ReadAsStringAsync()).GetProperty("tools").EnumerateArray().ToList();
        var getFile = tools.Single(t => t.GetProperty("name").GetString() == "get_file");

        var accept = getFile.GetProperty("inputSchema").GetProperty("properties").GetProperty("accept");
        Assert.Equal("string", accept.GetProperty("type").GetString()); // no nullable type unions (Claude schema rule)
        Assert.Equal("text/*", accept.GetProperty("default").GetString());
        Assert.Contains("accept=\"text/*\"", getFile.GetProperty("description").GetString());
        Assert.DoesNotContain("accept", getFile.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData(@".git\config")]
    [InlineData(@"nested\.ssh\notes.txt")]
    [InlineData(@"keys\id_ed25519")]
    public async Task Denied_files_are_refused_even_for_a_client_with_file_permission(string relative)
    {
        var f = _s.Files.Text(relative, "do not leak");
        var (status, result) = await CallAsync(_s.Mint("denied-reader-" + Guid.NewGuid().ToString("N")[..8], files: true), f);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(IsError(result));
        Assert.Contains("denied by server policy", ErrorText(result));
        Assert.DoesNotContain("do not leak", result.GetRawText());
    }

    [Fact]
    public async Task Unauthenticated_call_never_reaches_the_tool()
    {
        var r = await _s.PostAsync("""{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"get_file","arguments":{"path":"C:\\x"}}}""", token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}

/// <summary>The stdio server must never expose get_file.</summary>
public class StdioToolSetTests
{
    private static IEnumerable<MethodInfo> ToolMethods(Assembly a) =>
        a.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
         .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    [Fact]
    public void The_shared_Core_assembly_registers_exactly_the_three_original_tools()
    {
        // The stdio exe registers tools only from this assembly (Program.cs: WithToolsFromAssembly(Core)).
        var names = ToolMethods(typeof(WindowsSearchTool).Assembly)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "ListProperties", "ListPropertiesByFamily", "find_files" }, names);
    }

    [Fact]
    public void get_file_lives_only_in_the_HTTP_assembly()
    {
        Assert.NotEqual(typeof(WindowsSearchTool).Assembly, typeof(FileTool).Assembly);
        Assert.Contains(ToolMethods(typeof(FileTool).Assembly), m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name == "get_file");
        Assert.DoesNotContain(ToolMethods(typeof(WindowsSearchTool).Assembly), m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name == "get_file");
    }

    [Fact]
    public void The_stdio_project_does_not_reference_the_HTTP_assembly()
    {
        var stdio = typeof(WindowsSearchTool).Assembly; // Core
        Assert.DoesNotContain(stdio.GetReferencedAssemblies(), r => r.Name == "ws-search-mcp-http");
    }
}
