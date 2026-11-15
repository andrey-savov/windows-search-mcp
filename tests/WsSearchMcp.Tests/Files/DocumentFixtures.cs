using System.IO.Compression;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace WsSearchMcp.Tests.Files;

/// <summary>Builds small, valid PDF / docx / xlsx / pptx files in memory.</summary>
public static class DocumentFixtures
{
    private const string PkgRels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string OfficeRels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static byte[] Zip(params (string Name, string Content)[] parts) =>
        Zip(parts.Select(p => (p.Name, Encoding.UTF8.GetBytes(p.Content))).ToArray());

    public static byte[] Zip(params (string Name, byte[] Content)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in parts)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = e.Open();
                s.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static string Types(string mainPart, string mainType) =>
        $"<Types xmlns=\"{ContentTypes}\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        $"<Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"{mainPart}\" ContentType=\"{mainType}\"/></Types>";

    private static string RootRels(string target) =>
        $"<Relationships xmlns=\"{PkgRels}\"><Relationship Id=\"rId1\" Type=\"{OfficeRels}/officeDocument\" Target=\"{target}\"/></Relationships>";

    // ---- docx ----------------------------------------------------------------------------------

    public const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static byte[] Docx(string bodyXml) => Zip(
        ("[Content_Types].xml", Types("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")),
        ("_rels/.rels", RootRels("word/document.xml")),
        ("word/document.xml", $"<w:document xmlns:w=\"{W}\"><w:body>{bodyXml}</w:body></w:document>"));

    public static string Para(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(text)}</w:t></w:r></w:p>";

    // ---- xlsx (sheet parts deliberately NOT in workbook order) ---------------------------------

    public static byte[] Xlsx() => Zip(
        ("[Content_Types].xml", Types("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
        ("_rels/.rels", RootRels("xl/workbook.xml")),
        ("xl/workbook.xml",
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " + $"xmlns:r=\"{OfficeRels}\">" +
            "<sheets><sheet name=\"Beta\" sheetId=\"1\" r:id=\"rId2\"/><sheet name=\"Alpha\" sheetId=\"2\" state=\"hidden\" r:id=\"rId1\"/></sheets></workbook>"),
        // Beta (first in the workbook) lives in sheet1.xml via rId2; Alpha in sheet2.xml via rId1: not ordinal.
        ("xl/_rels/workbook.xml.rels",
            $"<Relationships xmlns=\"{PkgRels}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{OfficeRels}/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{OfficeRels}/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            $"<Relationship Id=\"rId3\" Type=\"{OfficeRels}/sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>"),
        ("xl/sharedStrings.xml",
            "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<si><t>Hello</t></si>" +
            "<si><r><t xml:space=\"preserve\">Rich </t></r><r><t>text</t></r><rPh sb=\"0\" eb=\"1\"><t>IGNORED</t></rPh></si></sst>"),
        ("xl/worksheets/sheet1.xml",
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"inlineStr\"><is><t>inline</t></is></c><c r=\"C1\" t=\"str\"><f>x</f><v>fx</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"b\"><v>1</v></c><c r=\"C2\" t=\"e\"><v>#DIV/0!</v></c><c r=\"D2\"><v>42</v></c></row>" +
            "<row r=\"3\"/>" +
            "<row r=\"4\"><c r=\"C4\" t=\"s\"><v>1</v></c></row>" +
            "</sheetData></worksheet>"),
        ("xl/worksheets/sheet2.xml",
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>second</t></is></c></row></sheetData></worksheet>"));

    // ---- pptx (presentation order differs from slideN.xml numbering) ---------------------------

    public static byte[] Pptx() => Zip(
        ("[Content_Types].xml", Types("/ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml")),
        ("_rels/.rels", RootRels("ppt/presentation.xml")),
        ("ppt/presentation.xml",
            "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " + $"xmlns:r=\"{OfficeRels}\">" +
            "<p:sldIdLst><p:sldId id=\"256\" r:id=\"rId2\"/><p:sldId id=\"257\" r:id=\"rId1\"/></p:sldIdLst></p:presentation>"),
        ("ppt/_rels/presentation.xml.rels",
            $"<Relationships xmlns=\"{PkgRels}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{OfficeRels}/slide\" Target=\"slides/slide1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{OfficeRels}/slide\" Target=\"slides/slide2.xml\"/></Relationships>"),
        ("ppt/slides/slide1.xml", Slide("Title One")),
        ("ppt/slides/slide2.xml", Slide("Title Two")));

    private static string Slide(string text) =>
        "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
        $"<p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>";

    // ---- pdf -------------------------------------------------------------------------------------

    /// <summary>A PDF with one page per entry; an empty string makes a page without any text (like a scan).</summary>
    public static byte[] Pdf(params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(PageSize.A4);
            if (text.Length > 0) page.AddText(text, 12, new PdfPoint(40, 700), font);
        }
        return builder.Build();
    }

    // ---- zip tampering ---------------------------------------------------------------------------

    /// <summary>Rewrites every declared uncompressed size in a zip's local and central headers (the sizes are attacker-controlled).</summary>
    public static byte[] WithDeclaredUncompressedSize(byte[] zip, uint declared)
    {
        var copy = (byte[])zip.Clone();
        for (var i = 0; i + 4 <= copy.Length; i++)
        {
            if (copy[i] != 'P' || copy[i + 1] != 'K') continue;
            var offset = (copy[i + 2], copy[i + 3]) switch
            {
                (3, 4) => 22,  // local file header
                (1, 2) => 24,  // central directory header
                _ => -1,
            };
            if (offset > 0 && i + offset + 4 <= copy.Length) BitConverter.GetBytes(declared).CopyTo(copy, i + offset);
        }
        return copy;
    }
}
