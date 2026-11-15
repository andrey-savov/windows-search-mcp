using System.ComponentModel;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WsSearchMcp.Files;
using WsSearchMcp.Http;
using WsSearchMcp.Http.Conversion;
using WsSearchMcp.Security;
using Xunit;

namespace WsSearchMcp.Tests.Files;

public class AcceptListTests
{
    [Theory]
    [InlineData(null, "text/*")]
    [InlineData("", "text/*")]
    [InlineData("   ", "text/*")]
    [InlineData("text/*", "text/*")]
    [InlineData("application/pdf", "text/*,application/pdf")]
    [InlineData(" Image/PNG ; q=0.5 , APPLICATION/PDF", "text/*,image/png,application/pdf")]
    [InlineData("image/*,image/*,text/*", "text/*,image/*")]
    [InlineData("*/*", "text/*,*/*")]
    public void Parse_normalises(string? input, string expected) =>
        Assert.Equal(expected, AcceptList.Parse(input).Normalized);

    [Theory]
    [InlineData("pdf")]
    [InlineData("application/")]
    [InlineData("/pdf")]
    [InlineData("*/pdf")]
    [InlineData("application/pdf/x")]
    [InlineData("text plain/x")]
    public void Parse_rejects_malformed_types(string input) =>
        Assert.Throws<FormatException>(() => AcceptList.Parse(input));

    [Fact]
    public void Parse_rejects_absurd_lists()
    {
        Assert.Throws<FormatException>(() => AcceptList.Parse(new string('a', 1001)));
        Assert.Throws<FormatException>(() => AcceptList.Parse(string.Join(",", Enumerable.Range(0, 40).Select(i => "x/t" + i))));
    }

    [Fact]
    public void Text_is_always_accepted_and_wildcards_match_by_type()
    {
        var onlyPdf = AcceptList.Parse("application/pdf");
        Assert.True(onlyPdf.Accepts("text/plain"));
        Assert.True(onlyPdf.Accepts("text/csv; charset=utf-8"));
        Assert.True(onlyPdf.Accepts("application/pdf"));
        Assert.False(onlyPdf.Accepts("application/zip"));
        Assert.False(onlyPdf.Accepts("image/png"));

        var images = AcceptList.Parse("image/*");
        Assert.True(images.Accepts("image/png"));
        Assert.True(images.Accepts("IMAGE/WEBP"));
        Assert.False(images.Accepts("application/pdf"));

        Assert.True(AcceptList.Parse("*/*").Accepts("application/x-anything"));
        Assert.False(AcceptList.Parse(null).Accepts(null));
        Assert.False(AcceptList.Parse(null).Accepts("nonsense"));
    }
}

public class ContentNegotiatorTests
{
    private const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static Delivery Decide(FileKind kind, string mime, string accept, bool textLike = false, string format = "auto", bool canConvert = true, bool canRender = false) =>
        ContentNegotiator.Decide(kind, textLike, mime, format, AcceptList.Parse(accept), canConvert, canRender);

    [Theory]
    [InlineData("text/*,image/*", Delivery.RenderedImages)]
    [InlineData("image/png", Delivery.RenderedImages)]
    [InlineData("image/jpeg", Delivery.RenderedImages)]
    [InlineData("image/webp", Delivery.RenderedImages)]
    [InlineData("*/*", Delivery.Blob)]                        // the raw PDF is the best binary when it is accepted
    [InlineData("application/pdf,image/*", Delivery.Blob)]
    [InlineData("text/*", Delivery.ConvertedText)]            // no image type accepted: extracted text
    [InlineData("image/tiff", Delivery.ConvertedText)]        // an image type we cannot render to
    [InlineData("image/svg+xml", Delivery.ConvertedText)]
    public void A_pdf_goes_out_as_page_images_when_images_are_accepted_but_not_pdf(string accept, Delivery expected) =>
        Assert.Equal(expected, Decide(FileKind.Pdf, "application/pdf", accept, canRender: true));

    [Fact]
    public void Without_a_renderer_a_pdf_still_converts_to_text_and_office_files_never_render()
    {
        Assert.Equal(Delivery.ConvertedText, Decide(FileKind.Pdf, "application/pdf", "image/*", canRender: false));
        Assert.Equal(Delivery.ConvertedText, Decide(FileKind.OoxmlWord, Docx, "image/*", canRender: true));
        Assert.Equal(Delivery.Text, Decide(FileKind.Pdf, "application/pdf", "image/*", format: "text", canRender: true));
    }

    [Fact]
    public void Rendered_pages_prefer_png_then_jpeg_then_webp()
    {
        Assert.Equal("image/png", ContentNegotiator.RenderMime(AcceptList.Parse("image/*")));
        Assert.Equal("image/png", ContentNegotiator.RenderMime(AcceptList.Parse("image/webp,image/png,image/jpeg")));
        Assert.Equal("image/jpeg", ContentNegotiator.RenderMime(AcceptList.Parse("image/jpeg,image/webp")));
        Assert.Equal("image/webp", ContentNegotiator.RenderMime(AcceptList.Parse("image/webp")));
        Assert.Null(ContentNegotiator.RenderMime(AcceptList.Parse("text/*,application/pdf")));
    }

    [Fact]
    public void Text_is_text_whatever_is_accepted()
    {
        foreach (var accept in new[] { "text/*", "application/pdf", "*/*", "image/*" })
            Assert.Equal(Delivery.Text, Decide(FileKind.Unknown, "text/plain", accept, textLike: true));
    }

    [Theory]
    [InlineData(FileKind.Pdf, "application/pdf", "text/*", Delivery.ConvertedText)]
    [InlineData(FileKind.Pdf, "application/pdf", "image/*", Delivery.ConvertedText)]
    [InlineData(FileKind.Pdf, "application/pdf", "application/pdf", Delivery.Blob)]
    [InlineData(FileKind.Pdf, "application/pdf", "text/*,application/pdf", Delivery.Blob)]
    [InlineData(FileKind.Pdf, "application/pdf", "*/*", Delivery.Blob)]
    [InlineData(FileKind.OoxmlWord, Docx, "text/*", Delivery.ConvertedText)]
    [InlineData(FileKind.OoxmlWord, Docx, Docx, Delivery.Blob)]
    [InlineData(FileKind.OoxmlWord, Docx, "application/*", Delivery.Blob)]
    [InlineData(FileKind.Png, "image/png", "image/*", Delivery.Image)]
    [InlineData(FileKind.Png, "image/png", "image/png", Delivery.Image)]
    [InlineData(FileKind.Jpeg, "image/jpeg", "*/*", Delivery.Image)]
    [InlineData(FileKind.Unknown, "application/octet-stream", "*/*", Delivery.Blob)]
    [InlineData(FileKind.Unknown, "application/octet-stream", "application/octet-stream", Delivery.Blob)]
    [InlineData(FileKind.Zip, "application/zip", "application/zip", Delivery.Blob)]
    [InlineData(FileKind.Ole2, "application/msword", "*/*", Delivery.Blob)]
    public void Negotiation_matrix(FileKind kind, string mime, string accept, Delivery expected) =>
        Assert.Equal(expected, Decide(kind, mime, accept));

    [Theory]
    [InlineData(FileKind.Png, "image/png", "text/*")]
    [InlineData(FileKind.Png, "image/png", "application/pdf")]
    [InlineData(FileKind.Unknown, "application/octet-stream", "text/*")]
    [InlineData(FileKind.Unknown, "image/tiff", "application/pdf")]
    [InlineData(FileKind.Zip, "application/zip", "text/*")]
    [InlineData(FileKind.Ole2, "application/msword", "text/*")]
    public void Unconvertible_files_the_client_cannot_read_are_refused_with_an_explanation(FileKind kind, string mime, string accept)
    {
        var ex = Assert.Throws<McpException>(() => Decide(kind, mime, accept));
        Assert.Contains("cannot be delivered", ex.Message);
        Assert.Contains(mime, ex.Message);
        Assert.Contains("accept", ex.Message);
    }

    [Fact]
    public void A_pdf_without_a_converter_is_refused_not_sent()
    {
        var ex = Assert.Throws<McpException>(() => Decide(FileKind.Pdf, "application/pdf", "text/*", canConvert: false));
        Assert.Contains("cannot be delivered", ex.Message);
    }

    [Fact]
    public void Explicit_formats_override_negotiation()
    {
        Assert.Equal(Delivery.Blob, Decide(FileKind.Pdf, "application/pdf", "text/*", format: "base64"));
        Assert.Equal(Delivery.Text, Decide(FileKind.Pdf, "application/pdf", "text/*", format: "text"));
        Assert.Equal(Delivery.Blob, Decide(FileKind.Unknown, "text/plain", "text/*", textLike: true, format: "base64"));
    }
}

public class FileClassifierTests : IDisposable
{
    private readonly TempTree _t = new();
    public void Dispose() => _t.Dispose();

    private FileKind Classify(byte[] bytes)
    {
        var path = _t.Bytes("x.dat", bytes);
        using var fs = File.OpenRead(path);
        var head = bytes.AsSpan(0, Math.Min(bytes.Length, 4096)).ToArray();
        return FileClassifier.Classify(fs, head, bytes.Length, 50L * 1024 * 1024);
    }

    [Fact]
    public void Formats_are_recognised_by_content()
    {
        Assert.Equal(FileKind.Pdf, Classify(DocumentFixtures.Pdf("hi")));
        Assert.Equal(FileKind.OoxmlWord, Classify(DocumentFixtures.Docx(DocumentFixtures.Para("hi"))));
        Assert.Equal(FileKind.OoxmlExcel, Classify(DocumentFixtures.Xlsx()));
        Assert.Equal(FileKind.OoxmlPowerPoint, Classify(DocumentFixtures.Pptx()));
        Assert.Equal(FileKind.Zip, Classify(DocumentFixtures.Zip(("a.txt", "hello"))));
        Assert.Equal(FileKind.Png, Classify(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")));
        Assert.Equal(FileKind.Jpeg, Classify(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10 }));
        Assert.Equal(FileKind.Gif, Classify(Encoding.ASCII.GetBytes("GIF89a....")));
        Assert.Equal(FileKind.Webp, Classify(Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ")));
        Assert.Equal(FileKind.Ole2, Classify(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0 }));
        Assert.Equal(FileKind.Unknown, Classify(Encoding.UTF8.GetBytes("just some text")));
        Assert.Equal(FileKind.Unknown, Classify(new byte[] { 0x49, 0x49, 0x2A, 0x00, 1, 2 })); // tiff is not an image block type
    }

    [Fact]
    public void The_extension_never_decides_a_corrupt_zip_is_just_a_zip()
    {
        var truncated = DocumentFixtures.Docx(DocumentFixtures.Para("hi")).Take(40).ToArray();
        Assert.Equal(FileKind.Zip, Classify(truncated));
    }

    [Fact]
    public void Mime_comes_from_content_for_images_and_pdfs()
    {
        Assert.Equal("application/pdf", FileClassifier.MimeFor(FileKind.Pdf, null));
        Assert.Equal("image/png", FileClassifier.MimeFor(FileKind.Png, "application/octet-stream"));
        Assert.Equal("text/plain", FileClassifier.MimeFor(FileKind.Unknown, "text/plain"));
        Assert.StartsWith("application/vnd.openxmlformats", FileClassifier.MimeFor(FileKind.OoxmlExcel, null));
    }
}

public class ConverterTests
{
    private static string Convert(IContentConverter c, FileKind kind, byte[] data, long maxExpanded = 200L * 1024 * 1024, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(data);
        return c.Convert(ms, kind, new ConversionBudget(maxExpanded, 100_000_000, ct));
    }

    [Fact]
    public void Pdf_text_is_extracted_page_by_page()
    {
        var text = Convert(new PdfTextConverter(), FileKind.Pdf, DocumentFixtures.Pdf("Hello PDF world", "Second page text"));
        Assert.Contains("--- page 1 ---", text);
        Assert.Contains("Hello PDF world", text);
        Assert.Contains("--- page 2 ---", text);
        Assert.Contains("Second page text", text);
        Assert.True(text.IndexOf("Hello", StringComparison.Ordinal) < text.IndexOf("Second", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pdf_without_any_text_layer_is_reported_as_a_scan()
    {
        var ex = Assert.Throws<ConversionException>(() => Convert(new PdfTextConverter(), FileKind.Pdf, DocumentFixtures.Pdf("", "")));
        Assert.Contains("no extractable text", ex.Message);
    }

    [Fact]
    public void A_corrupt_pdf_is_a_conversion_error_without_internals()
    {
        var ex = Assert.Throws<ConversionException>(() =>
            Convert(new PdfTextConverter(), FileKind.Pdf, Encoding.ASCII.GetBytes("%PDF-1.4\nthis is not a pdf")));
        Assert.DoesNotContain("Exception", ex.Message);
    }

    [Fact]
    public void Docx_body_paragraphs_tables_and_alternate_content_are_extracted_once()
    {
        var body =
            DocumentFixtures.Para("First paragraph") +
            "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>B</w:t></w:r></w:p></w:tc></w:tr>" +
            "<w:tr><w:tc><w:p><w:r><w:t>C</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>D</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" +
            "<w:p><w:r><w:t>before</w:t><w:tab/><w:t>after</w:t></w:r></w:p>" +
            "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\">" +
            "<mc:Choice Requires=\"wps\"><w:p><w:r><w:t>boxed</w:t></w:r></w:p></mc:Choice>" +
            "<mc:Fallback><w:p><w:r><w:t>boxed</w:t></w:r></w:p></mc:Fallback></mc:AlternateContent>";
        var text = Convert(new OoxmlTextConverter(), FileKind.OoxmlWord, DocumentFixtures.Docx(body));

        Assert.StartsWith("First paragraph\nA\tB\nC\tD\nbefore\tafter\nboxed", text.Replace("\r", ""));
        Assert.Equal(1, text.Split("boxed").Length - 1);
    }

    [Fact]
    public void Xlsx_sheets_are_mapped_through_relationships_not_by_file_number()
    {
        var text = Convert(new OoxmlTextConverter(), FileKind.OoxmlExcel, DocumentFixtures.Xlsx()).Replace("\r", "");
        var expected =
            "--- sheet: Beta ---\n" +
            "Hello\tinline\tfx\n" +
            "TRUE\t\t#DIV/0!\t42\n" +
            "\t\tRich text\n" +
            "\n" +
            "--- sheet: Alpha (hidden) ---\n" +
            "second";
        Assert.Equal(expected, text);
        Assert.DoesNotContain("IGNORED", text); // phonetic hints are skipped
    }

    [Fact]
    public void Pptx_slides_follow_presentation_order_not_file_names()
    {
        var text = Convert(new OoxmlTextConverter(), FileKind.OoxmlPowerPoint, DocumentFixtures.Pptx()).Replace("\r", "");
        Assert.Equal("--- slide 1 ---\nTitle Two\n\n--- slide 2 ---\nTitle One", text);
    }

    [Fact]
    public void A_document_with_no_text_or_a_broken_package_is_a_conversion_error()
    {
        Assert.Throws<ConversionException>(() => Convert(new OoxmlTextConverter(), FileKind.OoxmlWord, DocumentFixtures.Docx("")));
        Assert.Throws<ConversionException>(() => Convert(new OoxmlTextConverter(), FileKind.OoxmlWord, new byte[] { 1, 2, 3 }));
        Assert.Throws<ConversionException>(() => Convert(new OoxmlTextConverter(), FileKind.OoxmlWord,
            DocumentFixtures.Zip(("hello.txt", "no office parts here"))));
    }

    [Fact]
    public void Dtd_declarations_are_rejected()
    {
        var evil = DocumentFixtures.Zip(
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"),
            ("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/document.xml", "<!DOCTYPE d [<!ENTITY x \"boom\">]><w:document xmlns:w=\"" + DocumentFixtures.W + "\"><w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>"));
        var ex = Assert.Throws<ConversionException>(() => Convert(new OoxmlTextConverter(), FileKind.OoxmlWord, evil));
        Assert.DoesNotContain("boom", ex.Message);
    }

    [Fact]
    public void Real_expansion_beyond_the_budget_aborts_the_conversion()
    {
        // ~8 MiB of text compresses to a few KB; the budget counts what actually comes out.
        var huge = DocumentFixtures.Docx(string.Concat(Enumerable.Repeat(DocumentFixtures.Para(new string('a', 1000)), 8000)));
        Assert.True(huge.Length < 1_000_000);
        var ex = Assert.Throws<ConversionLimitException>(() =>
            Convert(new OoxmlTextConverter(), FileKind.OoxmlWord, huge, maxExpanded: 1024 * 1024));
        Assert.Contains("expands beyond", ex.Message);
    }

    [Fact]
    public void A_zip_that_lies_about_its_uncompressed_size_cannot_dodge_the_budget()
    {
        var huge = DocumentFixtures.Docx(string.Concat(Enumerable.Repeat(DocumentFixtures.Para(new string('b', 1000)), 8000)));
        var lying = DocumentFixtures.WithDeclaredUncompressedSize(huge, 100);
        // Either the real bytes hit the budget, or the runtime notices the mismatch: never a successful oversized read.
        Assert.ThrowsAny<ConversionException>(() =>
            Convert(new OoxmlTextConverter(), FileKind.OoxmlWord, lying, maxExpanded: 1024 * 1024));
    }

    [Fact]
    public void The_budget_is_shared_across_all_entries_of_an_archive()
    {
        var parts = new List<(string, string)>
        {
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/></Types>"),
            ("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
        };
        var ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        parts.Add(("xl/workbook.xml", $"<workbook {ns}><sheets>" +
            string.Concat(Enumerable.Range(1, 6).Select(i => $"<sheet name=\"S{i}\" sheetId=\"{i}\" r:id=\"rId{i}\"/>")) + "</sheets></workbook>"));
        parts.Add(("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            string.Concat(Enumerable.Range(1, 6).Select(i => $"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/s{i}.xml\"/>")) + "</Relationships>"));
        // Each sheet is ~300 KB of cells (well under the limit on its own); together they exceed it.
        var rows = string.Concat(Enumerable.Range(1, 3000).Select(r => $"<row r=\"{r}\"><c r=\"A{r}\" t=\"inlineStr\"><is><t>{new string('x', 60)}</t></is></c></row>"));
        for (var i = 1; i <= 6; i++)
            parts.Add(($"xl/worksheets/s{i}.xml", $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>{rows}</sheetData></worksheet>"));

        var zip = DocumentFixtures.Zip(parts.ToArray());
        Assert.Throws<ConversionLimitException>(() =>
            Convert(new OoxmlTextConverter(), FileKind.OoxmlExcel, zip, maxExpanded: 1024 * 1024));
        // Sanity: the same file converts under a generous budget.
        Assert.Contains("--- sheet: S6 ---", Convert(new OoxmlTextConverter(), FileKind.OoxmlExcel, zip));
    }

    [Fact]
    public void Column_references_are_decoded()
    {
        Assert.Equal(0, OoxmlTextConverter.ColumnOf("A1", 9));
        Assert.Equal(2, OoxmlTextConverter.ColumnOf("C5", 9));
        Assert.Equal(26, OoxmlTextConverter.ColumnOf("AA10", 9));
        Assert.Equal(9, OoxmlTextConverter.ColumnOf(null, 9));
        Assert.Equal(9, OoxmlTextConverter.ColumnOf("12", 9));
    }
}

public class ConversionServiceTests
{
    private sealed class StubConverter : IContentConverter
    {
        public int Calls;
        public Func<ConversionBudget, string> Body { get; init; } = _ => "converted";
        public bool CanConvert(FileKind kind) => kind == FileKind.Pdf;
        public string Describe(FileKind kind) => "stub";
        public string Convert(Stream input, FileKind kind, ConversionBudget budget)
        {
            Interlocked.Increment(ref Calls);
            return Body(budget);
        }
    }

    private static ConversionKey Key(string name, long ticks = 1) => new(@"C:\x\" + name, 10, ticks);

    private static Task<byte[]> Get(ConversionService s, ConversionKey key) =>
        s.GetTextAsync(FileKind.Pdf, key, new MemoryStream(new byte[(int)key.Size]), CancellationToken.None);

    [Fact]
    public async Task A_second_request_for_the_same_file_version_is_served_from_the_cache()
    {
        var stub = new StubConverter();
        var s = new ConversionService(new[] { stub }, new ConversionOptions(), 1_000_000);
        var a = await Get(s, Key("a.pdf"));
        var b = await Get(s, Key("A.PDF")); // paths compare case-insensitively
        Assert.Equal(a, b);
        Assert.Equal(1, stub.Calls);

        await Get(s, Key("a.pdf", ticks: 2)); // a changed file is a new version
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_conversion()
    {
        var gate = new ManualResetEventSlim();
        var stub = new StubConverter { Body = _ => { gate.Wait(TimeSpan.FromSeconds(10)); return "shared"; } };
        var s = new ConversionService(new[] { stub }, new ConversionOptions(), 1_000_000);
        var tasks = Enumerable.Range(0, 5).Select(_ => Get(s, Key("same.pdf"))).ToArray();
        await Task.Delay(100);
        gate.Set();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal("shared", Encoding.UTF8.GetString(r)));
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task Failures_are_not_cached_and_the_next_call_retries()
    {
        var attempts = 0;
        var stub = new StubConverter { Body = _ => ++attempts == 1 ? throw new ConversionException("first try fails") : "ok" };
        var s = new ConversionService(new[] { stub }, new ConversionOptions(), 1_000_000);
        await Assert.ThrowsAsync<ConversionException>(() => Get(s, Key("f.pdf")));
        Assert.Equal("ok", Encoding.UTF8.GetString(await Get(s, Key("f.pdf"))));
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task The_cache_stays_within_its_byte_budget_and_evicts_least_recently_used()
    {
        var stub = new StubConverter { Body = _ => new string('z', 400) };
        var s = new ConversionService(new[] { stub }, new ConversionOptions { CacheBytes = 1000 }, 1_000_000);
        await Get(s, Key("1"));
        await Get(s, Key("2"));
        await Get(s, Key("1")); // touch 1 so 2 is the least recently used
        await Get(s, Key("3")); // 1200 bytes would exceed 1000: evicts 2
        Assert.True(s.CachedBytes <= 1000, $"cached {s.CachedBytes}");

        var before = stub.Calls;
        await Get(s, Key("1"));
        Assert.Equal(before, stub.Calls);        // still cached
        await Get(s, Key("2"));
        Assert.Equal(before + 1, stub.Calls);    // was evicted
    }

    [Fact]
    public async Task A_result_bigger_than_the_whole_budget_is_served_but_not_kept()
    {
        var stub = new StubConverter { Body = _ => new string('q', 2000) };
        var s = new ConversionService(new[] { stub }, new ConversionOptions { CacheBytes = 1000 }, 1_000_000);
        Assert.Equal(2000, (await Get(s, Key("big"))).Length);
        Assert.Equal(0, s.CachedBytes);
        await Get(s, Key("big"));
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task A_converter_that_never_returns_times_out()
    {
        // Blocks until the service cancels the budget after the timeout, so the worker thread is released.
        var stub = new StubConverter { Body = b => { b.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)); return "late"; } };
        var s = new ConversionService(new[] { stub }, new ConversionOptions { Timeout = TimeSpan.FromMilliseconds(250) }, 1_000_000);
        var ex = await Assert.ThrowsAsync<ConversionLimitException>(() => Get(s, Key("hang.pdf")));
        Assert.Contains("timed out", ex.Message);
        Assert.Equal(0, s.CachedBytes);
    }

    [Fact]
    public async Task Files_over_the_input_limit_are_not_loaded()
    {
        var s = new ConversionService(new[] { new StubConverter() }, new ConversionOptions { MaxInputBytes = 5 }, 1_000_000);
        var ex = await Assert.ThrowsAsync<ConversionLimitException>(() => Get(s, Key("large.pdf")));
        Assert.Contains("not converted", ex.Message);
    }

    [Fact]
    public async Task Output_beyond_the_per_call_cap_is_refused()
    {
        var stub = new StubConverter { Body = b => { b.CheckOutput(5000); return "x"; } };
        var s = new ConversionService(new[] { stub }, new ConversionOptions(), maxOutputBytes: 100);
        await Assert.ThrowsAsync<ConversionLimitException>(() => Get(s, Key("o.pdf")));
    }
}

public class PdfRenderingServiceTests
{
    private sealed class FakeRenderer : IPdfRenderer
    {
        public int Pages { get; init; } = 3;
        public Action? OnRender { get; init; }
        public int GetPageCount(byte[] pdf) => Pages;
        public RenderedPage Render(byte[] pdf, int pageIndex, string mimeType, int maxDimension, int maxDpi)
        {
            OnRender?.Invoke();
            return new RenderedPage(pageIndex + 1, new byte[100], mimeType, 10, 10);
        }
    }

    private static ConversionService Service(IPdfRenderer renderer, ConversionOptions? options = null) =>
        new(Array.Empty<IContentConverter>(), options ?? new ConversionOptions(), 1_000_000, renderer);

    private static Task<RenderResult> Render(ConversionService s, int first = 0, int max = 10, long maxBytes = 1_000_000) =>
        s.RenderPagesAsync(new ConversionKey("C:/x.pdf", 10, 1), new MemoryStream(new byte[10]), "image/png", first, max, maxBytes, 1568, 150, CancellationToken.None);

    [Fact]
    public async Task Pages_are_returned_from_the_requested_page_up_to_the_requested_count()
    {
        var s = Service(new FakeRenderer { Pages = 5 });
        var r = await Render(s, first: 1, max: 2);
        Assert.Equal(5, r.PageCount);
        Assert.Equal(new[] { 2, 3 }, r.Pages.Select(p => p.Page));
        Assert.Empty((await Render(s, first: 9)).Pages);
    }

    [Fact]
    public async Task Pages_stop_at_the_byte_cap_but_the_first_page_always_fits_or_fails()
    {
        var s = Service(new FakeRenderer { Pages = 5 });
        Assert.Equal(2, (await Render(s, max: 10, maxBytes: 250)).Pages.Count); // 100 bytes per page
        await Assert.ThrowsAsync<ConversionLimitException>(() => Render(s, max: 10, maxBytes: 50));
    }

    [Fact]
    public async Task A_renderer_that_hangs_times_out_and_does_not_block_the_next_caller_forever()
    {
        var release = new ManualResetEventSlim();
        var s = Service(new FakeRenderer { OnRender = () => release.Wait(TimeSpan.FromSeconds(10)) },
            new ConversionOptions { Timeout = TimeSpan.FromMilliseconds(300) });
        var ex = await Assert.ThrowsAsync<ConversionLimitException>(() => Render(s));
        Assert.Contains("timed out", ex.Message);

        // The hung native call still holds the gate: the next caller must fail fast, not queue indefinitely.
        var busy = await Assert.ThrowsAsync<ConversionLimitException>(() => Render(s));
        Assert.Contains("busy", busy.Message);
        release.Set();
    }

    [Fact]
    public async Task Oversized_input_is_not_rendered()
    {
        var s = Service(new FakeRenderer(), new ConversionOptions { MaxInputBytes = 5 });
        var ex = await Assert.ThrowsAsync<ConversionLimitException>(() => Render(s));
        Assert.Contains("not rendered", ex.Message);
    }
}

/// <summary>get_file negotiation and conversion through the tool itself (policy, index and permission still apply first).</summary>
public class FileToolNegotiationTests : IDisposable
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly TempTree _t = new();
    private readonly FakeIndex _index = new();
    public void Dispose() => _t.Dispose();

    private FileTool Tool(ConversionService? conversion = null, long cap = McpHttpServerOptions.DefaultMaxFileBytes)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[McpHttpServer.ClientItemKey] = TestClients.Make(files: true);
        var options = new McpHttpServerOptions { Store = new InMemoryClientStore(), Certificate = TestClients.PublicCert(), MaxFileBytes = cap };
        return new FileTool(new HttpContextAccessor { HttpContext = ctx }, options, _t.Policy(), _index.Resolver,
            conversion ?? ConversionService.CreateDefault(new ConversionOptions(), cap), NullLogger<FileTool>.Instance);
    }

    private static JsonElement Meta(IEnumerable<ContentBlock> blocks) =>
        JsonDocument.Parse(((TextContentBlock)blocks.First()).Text).RootElement.Clone();

    private static string TextOf(IEnumerable<ContentBlock> blocks) => ((TextContentBlock)blocks.ElementAt(1)).Text;

    // ---- PDF -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_pdf_is_converted_to_text_by_default()
    {
        var f = _t.Bytes("report.pdf", DocumentFixtures.Pdf("Hello PDF world"));
        var blocks = (await Tool().GetFile(f)).ToList();
        var m = Meta(blocks);
        Assert.Equal("converted-text", m.GetProperty("deliveredAs").GetString());
        Assert.Equal("application/pdf", m.GetProperty("convertedFrom").GetString());
        Assert.Equal("text/plain", m.GetProperty("mimeType").GetString());
        Assert.Equal("text/*", m.GetProperty("accept").GetString());
        Assert.Equal(new FileInfo(f).Length, m.GetProperty("sourceSize").GetInt64());
        Assert.Equal(Encoding.UTF8.GetByteCount(TextOf(blocks)), m.GetProperty("size").GetInt64());
        Assert.Contains("Hello PDF world", TextOf(blocks));
        Assert.Contains("scanned", m.GetProperty("note").GetString());
    }

    [Fact]
    public async Task A_pdf_is_delivered_natively_when_accepted_and_the_note_explains_the_fallback()
    {
        var bytes = DocumentFixtures.Pdf("Hello PDF world");
        var f = _t.Bytes("report.pdf", bytes);
        var blocks = (await Tool().GetFile(f, accept: "text/*,application/pdf")).ToList();
        var blob = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(blocks[1]).Resource);
        Assert.Equal("application/pdf", blob.MimeType);
        Assert.Equal(bytes, blob.DecodedData.ToArray());
        var m = Meta(blocks);
        Assert.Equal("blob", m.GetProperty("deliveredAs").GetString());
        Assert.Contains("accept=\"text/*\"", m.GetProperty("note").GetString());
        Assert.False(m.TryGetProperty("convertedFrom", out _));
    }

    [Fact]
    public async Task A_pdf_whose_head_is_pure_ascii_is_still_a_pdf_never_text()
    {
        // Many real PDFs have no NUL or high byte in their first 4 KiB; they must not be sent as "UTF-8 text".
        var ascii = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");
        var f = _t.Bytes("plain.pdf", ascii);
        var blocks = (await Tool().GetFile(f, accept: "*/*")).ToList();
        Assert.Equal("blob", Meta(blocks).GetProperty("deliveredAs").GetString());
        Assert.IsType<EmbeddedResourceBlock>(blocks[1]);
    }

    [Fact]
    public async Task A_scanned_pdf_with_default_accept_fails_with_a_clear_reason()
    {
        var f = _t.Bytes("scan.pdf", DocumentFixtures.Pdf("", ""));
        var ex = await Assert.ThrowsAsync<McpException>(() => Tool().GetFile(f));
        Assert.Contains("could not be converted", ex.Message);
        Assert.Contains("no extractable text", ex.Message);
        Assert.DoesNotContain(_t.Root, ex.Message); // no server paths in client errors
    }

    // ---- PDF as rendered images ------------------------------------------------------------------

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    [Fact]
    public async Task A_pdf_is_delivered_as_page_images_not_text_when_images_are_accepted()
    {
        var f = _t.Bytes("report.pdf", DocumentFixtures.Pdf("Page one", "Page two", "Page three"));
        var blocks = (await Tool().GetFile(f, accept: "text/*,image/*")).ToList();

        var m = Meta(blocks);
        Assert.Equal("rendered-images", m.GetProperty("deliveredAs").GetString());
        Assert.Equal("application/pdf", m.GetProperty("convertedFrom").GetString());
        Assert.Equal("image/png", m.GetProperty("mimeType").GetString());
        Assert.Equal("pages", m.GetProperty("unit").GetString());
        Assert.Equal(3, m.GetProperty("size").GetInt64());
        Assert.Equal(1, m.GetProperty("returned").GetInt64());
        Assert.Equal(1, m.GetProperty("nextOffset").GetInt64());
        Assert.Contains("accept=\"text/*\"", m.GetProperty("note").GetString());

        Assert.Equal(2, blocks.Count); // metadata + one page; no text block
        var img = Assert.IsType<ImageContentBlock>(blocks[1]);
        Assert.Equal("image/png", img.MimeType);
        Assert.Equal(PngSignature, img.DecodedData.ToArray().Take(8).ToArray());

        var page = m.GetProperty("pages")[0];
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.InRange(page.GetProperty("height").GetInt32(), 1000, 1568);  // A4: the longest edge is the height
        Assert.True(page.GetProperty("width").GetInt32() < page.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task Rendered_pages_are_paged_by_page_number()
    {
        var f = _t.Bytes("three.pdf", DocumentFixtures.Pdf("a", "b", "c"));
        var tool = Tool();

        var two = (await tool.GetFile(f, offset: 0, length: 2, accept: "image/*")).ToList();
        Assert.Equal(3, two.Count);
        Assert.Equal(2, Meta(two).GetProperty("returned").GetInt64());
        Assert.Equal(2, Meta(two).GetProperty("nextOffset").GetInt64());

        var last = (await tool.GetFile(f, offset: 2, accept: "image/*")).ToList();
        Assert.Equal(2, last.Count);
        Assert.Equal(JsonValueKind.Null, Meta(last).GetProperty("nextOffset").ValueKind);
        Assert.False(Meta(last).GetProperty("truncated").GetBoolean());

        var past = (await tool.GetFile(f, offset: 7, accept: "image/*")).ToList();
        Assert.Single(past); // metadata only
        Assert.Equal(0, Meta(past).GetProperty("returned").GetInt64());
    }

    [Fact]
    public async Task The_pages_per_call_limit_applies_however_many_are_requested()
    {
        var f = _t.Bytes("many.pdf", DocumentFixtures.Pdf("1", "2", "3", "4"));
        var conversion = ConversionService.CreateDefault(new ConversionOptions { RenderMaxPagesPerCall = 2 }, McpHttpServerOptions.DefaultMaxFileBytes);
        var blocks = (await Tool(conversion).GetFile(f, length: 99, accept: "image/*")).ToList();
        Assert.Equal(2, Meta(blocks).GetProperty("returned").GetInt64());
        Assert.Equal(2, Meta(blocks).GetProperty("nextOffset").GetInt64());
    }

    [Fact]
    public async Task A_scanned_pdf_that_has_no_text_still_works_as_images()
    {
        var f = _t.Bytes("scan.pdf", DocumentFixtures.Pdf("", ""));
        var blocks = (await Tool().GetFile(f, accept: "text/*,image/*")).ToList();
        Assert.IsType<ImageContentBlock>(blocks[1]);
    }

    [Fact]
    public async Task The_accepted_image_type_decides_the_rendered_format()
    {
        var f = _t.Bytes("fmt.pdf", DocumentFixtures.Pdf("hi"));
        var jpeg = (await Tool().GetFile(f, accept: "image/jpeg")).ToList();
        var img = Assert.IsType<ImageContentBlock>(jpeg[1]);
        Assert.Equal("image/jpeg", img.MimeType);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, img.DecodedData.ToArray().Take(3).ToArray());
    }

    [Fact]
    public async Task Native_pdf_delivery_beats_rendering_when_both_are_accepted()
    {
        var f = _t.Bytes("both.pdf", DocumentFixtures.Pdf("hi"));
        var blocks = (await Tool().GetFile(f, accept: "application/pdf,image/*")).ToList();
        Assert.Equal("blob", Meta(blocks).GetProperty("deliveredAs").GetString());
    }

    [Fact]
    public async Task A_pdf_that_cannot_be_rendered_says_so_and_points_at_the_text_fallback()
    {
        var f = _t.Bytes("bad.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\nthis is not really a pdf\n"));
        var ex = await Assert.ThrowsAsync<McpException>(() => Tool().GetFile(f, accept: "image/*"));
        Assert.Contains("could not be rendered", ex.Message);
        Assert.Contains("accept=\"text/*\"", ex.Message);
        Assert.DoesNotContain(_t.Root, ex.Message);
    }

    [Fact]
    public async Task A_rendered_page_larger_than_the_per_call_cap_is_refused()
    {
        var f = _t.Bytes("big.pdf", DocumentFixtures.Pdf("hi"));
        var ex = await Assert.ThrowsAsync<McpException>(() => Tool(cap: 100).GetFile(f, accept: "image/*"));
        Assert.Contains("per-call cap", ex.Message);
    }

    // ---- Office --------------------------------------------------------------------------------

    [Fact]
    public async Task Office_files_are_converted_and_native_delivery_needs_their_mime_type()
    {
        var docx = _t.Bytes("letter.docx", DocumentFixtures.Docx(DocumentFixtures.Para("Dear reader")));
        var xlsx = _t.Bytes("sheet.xlsx", DocumentFixtures.Xlsx());
        var pptx = _t.Bytes("deck.pptx", DocumentFixtures.Pptx());

        Assert.Contains("Dear reader", TextOf(await Tool().GetFile(docx)));
        Assert.Contains("--- sheet: Beta ---", TextOf(await Tool().GetFile(xlsx)));
        Assert.Contains("--- slide 1 ---", TextOf(await Tool().GetFile(pptx)));

        var native = (await Tool().GetFile(docx, accept: "application/vnd.openxmlformats-officedocument.wordprocessingml.document")).ToList();
        Assert.IsType<EmbeddedResourceBlock>(native[1]);
        Assert.Equal("blob", Meta(native).GetProperty("deliveredAs").GetString());
    }

    [Fact]
    public async Task A_docx_renamed_to_txt_is_still_recognised_by_content()
    {
        var f = _t.Bytes("notes.txt", DocumentFixtures.Docx(DocumentFixtures.Para("secret plan")));
        var blocks = (await Tool().GetFile(f)).ToList();
        Assert.Equal("converted-text", Meta(blocks).GetProperty("deliveredAs").GetString());
        Assert.Contains("secret plan", TextOf(blocks));
    }

    [Fact]
    public async Task Legacy_office_files_cannot_be_converted_and_say_so()
    {
        var ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 };
        var f = _t.Bytes("old.doc", ole);
        var ex = await Assert.ThrowsAsync<McpException>(() => Tool().GetFile(f));
        Assert.Contains("Legacy and password-protected", ex.Message);
        Assert.IsType<EmbeddedResourceBlock>((await Tool().GetFile(f, accept: "*/*")).ElementAt(1));
    }

    // ---- paging converted output -----------------------------------------------------------------

    [Fact]
    public async Task Converted_text_pages_by_converted_bytes_converts_once_and_never_splits_a_character()
    {
        var sentence = "Grüße aus Zürich – 日本語 😀 ";
        var f = _t.Bytes("long.docx", DocumentFixtures.Docx(string.Concat(Enumerable.Repeat(DocumentFixtures.Para(sentence), 40))));
        var service = ConversionService.CreateDefault(new ConversionOptions(), McpHttpServerOptions.DefaultMaxFileBytes);
        var tool = Tool(service);

        var sb = new StringBuilder();
        long offset = 0;
        long size = -1;
        for (var guard = 0; guard < 2000; guard++)
        {
            var blocks = (await tool.GetFile(f, offset, 37)).ToList();
            var m = Meta(blocks);
            Assert.True(m.GetProperty("returned").GetInt64() <= 37);
            size = m.GetProperty("size").GetInt64();
            sb.Append(TextOf(blocks));
            if (m.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
            offset = m.GetProperty("nextOffset").GetInt64();
        }
        var all = sb.ToString();
        Assert.Equal(size, Encoding.UTF8.GetByteCount(all));
        Assert.Equal(40, all.Split(sentence.Trim()).Length - 1);
        Assert.DoesNotContain('\uFFFD', all);
        Assert.Equal(1, service.Conversions);
    }

    // ---- images ----------------------------------------------------------------------------------

    [Fact]
    public async Task Images_need_an_image_accept_and_are_delivered_whole()
    {
        var f = _t.Bytes("pic.png", TinyPng);
        var tool = Tool();

        var err = await Assert.ThrowsAsync<McpException>(() => tool.GetFile(f)); // default accept: text only
        Assert.Contains("cannot be delivered", err.Message);
        Assert.Contains("Images cannot be converted", err.Message);

        Assert.IsType<ImageContentBlock>((await tool.GetFile(f, accept: "image/png")).ElementAt(1));
        Assert.IsType<ImageContentBlock>((await tool.GetFile(f, offset: 0, length: TinyPng.Length + 100, accept: "image/*")).ElementAt(1));
    }

    [Fact]
    public async Task Offset_or_a_short_length_on_an_image_gets_the_delivered_whole_message_not_the_generic_one()
    {
        var f = _t.Bytes("pic.png", TinyPng);
        foreach (var (offset, length) in new[] { (5L, 0L), (0L, 10L), (3L, 10L) })
        {
            var ex = await Assert.ThrowsAsync<McpException>(() => Tool().GetFile(f, offset, length, accept: "image/*"));
            Assert.Contains("delivered whole", ex.Message);
            Assert.DoesNotContain("cannot be delivered", ex.Message);
        }
    }

    [Fact]
    public async Task An_image_over_the_server_cap_is_refused_with_the_whole_image_message_and_base64_can_page_it()
    {
        var f = _t.Bytes("pic.png", TinyPng);
        var ex = await Assert.ThrowsAsync<McpException>(() => Tool(cap: 20).GetFile(f, accept: "image/*"));
        Assert.Contains("delivered whole", ex.Message);
        Assert.Contains("over the server cap", ex.Message);

        var part = (await Tool(cap: 20).GetFile(f, format: "base64")).ToList();
        var blob = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(part[1]).Resource);
        Assert.Equal(TinyPng.Take(20).ToArray(), blob.DecodedData.ToArray());
        Assert.True(Meta(part).GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task A_tiff_is_an_ordinary_blob_never_an_image_block()
    {
        var tiff = new byte[] { 0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0, 0, 0 };
        var f = _t.Bytes("scan.tif", tiff);
        var blocks = (await Tool().GetFile(f, accept: "image/*")).ToList();
        Assert.IsType<EmbeddedResourceBlock>(blocks[1]);
        Assert.Equal("image/tiff", Meta(blocks).GetProperty("mimeType").GetString());
    }

    // ---- text, unknown and archive files ------------------------------------------------------------

    [Fact]
    public async Task Text_stays_text_even_when_the_client_lists_only_other_types()
    {
        var f = _t.Text("a.txt", "plain words");
        var blocks = (await Tool().GetFile(f, accept: "application/pdf")).ToList();
        Assert.Equal("text", Meta(blocks).GetProperty("deliveredAs").GetString());
        Assert.Equal("plain words", TextOf(blocks));
    }

    [Fact]
    public async Task Unknown_binaries_and_archives_need_their_type_in_accept()
    {
        var bin = _t.Bytes("data.bin", new byte[] { 0, 1, 2, 3, 0, 255, 254 });
        var zip = _t.Bytes("pack.zip", DocumentFixtures.Zip(("a.txt", "x")));

        foreach (var f in new[] { bin, zip })
        {
            var ex = await Assert.ThrowsAsync<McpException>(() => Tool().GetFile(f));
            Assert.Contains("cannot be delivered", ex.Message);
            Assert.Contains("accept", ex.Message);
        }
        Assert.IsType<EmbeddedResourceBlock>((await Tool().GetFile(bin, accept: "application/octet-stream")).ElementAt(1));
        Assert.IsType<EmbeddedResourceBlock>((await Tool().GetFile(zip, accept: "application/zip")).ElementAt(1));
        Assert.IsType<EmbeddedResourceBlock>((await Tool().GetFile(zip, accept: "*/*")).ElementAt(1));
    }

    [Fact]
    public async Task A_late_NUL_in_an_apparently_text_file_falls_back_to_the_binary_rules()
    {
        var bytes = Encoding.ASCII.GetBytes(new string('a', 5000)).Concat(new byte[] { 0, 1, 2 }).ToArray();
        var f = _t.Bytes("late.dat", bytes);
        // First chunk (start of the file) is text; the chunk holding the NULs is binary and needs an accepting client.
        Assert.Equal("text", Meta(await Tool().GetFile(f, 0, 100)).GetProperty("deliveredAs").GetString());
        await Assert.ThrowsAsync<McpException>(() => Tool().GetFile(f, 4990, 100));
        Assert.Equal("blob", Meta(await Tool().GetFile(f, 4990, 100, accept: "*/*")).GetProperty("deliveredAs").GetString());
    }

    [Fact]
    public async Task Invalid_accept_is_rejected_with_an_explanation()
    {
        var f = _t.Text("a.txt", "x");
        var ex = await Assert.ThrowsAsync<McpException>(() => Tool().GetFile(f, accept: "not a mime"));
        Assert.Contains("not a valid MIME type", ex.Message);
    }

    // ---- conversion is behind every policy check ---------------------------------------------------------

    private sealed class CountingConverter : IContentConverter
    {
        public int Calls;
        public bool CanConvert(FileKind kind) => kind == FileKind.Pdf;
        public string Describe(FileKind kind) => "";
        public string Convert(Stream input, FileKind kind, ConversionBudget budget)
        {
            Interlocked.Increment(ref Calls);
            return "should never be produced";
        }
    }

    [Fact]
    public async Task Denied_and_unindexed_files_never_reach_a_converter()
    {
        var counting = new CountingConverter();
        var service = new ConversionService(new[] { counting }, new ConversionOptions(), 1_000_000);
        var pdf = DocumentFixtures.Pdf("top secret");

        var dot = _t.Bytes(@".git\secret.pdf", pdf);
        var hidden = _t.Bytes(@"d\hidden.pdf", pdf);
        TempTree.Hide(hidden);
        var notIndexed = _t.P("never-indexed.pdf");
        _t.Junction("link", _t.Dir(".vault"));
        File.WriteAllBytes(Path.Combine(_t.P(".vault"), "in-vault.pdf"), pdf);
        var throughJunction = Path.Combine(_t.P("link"), "in-vault.pdf");

        foreach (var p in new[] { dot, hidden, notIndexed, throughJunction })
            await Assert.ThrowsAsync<McpException>(() => Tool(service).GetFile(p));
        Assert.Equal(0, counting.Calls);
    }

    private sealed class CountingRenderer : IPdfRenderer
    {
        public int Calls;
        public int GetPageCount(byte[] pdf) { Interlocked.Increment(ref Calls); return 1; }
        public RenderedPage Render(byte[] pdf, int pageIndex, string mimeType, int maxDimension, int maxDpi)
        {
            Interlocked.Increment(ref Calls);
            return new RenderedPage(1, new byte[10], mimeType, 1, 1);
        }
    }

    [Fact]
    public async Task Denied_and_unindexed_files_never_reach_the_renderer()
    {
        var renderer = new CountingRenderer();
        var service = new ConversionService(Array.Empty<IContentConverter>(), new ConversionOptions(), 1_000_000, renderer);
        var pdf = DocumentFixtures.Pdf("top secret");

        var dot = _t.Bytes(@".git\secret.pdf", pdf);
        var hidden = _t.Bytes(@"d\hidden.pdf", pdf);
        TempTree.Hide(hidden);
        var notIndexed = _t.P("never-indexed.pdf");

        foreach (var p in new[] { dot, hidden, notIndexed })
            await Assert.ThrowsAsync<McpException>(() => Tool(service).GetFile(p, accept: "image/*"));
        Assert.Equal(0, renderer.Calls);
    }

    [Fact]
    public async Task A_cancelled_request_does_not_wait_for_a_stuck_conversion()
    {
        var f = _t.Bytes("slow.pdf", DocumentFixtures.Pdf("x"));
        var hang = new HangingConverter();
        var service = new ConversionService(new[] { hang }, new ConversionOptions { Timeout = TimeSpan.FromSeconds(30) }, 1_000_000);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tool(service).GetFile(f, cancellationToken: cts.Token));
        hang.Release();
    }

    private sealed class HangingConverter : IContentConverter
    {
        private readonly ManualResetEventSlim _release = new();
        public void Release() => _release.Set();
        public bool CanConvert(FileKind kind) => kind == FileKind.Pdf;
        public string Describe(FileKind kind) => "";
        public string Convert(Stream input, FileKind kind, ConversionBudget budget)
        {
            _release.Wait(TimeSpan.FromSeconds(20));
            return "done";
        }
    }

    // ---- the description teaches the model the fallback --------------------------------------------------------

    [Fact]
    public void Tool_and_parameter_descriptions_carry_the_retry_instruction()
    {
        var method = typeof(FileTool).GetMethod(nameof(FileTool.GetFile))!;
        var description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
        Assert.Contains("call get_file again", description);
        Assert.Contains("accept=\"text/*\"", description);
        Assert.Contains("does not support pdf input", description);
        Assert.Contains("scanned PDF", description);
        Assert.Contains("PAGE IMAGES", description);
        Assert.Contains("leave image types out of", description);

        var acceptParam = method.GetParameters().Single(p => p.Name == "accept");
        Assert.Equal(typeof(string), acceptParam.ParameterType); // flat string, per the schema rule
        Assert.Equal("text/*", acceptParam.DefaultValue);
        Assert.Contains("retry with accept=\"text/*\"", acceptParam.GetCustomAttribute<DescriptionAttribute>()!.Description);
    }
}
