using System.Text.Json;

namespace WsSearchMcp.Files;

/// <summary>What the Windows Search index knows about one file.</summary>
public sealed record IndexedFile(string Path, long Size, bool IsDirectory, DateTimeOffset? Modified);

/// <summary>
/// Looks a path up in the Windows Search index (exact match on <c>System.ItemPathDisplay</c>, which
/// is case-insensitive). The index is an allowlist and supplies the canonical path that is opened —
/// never the caller's string. All SQL goes through <see cref="WindowsSearchTool.Search"/>, so
/// escaping and COM handling are shared with <c>find_files</c>.
/// </summary>
public sealed class IndexedFileResolver
{
    /// <summary>(filtersJson, extraColumns, maxResults) → the JSON array <c>find_files</c> returns.</summary>
    public delegate string SearchFunction(string filtersJson, string columns, int maxResults);

    private const string Columns = "System.Size,System.FileAttributes,System.DateModified";
    private const int FallbackMaxResults = 100;

    private readonly SearchFunction _search;

    public IndexedFileResolver(SearchFunction search) => _search = search;

    /// <summary>The real index, via the same code path as the <c>find_files</c> tool.</summary>
    public static IndexedFileResolver CreateDefault() => new((filters, columns, max) =>
        WindowsSearchTool.Search(text: "", scope: "", maxResults: max, mode: "contains",
            columns: columns, filters: filters, orderBy: "", orderDir: "ASC", filterLogic: "AND"));

    public IndexedFile? Resolve(string path)
    {
        if (!FileAccessPolicy.TryNormalize(path, out var wanted).Allowed) return null;

        // 1. Exact path match.
        var exact = Parse(_search(Filter(("System.ItemPathDisplay", "=", wanted)), Columns, 5), wanted);
        if (exact is not null) return exact;

        // 2. Entries whose ItemPathDisplay is empty are repaired by find_files from their DP:// ItemUrl, so
        //    an exact match on the column cannot find them. Match on the file name among DP:// entries and
        //    accept only a row whose repaired path equals the requested one.
        var name = System.IO.Path.GetFileName(wanted);
        if (name.Length == 0) return null;
        return Parse(_search(
            Filter(("System.FileName", "=", name), ("System.ItemUrl", "LIKE", "DP://%")),
            Columns, FallbackMaxResults), wanted);
    }

    private static string Filter(params (string Property, string Op, string Value)[] filters) =>
        JsonSerializer.Serialize(filters.Select(f => new { property = f.Property, op = f.Op, value = f.Value }));

    private static IndexedFile? Parse(string json, string wanted)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (!row.TryGetProperty("System.ItemPathDisplay", out var p) || p.ValueKind != JsonValueKind.String) continue;
                var indexed = p.GetString()!;
                if (!FileAccessPolicy.TryNormalize(indexed, out var canonical).Allowed) continue;
                if (!canonical.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;

                var size = row.TryGetProperty("System.Size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var sv) ? sv : 0;
                var attrs = row.TryGetProperty("System.FileAttributes", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out var av) ? av : 0;
                DateTimeOffset? modified = row.TryGetProperty("System.DateModified", out var m)
                    && m.ValueKind == JsonValueKind.String && m.TryGetDateTimeOffset(out var mv) ? mv : null;
                return new IndexedFile(canonical, size, (attrs & (long)FileAttributes.Directory) != 0, modified);
            }
        }
        return null;
    }
}
