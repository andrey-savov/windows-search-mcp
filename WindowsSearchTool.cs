using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WsSearchMcp;

public sealed class SearchFilter
{
    [Description(
        "System.* property name to filter on. " +
        "Examples: 'System.DateModified', 'System.DateCreated', 'System.DateAccessed', " +
        "'System.Size', 'System.Kind', 'System.ItemType', 'System.Author', " +
        "'System.Photo.DateTaken', 'System.Photo.CameraModel', 'System.Image.Dimensions', " +
        "'System.Music.Artist'.")]
    public string Property { get; set; } = "";

    [Description(
        "Operator. One of: '=', '!=', '<>', '<', '<=', '>', '>=', " +
        "'LIKE' (use % wildcards), 'NOT LIKE', " +
        "'IS NULL', 'IS NOT NULL' (Value is ignored), " +
        "'CONTAINS' (full-text match against this specific property).")]
    public string Op { get; set; } = "=";

    [Description(
        "Value to compare against, encoded as a string. For dates use ISO 8601 " +
        "(e.g. '2024-01-01' or '2024-01-01T15:30:00'). Leave null for IS NULL / IS NOT NULL.")]
    public string? Value { get; set; }

    [Description(
        "How Value is rendered as a SQL literal: " +
        "'text' (default; quoted string — also used for dates), " +
        "'number' (bare numeric literal), " +
        "'bool' (rendered as 1 or 0).")]
    public string ValueType { get; set; } = "text";
}

[McpServerToolType]
[SupportedOSPlatform("windows")]
public static class WindowsSearchTool
{
    private static readonly Regex PropertyNameRegex =
        new(@"^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.Compiled);

    private static readonly HashSet<string> ComparisonOps =
        new(StringComparer.Ordinal) { "=", "!=", "<>", "<", "<=", ">", ">=" };

    private static readonly HashSet<string> LikeOps =
        new(StringComparer.OrdinalIgnoreCase) { "LIKE", "NOT LIKE" };

    internal static (string sql, string? scopeUri, List<string> cols) BuildSql(
        string? query,
        string? scope,
        int maxResults,
        string mode,
        string[]? columns,
        SearchFilter[]? filters = null,
        string? orderBy = null,
        string? orderDir = null)
    {
        if (maxResults < 1) maxResults = 1;
        if (maxResults > 1000) maxResults = 1000;

        var cols = new List<string> { "System.ItemPathDisplay" };
        if (columns is not null)
        {
            foreach (var p in columns)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (!cols.Contains(p, StringComparer.OrdinalIgnoreCase))
                    cols.Add(p);
            }
        }

        var clauses = new List<string>();
        if (!string.IsNullOrEmpty(query))
            clauses.Add(BuildQueryClause(query, mode));

        if (filters is not null)
        {
            foreach (var f in filters)
                clauses.Add(RenderFilter(f));
        }

        if (clauses.Count == 0)
            throw new ArgumentException("At least one of 'query' or 'filters' must be provided.");

        var where = string.Join(" AND ", clauses);
        var sql = $"SELECT TOP {maxResults} {string.Join(", ", cols)} FROM SYSTEMINDEX WHERE {where}";

        if (scope is null)
            scope = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string? scopeUri = null;
        if (!string.IsNullOrEmpty(scope))
        {
            scopeUri = $"file:{scope}";
            sql += $" AND SCOPE='{EscapeSqlLiteral(scopeUri)}'";
        }

        if (!string.IsNullOrEmpty(orderBy))
        {
            ValidatePropertyName(orderBy);
            var dir = string.IsNullOrWhiteSpace(orderDir) ? "ASC" : orderDir.Trim().ToUpperInvariant();
            if (dir != "ASC" && dir != "DESC")
                throw new ArgumentException($"orderDir must be 'ASC' or 'DESC', got '{orderDir}'.", nameof(orderDir));
            sql += $" ORDER BY {orderBy} {dir}";
        }

        return (sql, scopeUri, cols);
    }

    private static string BuildQueryClause(string query, string mode) =>
        mode.ToLowerInvariant() switch
        {
            "contains" => "(CONTAINS('\"" + EscapeSqlLiteral(query) + "\"') OR System.FileName LIKE '%" + EscapeSqlLiteral(query) + "%')",
            "filename" => $"System.FileName LIKE '%{EscapeSqlLiteral(query)}%'",
            "raw" => query,
            _ => throw new ArgumentException(
                $"Unknown mode '{mode}'. Expected one of: contains, filename, raw.", nameof(mode))
        };

    private static string RenderFilter(SearchFilter f)
    {
        if (f is null) throw new ArgumentException("Filter cannot be null.", nameof(f));
        ValidatePropertyName(f.Property);
        var op = (f.Op ?? "=").Trim();
        var opUpper = op.ToUpperInvariant();

        if (opUpper is "IS NULL" or "IS NOT NULL")
            return $"{f.Property} {opUpper}";

        if (opUpper == "CONTAINS")
        {
            if (f.Value is null)
                throw new ArgumentException(
                    $"Operator 'CONTAINS' requires a value (property: {f.Property}).");
            return $"CONTAINS({f.Property}, '{EscapeSqlLiteral(f.Value)}')";
        }

        var isComparison = ComparisonOps.Contains(op);
        var isLike = LikeOps.Contains(opUpper);
        if (!isComparison && !isLike)
            throw new ArgumentException(
                $"Unsupported operator '{f.Op}' on property '{f.Property}'.", nameof(f));

        if (f.Value is null)
            throw new ArgumentException(
                $"Operator '{op}' requires a value (property: {f.Property}). " +
                "Use 'IS NULL' / 'IS NOT NULL' to test for missing values.");

        var literal = RenderValueLiteral(f.Value, f.ValueType, f.Property);
        var sqlOp = isLike ? opUpper : op;
        return $"{f.Property} {sqlOp} {literal}";
    }

    private static string RenderValueLiteral(string value, string? valueType, string property)
    {
        switch ((valueType ?? "text").Trim().ToLowerInvariant())
        {
            case "":
            case "text":
            case "string":
            case "date":
            case "datetime":
                return $"'{EscapeSqlLiteral(value)}'";

            case "number":
            case "numeric":
            case "int":
            case "long":
            case "double":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw new ArgumentException(
                        $"Value '{value}' for property '{property}' is not a valid number.");
                return value;

            case "bool":
            case "boolean":
                return value.Trim().ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" => "1",
                    "false" or "0" or "no" => "0",
                    _ => throw new ArgumentException(
                        $"Value '{value}' for property '{property}' is not a valid boolean.")
                };

            default:
                throw new ArgumentException(
                    $"Unknown valueType '{valueType}' for property '{property}'. " +
                    "Expected one of: text, number, bool.");
        }
    }

    private static void ValidatePropertyName(string name)
    {
        if (string.IsNullOrEmpty(name) || !PropertyNameRegex.IsMatch(name))
            throw new ArgumentException(
                $"Invalid property name '{name}'. Must match [A-Za-z_][A-Za-z0-9_.]*.");
    }

    private static string EscapeSqlLiteral(string s) => s.Replace("'", "''");

    [McpServerTool]
    [Description(
        "List the System.* properties available on this machine that belong to a given family. " +
        "A family is the segment after 'System.' (e.g. 'Photo' for System.Photo.*, 'Music' for " +
        "System.Music.*). Pass '' or 'System' for the root family (properties like System.DateModified, " +
        "System.FileName, System.Size — no second dot segment). Returns a JSON array of " +
        "{canonicalName, displayName, type}.")]
    public static string ListPropertiesByFamily(
        [Description("Family name, e.g. 'Photo', 'Music', 'Video', 'Document', 'GPS', 'Message'. Pass '' for root System.* properties.")]
            string family,
        [Description("Maximum number of properties to return. Clamped to [1, 2000]. Default 500.")]
            int maxResults = 500)
    {
        if (maxResults < 1) maxResults = 1;
        if (maxResults > 2000) maxResults = 2000;
        var matches = WindowsPropertySystem.ByFamily(family).Take(maxResults).ToList();
        return JsonSerializer.Serialize(matches);
    }

    [McpServerTool]
    [Description(
        "List/search all System.* properties available on this machine. Optionally filters by a " +
        "case-insensitive substring matched against both the canonical name (e.g. 'System.Photo.DateTaken') " +
        "and the human-readable display name (e.g. 'Date taken'). Returns a JSON array of " +
        "{canonicalName, displayName, type}.")]
    public static string ListProperties(
        [Description("Optional case-insensitive substring filter (matched against canonical name and display name). Omit to list everything.")]
            string? filter = null,
        [Description("Maximum number of properties to return. Clamped to [1, 2000]. Default 500.")]
            int maxResults = 500)
    {
        if (maxResults < 1) maxResults = 1;
        if (maxResults > 2000) maxResults = 2000;
        var matches = WindowsPropertySystem.Search(filter).Take(maxResults).ToList();
        return JsonSerializer.Serialize(matches);
    }

    [McpServerTool(Name = "query")]
    [Description(
        "Search the local Windows Search index (SYSTEMINDEX) via ADODB + Search.CollatorDSO. " +
        "Supports full-text queries, filename matching, structured filters on any indexed " +
        "System.* property (dates, sizes, EXIF, music metadata, etc.), and ordering. Returns a " +
        "JSON array of matching items with their requested indexed properties.")]
    public static string Search(
        [Description(
            "Search query. With mode='contains' (default), matches a CONTAINS() full-text " +
            "argument OR a filename LIKE '%text%' (so name-only hits aren't missed). " +
            "With mode='filename', only filename LIKE '%text%'. " +
            "With mode='raw', used verbatim as the WHERE clause (caller is responsible for valid " +
            "AQS / SQL). Pass '' to search by 'filters' alone.")]
            string text,
        [Description(
            "Optional directory to limit the search to (e.g. 'C:\\\\Users\\\\me\\\\Documents'). " +
            "Defaults to the user profile directory. Pass '' to search the entire index.")]
            string? scope = null,
        [Description("Maximum number of results to return. Clamped to [1, 1000]. Default 50.")]
            int maxResults = 50,
        [Description("Match mode for 'text'. One of: 'contains' (default), 'filename', 'raw'.")]
            string mode = "contains",
        [Description(
            "Additional System.* property names to include in each result row. " +
            "System.ItemPathDisplay is always included. " +
            "Windows Search exposes ~600 properties grouped into families: " +
            "root System.* (DateModified, DateCreated, DateAccessed, FileName, Size, ItemType, " +
            "Author, Title, Kind, Keywords, Rating, …), " +
            "System.Photo.* (DateTaken, CameraModel, FNumber, ISOSpeed, FocalLength, Orientation, …), " +
            "System.Image.* (Dimensions, HorizontalSize, VerticalSize, BitDepth, …), " +
            "System.Music.* (Artist, AlbumTitle, TrackNumber, Genre, Year, Composer, …), " +
            "System.Video.* (FrameWidth, FrameHeight, FrameRate, TotalBitrate, Director, …), " +
            "System.Audio.* (ChannelCount, SampleRate, EncodingBitrate, Format, …), " +
            "System.Document.* (DateSaved, PageCount, WordCount, RevisionNumber, …), " +
            "System.GPS.* (Latitude, Longitude, Altitude, …), " +
            "System.Message.* (FromName, ToName, Subject, DateReceived, HasAttachments, …), " +
            "System.Contact.*, System.Calendar.*, System.RecordedTV.*, System.Software.*, " +
            "System.Search.*, System.Link.*, System.AppUserModel.*, System.Devices.*. " +
            "Use the list_properties_by_family or list_properties tools to discover the exact " +
            "names available on this machine.")]
            string[]? columns = null,
        [Description(
            "Structured filters AND-combined with 'text'. Each filter targets one System.* property " +
            "with an operator and value. Use for date ranges, size limits, EXIF metadata, etc. " +
            "Examples: filter by modified-after with property='System.DateModified', op='>=', " +
            "value='2024-01-01', valueType='text'; or by camera with property='System.Photo.CameraModel', " +
            "op='=', value='Canon EOS R5'.")]
            SearchFilter[]? filters = null,
        [Description("Optional System.* property to sort by (e.g. 'System.DateModified', 'System.Size').")]
            string? orderBy = null,
        [Description("Sort direction when 'orderBy' is set: 'ASC' (default) or 'DESC'.")]
            string? orderDir = null)
    {
        // Always fetch System.ItemUrl so we can recover the path for DP:// symlink entries
        // where System.ItemPathDisplay is empty.
        const string itemUrl = "System.ItemUrl";
        var fetchUrl = columns is null || !columns.Contains(itemUrl, StringComparer.OrdinalIgnoreCase);
        if (fetchUrl)
        {
            var extra = new string[( columns?.Length ?? 0) + 1];
            columns?.CopyTo(extra, 0);
            extra[extra.Length - 1] = itemUrl;
            columns = extra;
        }

        string sql;
        List<string> cols;
        try
        {
            (sql, _, cols) = BuildSql(text, scope, maxResults, mode, columns, filters, orderBy, orderDir);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message, ex);
        }

        var connType = Type.GetTypeFromProgID("ADODB.Connection")
            ?? throw new InvalidOperationException(
                "ADODB.Connection ProgID not found. Is the Windows Search service installed and running?");

        dynamic conn = Activator.CreateInstance(connType)!;
        try
        {
            conn.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            dynamic rs = conn.Execute(sql);
            try
            {
                var rows = new List<Dictionary<string, object?>>();
                while (!(bool)rs.EOF)
                {
                    var row = new Dictionary<string, object?>(cols.Count);
                    foreach (var col in cols)
                    {
                        try { row[col] = rs.Fields.Item[col].Value; }
                        catch { row[col] = null; }
                    }

                    // If ItemPathDisplay is empty, recover it from a DP:// ItemUrl.
                    if (row.TryGetValue("System.ItemPathDisplay", out var display) &&
                        display is null or "" &&
                        row.TryGetValue(itemUrl, out var urlVal) &&
                        urlVal is string urlStr &&
                        urlStr.StartsWith("DP://", StringComparison.OrdinalIgnoreCase))
                    {
                        row["System.ItemPathDisplay"] = ExtractPathFromDpUrl(urlStr);
                    }

                    // Remove the internally-added ItemUrl column from output.
                    if (fetchUrl) row.Remove(itemUrl);

                    rows.Add(row);
                    rs.MoveNext();
                }
                return JsonSerializer.Serialize(rows);
            }
            finally
            {
                try { rs.Close(); } catch { /* best-effort */ }
            }
        }
        finally
        {
            try { conn.Close(); } catch { /* best-effort */ }
        }
    }

    // DP://{SID}/X:/path/to/file#fragment  →  X:\path\to\file
    private static string? ExtractPathFromDpUrl(string url)
    {
        // Skip "DP://"
        var rest = url.AsSpan(5);
        // Skip "{...}/" SID segment if present
        if (rest.StartsWith("{".AsSpan(), StringComparison.Ordinal))
        {
            var close = rest.IndexOf('}');
            if (close >= 0) rest = rest[(close + 1)..].TrimStart('/');
        }
        // Strip trailing "#fragment"
        var hash = rest.LastIndexOf('#');
        if (hash >= 0) rest = rest[..hash];
        if (rest.IsEmpty) return null;
        return rest.ToString().Replace('/', '\\');
    }
}
