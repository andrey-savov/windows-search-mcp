using System.IO.Compression;
using System.Xml;

namespace WsSearchMcp.Http.Conversion;

public enum OoxmlKind { Unknown, Word, Excel, PowerPoint }

/// <summary>
/// Read access to an Open Packaging Convention (docx/xlsx/pptx) zip. Parts are found through the
/// package relationships, never by assuming file names, and every byte read is counted against a
/// <see cref="ConversionBudget"/>. The caller owns the <see cref="ZipArchive"/>.
/// </summary>
public sealed class OoxmlPackage
{

    private readonly ZipArchive _zip;
    private readonly ConversionBudget _budget;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public string? MainPart { get; }
    public OoxmlKind Kind { get; }

    private OoxmlPackage(ZipArchive zip, ConversionBudget budget)
    {
        _zip = zip;
        _budget = budget;
        foreach (var e in zip.Entries)
            _entries.TryAdd(NormalizePart(e.FullName), e);

        MainPart = ReadRelationships("_rels/.rels")
            .FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase)).Target;
        Kind = DetectKind();
    }

    public static OoxmlPackage Open(ZipArchive zip, ConversionBudget budget) => new(zip, budget);

    // ---- parts ---------------------------------------------------------------------------------

    /// <summary>Canonical form of a part name: forward slashes, no leading slash, no "." / ".." segments.</summary>
    public static string NormalizePart(string name)
    {
        var parts = new List<string>();
        foreach (var seg in name.Replace('\\', '/').Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return string.Join("/", parts);
    }

    /// <summary>Resolves a relationship target against the folder of the part that owns the relationship.</summary>
    public static string ResolveTarget(string ownerPart, string target)
    {
        target = Uri.UnescapeDataString(target);
        if (target.StartsWith('/')) return NormalizePart(target);
        var slash = ownerPart.LastIndexOf('/');
        var dir = slash >= 0 ? ownerPart[..slash] : "";
        return NormalizePart(dir.Length == 0 ? target : dir + "/" + target);
    }

    public bool Has(string part) => _entries.ContainsKey(NormalizePart(part));

    /// <summary>Opens a part for reading through the counting stream; null if the part does not exist.</summary>
    public Stream? OpenPart(string part)
    {
        if (!_entries.TryGetValue(NormalizePart(part), out var entry)) return null;
        return new CountingLimitStream(entry.Open(), _budget);
    }

    public XmlReader? OpenXml(string part)
    {
        var s = OpenPart(part);
        return s is null ? null : CreateReader(s);
    }

    public static XmlReader CreateReader(Stream s) => XmlReader.Create(s, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CloseInput = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    });

    /// <summary>Relationships of <paramref name="relsPart"/> as (Id, Type, absolute target part). External targets are skipped.</summary>
    public List<(string Id, string Type, string Target)> ReadRelationships(string relsPart)
    {
        var result = new List<(string, string, string)>();
        using var reader = OpenXml(relsPart);
        if (reader is null) return result;

        // "word/_rels/document.xml.rels" belongs to "word/document.xml"; "_rels/.rels" to the package root.
        var owner = OwnerOf(relsPart);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
            var id = reader.GetAttribute("Id");
            var type = reader.GetAttribute("Type");
            var target = reader.GetAttribute("Target");
            if (id is null || type is null || target is null) continue;
            if (string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add((id, type, ResolveTarget(owner, target)));
        }
        return result;
    }

    public List<(string Id, string Type, string Target)> RelationshipsOf(string part)
    {
        var n = NormalizePart(part);
        var slash = n.LastIndexOf('/');
        var rels = slash >= 0 ? n[..slash] + "/_rels/" + n[(slash + 1)..] + ".rels" : "_rels/" + n + ".rels";
        return ReadRelationships(rels);
    }

    private static string OwnerOf(string relsPart)
    {
        var n = NormalizePart(relsPart);
        var marker = n.LastIndexOf("_rels/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return "";
        var folder = n[..marker];
        var file = n[(marker + "_rels/".Length)..];
        if (file.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) file = file[..^".rels".Length];
        return file.Length == 0 ? folder : folder + file;
    }

    // ---- classification --------------------------------------------------------------------------

    private OoxmlKind DetectKind()
    {
        if (MainPart is null || !Has(MainPart)) return OoxmlKind.Unknown;

        // The main part's declared content type is authoritative.
        using (var reader = OpenXml("[Content_Types].xml"))
        {
            while (reader is not null && reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Override") continue;
                var part = reader.GetAttribute("PartName");
                var type = reader.GetAttribute("ContentType");
                if (part is null || type is null || !NormalizePart(part).Equals(MainPart, StringComparison.OrdinalIgnoreCase)) continue;
                if (type.Contains("wordprocessingml", StringComparison.OrdinalIgnoreCase)) return OoxmlKind.Word;
                if (type.Contains("spreadsheetml", StringComparison.OrdinalIgnoreCase)) return OoxmlKind.Excel;
                if (type.Contains("presentationml", StringComparison.OrdinalIgnoreCase)) return OoxmlKind.PowerPoint;
                break;
            }
        }
        // No usable override: fall back to where the main part lives.
        if (MainPart.StartsWith("word/", StringComparison.OrdinalIgnoreCase)) return OoxmlKind.Word;
        if (MainPart.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) return OoxmlKind.Excel;
        if (MainPart.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase)) return OoxmlKind.PowerPoint;
        return OoxmlKind.Unknown;
    }

    // ---- namespace helpers (transitional and strict) ------------------------------------------------

    /// <summary>True if <paramref name="uri"/> is the given OOXML markup namespace in its transitional or strict form.</summary>
    public static bool IsNs(string uri, string markup) =>
        uri.EndsWith("/" + markup + "/2006/main", StringComparison.Ordinal)
        || uri.Equals("http://purl.oclc.org/ooxml/" + markup + "/main", StringComparison.Ordinal);

    /// <summary>The relationship id (<c>r:id</c> / <c>r:embed</c> style attribute with the given local name), whatever its prefix.</summary>
    public static string? RelAttribute(XmlReader reader, string localName)
    {
        if (!reader.HasAttributes) return null;
        for (var ok = reader.MoveToFirstAttribute(); ok; ok = reader.MoveToNextAttribute())
        {
            if (reader.LocalName == localName
                && (reader.NamespaceURI.EndsWith("/officeDocument/2006/relationships", StringComparison.Ordinal)
                    || reader.NamespaceURI.Equals("http://purl.oclc.org/ooxml/officeDocument/relationships", StringComparison.Ordinal)))
            {
                var v = reader.Value;
                reader.MoveToElement();
                return v;
            }
        }
        reader.MoveToElement();
        return null;
    }
}
