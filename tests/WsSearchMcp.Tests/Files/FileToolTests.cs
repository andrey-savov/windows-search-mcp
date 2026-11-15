using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WsSearchMcp.Files;
using WsSearchMcp.Http;
using WsSearchMcp.Security;
using Xunit;

namespace WsSearchMcp.Tests.Files;

public class FileToolTests : IDisposable
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly TempTree _t = new();
    private readonly FakeIndex _index = new();

    public void Dispose() => _t.Dispose();

    private FileTool Tool(ClientRecord? client, long cap = McpHttpServerOptions.DefaultMaxFileBytes, FileAccessPolicy? policy = null)
    {
        var ctx = new DefaultHttpContext();
        if (client is not null) ctx.Items[McpHttpServer.ClientItemKey] = client;
        var options = new McpHttpServerOptions
        {
            Store = new InMemoryClientStore(), Certificate = TestClients.PublicCert(), MaxFileBytes = cap,
        };
        return new FileTool(new HttpContextAccessor { HttpContext = ctx }, options, policy ?? _t.Policy(),
            _index.Resolver, NullLogger<FileTool>.Instance);
    }

    private FileTool WithFiles(long cap = McpHttpServerOptions.DefaultMaxFileBytes) => Tool(TestClients.Make(files: true), cap);

    private static JsonElement Meta(IEnumerable<ContentBlock> blocks)
    {
        var json = ((TextContentBlock)blocks.First()).Text;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string TextOf(IEnumerable<ContentBlock> blocks) => ((TextContentBlock)blocks.ElementAt(1)).Text;

    // ---- permission ----------------------------------------------------------------------

    [Fact]
    public async Task Client_without_the_files_permission_is_refused_before_any_lookup()
    {
        var f = _t.Text("a.txt", "secret");
        var ex = await Assert.ThrowsAsync<McpException>(() => Tool(TestClients.Make(files: false)).GetFile(f));
        Assert.Contains("not granted file access", ex.Message);
        Assert.Equal(0, _index.Calls);
    }

    [Fact]
    public async Task Unauthenticated_call_is_refused()
    {
        var f = _t.Text("a.txt", "x");
        var ex = await Assert.ThrowsAsync<McpException>(() => Tool(null).GetFile(f));
        Assert.Contains("Not authenticated", ex.Message);
    }

    // ---- reading -------------------------------------------------------------------------

    [Fact]
    public async Task Small_text_file_is_returned_whole_with_metadata()
    {
        var f = _t.Text(@"docs\notes.txt", "hello\nwörld €");
        var blocks = (await WithFiles().GetFile(f)).ToList();

        var m = Meta(blocks);
        Assert.Equal(f, m.GetProperty("path").GetString(), ignoreCase: true);
        Assert.Equal(new FileInfo(f).Length, m.GetProperty("size").GetInt64());
        Assert.Equal("text/plain", m.GetProperty("mimeType").GetString());
        Assert.Equal("utf-8", m.GetProperty("encoding").GetString());
        Assert.Equal(0, m.GetProperty("offset").GetInt64());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("nextOffset").ValueKind);
        Assert.False(m.GetProperty("truncated").GetBoolean());
        Assert.Equal("hello\nwörld €", TextOf(blocks));
    }

    [Fact]
    public async Task Utf8_file_can_be_paged_with_small_lengths_and_reassembles_exactly()
    {
        var text = string.Concat(Enumerable.Repeat("héllo wörld € 😀 ", 25));
        var f = _t.Text("big.txt", text);
        var sb = new StringBuilder();
        long offset = 0;
        for (var guard = 0; guard < 1000; guard++)
        {
            var blocks = (await WithFiles().GetFile(f, offset, 7)).ToList();
            var m = Meta(blocks);
            Assert.True(m.GetProperty("returned").GetInt64() <= 7);
            sb.Append(TextOf(blocks));
            if (m.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
            offset = m.GetProperty("nextOffset").GetInt64();
        }
        Assert.Equal(text, sb.ToString());
    }

    [Fact]
    public async Task Utf16_file_is_text_not_base64_and_pages_on_even_boundaries_without_splitting_surrogates()
    {
        var text = string.Concat(Enumerable.Repeat("ab😀é ", 20));
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
        var f = _t.Bytes("u16.txt", bytes);

        var first = (await WithFiles().GetFile(f, 0, 9)).ToList();
        Assert.Equal("utf-16le", Meta(first).GetProperty("encoding").GetString());

        var sb = new StringBuilder();
        long offset = 0;
        for (var guard = 0; guard < 1000; guard++)
        {
            var blocks = (await WithFiles().GetFile(f, offset, 9)).ToList();
            var m = Meta(blocks);
            Assert.Equal(0, m.GetProperty("returned").GetInt64() % 2);
            sb.Append(TextOf(blocks));
            if (m.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
            offset = m.GetProperty("nextOffset").GetInt64();
        }
        Assert.Equal(text, sb.ToString()); // BOM dropped, nothing split
    }

    [Fact]
    public async Task Odd_offset_into_a_utf16_file_is_refused()
    {
        var f = _t.Bytes("u16.txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("abcdef")).ToArray());
        var ex = await Assert.ThrowsAsync<McpException>(() => WithFiles().GetFile(f, 3));
        Assert.Contains("even", ex.Message);
    }

    [Fact]
    public async Task Binary_file_comes_back_as_a_base64_blob_that_matches_the_bytes()
    {
        var bytes = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();
        var f = _t.Bytes("data.bin", bytes);
        var blocks = (await WithFiles().GetFile(f)).ToList();

        var blob = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(blocks[1]).Resource);
        Assert.Equal(bytes, blob.DecodedData.ToArray());
        Assert.Equal("application/octet-stream", blob.MimeType);
        Assert.StartsWith("file:///", blob.Uri);
        Assert.Equal("base64", Meta(blocks).GetProperty("encoding").GetString());
    }

    [Fact]
    public async Task Complete_image_is_an_image_block_but_a_partial_image_is_a_blob()
    {
        var f = _t.Bytes("pic.png", TinyPng);
        var whole = (await WithFiles().GetFile(f)).ToList();
        var img = Assert.IsType<ImageContentBlock>(whole[1]);
        Assert.Equal("image/png", img.MimeType);
        Assert.Equal(TinyPng, img.DecodedData.ToArray());

        var part = (await WithFiles().GetFile(f, 0, 10)).ToList();
        var blob = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(part[1]).Resource);
        Assert.Equal(TinyPng.Take(10).ToArray(), blob.DecodedData.ToArray());
        Assert.True(Meta(part).GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Format_overrides_force_base64_or_text()
    {
        var f = _t.Text("a.txt", "plain text");
        var asBlob = (await WithFiles().GetFile(f, format: "base64")).ToList();
        Assert.IsType<EmbeddedResourceBlock>(asBlob[1]);

        var g = _t.Bytes("b.bin", new byte[] { 0x68, 0x69, 0x00, 0x21 });
        var asText = (await WithFiles().GetFile(g, format: "text")).ToList();
        Assert.Equal("hi\0!", TextOf(asText));
    }

    [Fact]
    public async Task Empty_file_is_an_empty_text_chunk()
    {
        var f = _t.Text("empty.txt", "");
        var blocks = (await WithFiles().GetFile(f)).ToList();
        Assert.Equal("", TextOf(blocks));
        Assert.Equal(JsonValueKind.Null, Meta(blocks).GetProperty("nextOffset").ValueKind);
    }

    // ---- offset / length / cap ------------------------------------------------------------

    [Fact]
    public async Task Server_cap_limits_every_call_and_nextOffset_continues()
    {
        var f = _t.Bytes("hundred.bin", Enumerable.Range(0, 100).Select(i => (byte)(i + 1)).ToArray());
        var tool = WithFiles(cap: 10);

        var a = (await tool.GetFile(f)).ToList();               // no length: capped at 10
        Assert.Equal(10, Meta(a).GetProperty("returned").GetInt64());
        Assert.Equal(10, Meta(a).GetProperty("nextOffset").GetInt64());
        Assert.True(Meta(a).GetProperty("truncated").GetBoolean());
        Assert.Equal(10, Meta(a).GetProperty("maxBytesPerCall").GetInt64());

        var b = (await tool.GetFile(f, 10, 1000)).ToList();     // length above the cap: still 10
        Assert.Equal(10, Meta(b).GetProperty("returned").GetInt64());
        Assert.Equal(20, Meta(b).GetProperty("nextOffset").GetInt64());
    }

    [Fact]
    public async Task Offset_past_the_end_returns_an_empty_chunk()
    {
        var f = _t.Text("a.txt", "abc");
        var blocks = (await WithFiles().GetFile(f, offset: 50)).ToList();
        Assert.Equal(0, Meta(blocks).GetProperty("returned").GetInt64());
        Assert.Equal(JsonValueKind.Null, Meta(blocks).GetProperty("nextOffset").ValueKind);
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected()
    {
        var f = _t.Text("a.txt", "abc");
        await Assert.ThrowsAsync<McpException>(() => WithFiles().GetFile(f, offset: -1));
        await Assert.ThrowsAsync<McpException>(() => WithFiles().GetFile(f, length: -1));
        await Assert.ThrowsAsync<McpException>(() => WithFiles().GetFile(f, format: "hex"));
    }

    [Fact]
    public async Task A_length_too_small_for_one_character_is_explained()
    {
        var f = _t.Text("emoji.txt", "😀😀😀");
        var ex = await Assert.ThrowsAsync<McpException>(() => WithFiles().GetFile(f, 0, 2));
        Assert.Contains("too small", ex.Message);
    }

    [Fact]
    public async Task A_file_that_another_process_has_open_for_writing_can_still_be_read()
    {
        var f = _t.Text("log.txt", "line one");
        using var writer = new FileStream(f, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal("line one", TextOf(await WithFiles().GetFile(f)));
    }

    // ---- index allowlist -------------------------------------------------------------------

    [Fact]
    public async Task A_file_that_is_not_in_the_index_is_refused()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => WithFiles().GetFile(_t.P("never-indexed.txt")));
        Assert.Contains("not found in the Windows Search index", ex.Message);
    }

    [Fact]
    public async Task A_directory_is_refused()
    {
        var d = _t.Dir("folder");
        var ex = await Assert.ThrowsAsync<McpException>(() => WithFiles().GetFile(d));
        Assert.Contains("directory", ex.Message);
    }

    // ---- deny list wins over the index -----------------------------------------------------

    [Theory]
    [InlineData(@".ssh\notes.txt")]
    [InlineData(@"proj\.git\config")]
    [InlineData(@"a\b\c\.venv\x.py")]
    [InlineData(@"stuff\id_rsa")]
    [InlineData(@"stuff\cert.pfx")]
    public async Task Denied_paths_are_refused_even_though_the_index_knows_them_and_without_consulting_it(string relative)
    {
        var f = _t.Text(relative, "top secret");
        var ex = await Assert.ThrowsAsync<McpException>(() => WithFiles().GetFile(f));
        Assert.Contains("denied by server policy", ex.Message);
        Assert.DoesNotContain("top secret", ex.Message);
        Assert.Equal(0, _index.Calls); // the text rules fire before the index is queried
    }

    [Fact]
    public async Task Hidden_and_system_files_and_folders_are_refused()
    {
        var hiddenFile = _t.Text(@"d\h.txt", "x");
        var inHidden = _t.Text(@"hid\deep\f.txt", "x");
        var sysFile = _t.Text(@"d\s.txt", "x");
        TempTree.Hide(hiddenFile);
        TempTree.Hide(_t.P("hid"));
        TempTree.MakeSystem(sysFile);

        foreach (var p in new[] { hiddenFile, inHidden, sysFile })
        {
            var ex = await Assert.ThrowsAsync<McpException>(() => WithFiles().GetFile(p));
            Assert.Contains("hidden or system", ex.Message);
        }
    }

    [Fact]
    public async Task Protected_root_is_refused_even_when_visible()
    {
        var f = _t.Text(@"prot\data.txt", "x");
        var tool = Tool(TestClients.Make(true), policy: _t.Policy(_t.P("prot")));
        var ex = await Assert.ThrowsAsync<McpException>(() => tool.GetFile(f));
        Assert.Contains("protected location", ex.Message);
    }

    [Fact]
    public async Task Private_key_pem_is_refused_by_content_but_a_certificate_pem_is_served()
    {
        var key = _t.Text(@"k\server.pem", "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n");
        var cert = _t.Text(@"k\server-cert.pem", "-----BEGIN CERTIFICATE-----\nBBBB\n-----END CERTIFICATE-----\n");
        var ex = await Assert.ThrowsAsync<McpException>(() => WithFiles().GetFile(key));
        Assert.Contains("private key", ex.Message);
        Assert.Contains("CERTIFICATE", TextOf(await WithFiles().GetFile(cert)));
    }

    [Fact]
    public async Task A_junction_in_a_parent_folder_cannot_smuggle_a_denied_file_out()
    {
        _t.Text(@".secret\key.txt", "do not leak");
        var link = _t.Junction("link", _t.P(".secret"));
        var through = Path.Combine(link, "key.txt");
        Assert.True(File.Exists(through));

        // The path text and the index look fine; the opened handle really points into .secret.
        Assert.True(_t.Policy().CheckPathText(through).Allowed);
        var ex = await Assert.ThrowsAsync<McpException>(() => WithFiles().GetFile(through));
        Assert.Contains("denied by server policy", ex.Message);
        Assert.DoesNotContain("do not leak", ex.Message);
    }

    [Fact]
    public async Task Denial_messages_name_the_category_but_not_the_path()
    {
        var f = _t.Text(@"customer-acme\.git\config", "x");
        var ex = await Assert.ThrowsAsync<McpException>(() => WithFiles().GetFile(f));
        Assert.DoesNotContain("customer-acme", ex.Message);
        Assert.Contains("dot-prefixed", ex.Message);
    }

    [Fact]
    public void Final_path_resolution_reports_the_real_location()
    {
        var f = _t.Text("x.txt", "x");
        using var s = File.OpenRead(f);
        var final = NativeFilePath.GetFinalPath(s.SafeFileHandle);
        Assert.StartsWith(@"\\?\", final);
        Assert.True(FileAccessPolicy.TryNormalize(final, out var n).Allowed);
        Assert.Equal(f, n, ignoreCase: true);
    }
}
