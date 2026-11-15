using System.IO.Compression;
using System.Text;
using System.Xml;

namespace WsSearchMcp.Http.Conversion;

/// <summary>
/// Text extraction from docx/xlsx/pptx (and the macro-enabled variants) with <c>System.IO.Compression</c>
/// and <c>System.Xml</c> only. Document structure is resolved through the package relationships; sheet and
/// slide order come from the workbook / presentation, never from part file names.
/// </summary>
public sealed class OoxmlTextConverter : IContentConverter
{
    private const string Wml = "wordprocessingml";
    private const string Sml = "spreadsheetml";
    private const string Pml = "presentationml";
    private const string Dml = "drawingml";
    private const int MaxColumns = 16384;

    public bool CanConvert(FileKind kind) => FileClassifier.IsOoxml(kind);

    public string Describe(FileKind kind) => kind switch
    {
        FileKind.OoxmlWord => "Text of the document body only; headers, footers, footnotes, comments, images and layout are not included.",
        FileKind.OoxmlExcel => "Cell values as stored, one TSV block per sheet; dates appear as Excel serial numbers; formatting, charts and images are not included.",
        FileKind.OoxmlPowerPoint => "Slide text only, in presentation order; speaker notes, images and layout are not included.",
        _ => "",
    };

    public string Convert(Stream input, FileKind kind, ConversionBudget budget)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException ex) { throw new ConversionException("The Office file is corrupt or not a valid package.", ex); }

        using (zip)
        {
            try
            {
                var pkg = OoxmlPackage.Open(zip, budget);
                if (pkg.MainPart is null)
                    throw new ConversionException("The Office file has no main document part.");
                var sb = new StringBuilder();
                switch (kind)
                {
                    case FileKind.OoxmlWord: Word(pkg, sb, budget); break;
                    case FileKind.OoxmlExcel: Excel(pkg, sb, budget); break;
                    case FileKind.OoxmlPowerPoint: PowerPoint(pkg, sb, budget); break;
                    default: throw new ConversionException("Not an Office Open XML document.");
                }
                var text = sb.ToString().Trim();
                if (text.Length == 0) throw new ConversionException("The document contains no extractable text.");
                return text;
            }
            catch (XmlException ex) { throw new ConversionException("The Office file contains malformed XML.", ex); }
            catch (InvalidDataException ex) { throw new ConversionException("The Office file is corrupt.", ex); }
        }
    }

    // ---- Word ------------------------------------------------------------------------------------

    private static void Word(OoxmlPackage pkg, StringBuilder sb, ConversionBudget budget)
    {
        using var r = pkg.OpenXml(pkg.MainPart!) ?? throw new ConversionException("The Word document body is missing.");
        var cellDepth = 0;
        var fallbackDepth = 0; // mc:Fallback repeats the text of the mc:Choice before it
        var inText = false;

        while (r.Read())
        {
            budget.CheckOutput(sb.Length);
            var ns = r.NamespaceURI;
            if (r.NodeType == XmlNodeType.Element)
            {
                if (r.LocalName == "Fallback" && ns.Contains("markup-compatibility", StringComparison.Ordinal))
                {
                    if (!r.IsEmptyElement) fallbackDepth++;
                    continue;
                }
                if (fallbackDepth > 0 || !OoxmlPackage.IsNs(ns, Wml)) continue;
                switch (r.LocalName)
                {
                    case "t": inText = !r.IsEmptyElement; break;
                    case "tab": sb.Append('\t'); break;
                    case "br" or "cr": sb.Append('\n'); break;
                    case "tc": if (!r.IsEmptyElement) cellDepth++; break;
                }
            }
            else if (r.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace && inText)
            {
                if (fallbackDepth == 0 && inText) sb.Append(r.Value);
            }
            else if (r.NodeType == XmlNodeType.EndElement)
            {
                if (r.LocalName == "Fallback" && ns.Contains("markup-compatibility", StringComparison.Ordinal))
                {
                    if (fallbackDepth > 0) fallbackDepth--;
                    continue;
                }
                if (fallbackDepth > 0 || !OoxmlPackage.IsNs(ns, Wml)) continue;
                switch (r.LocalName)
                {
                    case "t": inText = false; break;
                    case "p": sb.Append(cellDepth > 0 ? ' ' : '\n'); break;
                    case "tc":
                        if (cellDepth > 0) cellDepth--;
                        TrimEnd(sb, ' ');
                        sb.Append('\t');
                        break;
                    case "tr":
                        TrimEnd(sb, '\t', ' ');
                        sb.Append('\n');
                        break;
                }
            }
        }
    }

    // ---- Excel -----------------------------------------------------------------------------------

    private static void Excel(OoxmlPackage pkg, StringBuilder sb, ConversionBudget budget)
    {
        var workbook = pkg.MainPart!;
        var rels = ToMap(pkg.RelationshipsOf(workbook));

        // Sheets in workbook order, each mapped r:id -> part through the workbook relationships.
        var sheets = new List<(string Name, string? Part, bool Hidden)>();
        using (var r = pkg.OpenXml(workbook) ?? throw new ConversionException("The workbook part is missing."))
        {
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName != "sheet" || !OoxmlPackage.IsNs(r.NamespaceURI, Sml)) continue;
                var name = r.GetAttribute("name") ?? "Sheet" + (sheets.Count + 1);
                var hidden = (r.GetAttribute("state") ?? "visible") != "visible";
                var rid = OoxmlPackage.RelAttribute(r, "id");
                string? part = rid is not null && rels.TryGetValue(rid, out var rel) ? rel.Target : null;
                sheets.Add((name, part, hidden));
            }
        }
        if (sheets.Count == 0) throw new ConversionException("The workbook has no sheets.");

        var shared = LoadSharedStrings(pkg, rels.Values.FirstOrDefault(x => x.Type.EndsWith("/sharedStrings", StringComparison.OrdinalIgnoreCase)).Target, budget);

        foreach (var (name, part, hidden) in sheets)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("--- sheet: ").Append(name).Append(hidden ? " (hidden)" : "").Append(" ---\n");
            if (part is null || !pkg.Has(part)) { sb.Append("(sheet part missing)\n"); continue; }
            ReadSheet(pkg, part, shared, sb, budget);
        }
    }

    private static List<string> LoadSharedStrings(OoxmlPackage pkg, string? part, ConversionBudget budget)
    {
        var list = new List<string>();
        if (part is null) return list;
        using var r = pkg.OpenXml(part);
        if (r is null) return list;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "si" && OoxmlPackage.IsNs(r.NamespaceURI, Sml))
            {
                if (r.IsEmptyElement) { list.Add(""); continue; }
                using var sub = r.ReadSubtree();
                list.Add(ReadRichText(sub));
                budget.CheckOutput(list.Count);
            }
        }
        return list;
    }

    /// <summary>Concatenates the text runs (<c>t</c>) of a string item, skipping phonetic hints (<c>rPh</c>).</summary>
    private static string ReadRichText(XmlReader r)
    {
        var sb = new StringBuilder();
        var phonetic = 0;
        var inT = false;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                if (r.LocalName == "rPh" && !r.IsEmptyElement) phonetic++;
                else if (r.LocalName == "t" && !r.IsEmptyElement) inT = true;
            }
            else if (r.NodeType == XmlNodeType.EndElement)
            {
                if (r.LocalName == "rPh" && phonetic > 0) phonetic--;
                else if (r.LocalName == "t") inT = false;
            }
            else if (inT && phonetic == 0 && r.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
            {
                sb.Append(r.Value);
            }
        }
        return sb.ToString();
    }

    private static void ReadSheet(OoxmlPackage pkg, string part, List<string> shared, StringBuilder sb, ConversionBudget budget)
    {
        using var r = pkg.OpenXml(part)!;
        var cells = new List<string>();
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "row" && OoxmlPackage.IsNs(r.NamespaceURI, Sml))
            {
                cells.Clear();
                if (!r.IsEmptyElement)
                {
                    using var row = r.ReadSubtree();
                    row.Read(); // onto <row>
                    while (row.Read())
                    {
                        if (row.NodeType != XmlNodeType.Element || row.LocalName != "c") continue;
                        var col = ColumnOf(row.GetAttribute("r"), cells.Count);
                        var type = row.GetAttribute("t") ?? "n";
                        string value;
                        if (row.IsEmptyElement) value = "";
                        else { using var c = row.ReadSubtree(); value = ReadCell(c, type, shared); }

                        while (cells.Count < col) cells.Add("");
                        if (cells.Count == col) cells.Add(Clean(value)); else cells[col] = Clean(value);
                    }
                }
                var end = cells.Count;
                while (end > 0 && cells[end - 1].Length == 0) end--;
                if (end > 0)
                {
                    sb.Append(string.Join('\t', cells.Take(end))).Append('\n');
                    budget.CheckOutput(sb.Length);
                }
            }
        }
    }

    private static string ReadCell(XmlReader c, string type, List<string> shared)
    {
        string? v = null;
        string? inline = null;
        c.Read(); // onto <c>
        var more = c.Read();
        while (more)
        {
            if (c.NodeType == XmlNodeType.Element && c.LocalName == "v" && !c.IsEmptyElement)
            {
                v = c.ReadElementContentAsString(); // leaves the reader on the node after </v>
                continue;
            }
            if (c.NodeType == XmlNodeType.Element && c.LocalName == "is" && !c.IsEmptyElement)
            {
                using (var sub = c.ReadSubtree()) inline = ReadRichText(sub);
            }
            more = c.Read();
        }
        return type switch
        {
            "s" => int.TryParse(v, out var i) && i >= 0 && i < shared.Count ? shared[i] : "",
            "inlineStr" => inline ?? v ?? "",
            "b" => v == "1" ? "TRUE" : v == "0" ? "FALSE" : v ?? "",
            _ => v ?? inline ?? "", // str (formula string), e (error text), n, d
        };
    }

    /// <summary>Zero-based column from a cell reference such as "C5"; falls back to <paramref name="next"/> when missing.</summary>
    public static int ColumnOf(string? reference, int next)
    {
        if (string.IsNullOrEmpty(reference)) return next;
        var col = 0;
        var i = 0;
        for (; i < reference.Length && char.IsAsciiLetter(reference[i]); i++)
            col = col * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        if (i == 0) return next;
        return Math.Clamp(col - 1, 0, MaxColumns - 1);
    }

    private static string Clean(string s) =>
        s.IndexOfAny(new[] { '\t', '\r', '\n' }) < 0 ? s : s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    // ---- PowerPoint ---------------------------------------------------------------------------------

    private static void PowerPoint(OoxmlPackage pkg, StringBuilder sb, ConversionBudget budget)
    {
        var presentation = pkg.MainPart!;
        var rels = ToMap(pkg.RelationshipsOf(presentation)).ToDictionary(kv => kv.Key, kv => kv.Value.Target, StringComparer.Ordinal);

        var slides = new List<string>();
        using (var r = pkg.OpenXml(presentation) ?? throw new ConversionException("The presentation part is missing."))
        {
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName != "sldId" || !OoxmlPackage.IsNs(r.NamespaceURI, Pml)) continue;
                var rid = OoxmlPackage.RelAttribute(r, "id");
                if (rid is not null && rels.TryGetValue(rid, out var target)) slides.Add(target);
            }
        }
        if (slides.Count == 0) throw new ConversionException("The presentation has no slides.");

        for (var n = 0; n < slides.Count; n++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("--- slide ").Append(n + 1).Append(" ---\n");
            using var r = pkg.OpenXml(slides[n]);
            if (r is null) { sb.Append("(slide part missing)\n"); continue; }
            var inT = false;
            while (r.Read())
            {
                budget.CheckOutput(sb.Length);
                if (r.NodeType == XmlNodeType.Element && OoxmlPackage.IsNs(r.NamespaceURI, Dml))
                {
                    if (r.LocalName == "t") inT = !r.IsEmptyElement;
                    else if (r.LocalName == "br") sb.Append('\n');
                }
                else if (r.NodeType == XmlNodeType.EndElement && OoxmlPackage.IsNs(r.NamespaceURI, Dml))
                {
                    if (r.LocalName == "t") inT = false;
                    else if (r.LocalName == "p") sb.Append('\n');
                }
                else if (inT && r.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
                {
                    sb.Append(r.Value);
                }
            }
        }
    }

    private static Dictionary<string, (string Id, string Type, string Target)> ToMap(List<(string Id, string Type, string Target)> rels)
    {
        var map = new Dictionary<string, (string Id, string Type, string Target)>(StringComparer.Ordinal);
        foreach (var r in rels) map.TryAdd(r.Id, r);
        return map;
    }

    private static void TrimEnd(StringBuilder sb, params char[] chars)
    {
        var n = sb.Length;
        while (n > 0 && Array.IndexOf(chars, sb[n - 1]) >= 0) n--;
        sb.Length = n;
    }
}
