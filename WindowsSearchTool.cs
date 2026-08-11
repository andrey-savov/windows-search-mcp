using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
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

    // The tool description documents lowercase JSON keys ("property", "op", "value",
    // "valueType"), but SearchFilter's C# properties are PascalCase. System.Text.Json's
    // default deserialization is case-sensitive, so lowercase keys silently fail to bind
    // and every filter comes back with Property == "" — which then fails
    // ValidatePropertyName with a confusing "Invalid property name ''" error.
    private static readonly JsonSerializerOptions FilterJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static (string sql, string? scopeUri, List<string> cols) BuildSql(
        string? query,
        string? scope,
        int maxResults,
        string mode,
        string[]? columns,
        SearchFilter[]? filters = null,
        string? orderBy = null,
        string? orderDir = null,
        string filterLogic = "AND")
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

        // Ensure filterLogic is a supported logical operator (AND/OR)
        var logicUpper = filterLogic?.Trim().ToUpperInvariant();
        if (logicUpper != "AND" && logicUpper != "OR")
            throw new ArgumentException($"filterLogic must be 'AND' or 'OR', got '{filterLogic}'.", nameof(filterLogic));

        var where = string.Join($" {logicUpper} ", clauses);
        var sql = $"SELECT TOP {maxResults} {string.Join(", ", cols)} FROM SYSTEMINDEX WHERE {where}";

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
            "contains" => BuildContainsClause(query),
            "filename" => $"System.FileName LIKE '%{BuildLikePattern(query)}%'",
            "raw" => ValidateRawSqlClause(query),
            _ => throw new ArgumentException(
                $"Unknown mode '{mode}'. Expected one of: contains, filename, raw.", nameof(mode))
        };

    private static string ValidateRawSqlClause(string query)
    {
        if (Regex.IsMatch(query, @"\b(?:System\.)?[A-Za-z][A-Za-z0-9.]*:\s*[<>]"))
            throw new ArgumentException(
                "Raw mode expects a Windows Search SQL WHERE clause, not AQS syntax. " +
                "For example, use 'System.Size > 104857600', not 'System.Size:>100mb'.",
                nameof(query));
        return query;
    }

    // Builds the `contains` clause: a full-text CONTAINS phrase OR a filename substring
    // match. The term is embedded inside CONTAINS('"<term>"'), so a double quote in the
    // term would close the phrase early and yield a malformed full-text query that the
    // OLE DB provider rejects. SanitizeFullText neutralizes that; the cleaned term is used
    // for the filename match too, so a quoted query like  "annual report"  still matches a
    // file named  annual report.pdf.
    private static string BuildContainsClause(string query)
    {
        var phrase = SanitizeFullText(query);
        if (phrase.Length == 0)
            throw new ArgumentException("Query contains no searchable characters.", nameof(query));
        return $"(CONTAINS('\"{EscapeSqlLiteral(phrase)}\"') OR System.FileName LIKE '%{BuildLikePattern(query)}%')";
    }

    // Double quotes delimit phrases inside a CONTAINS predicate, so an unbalanced one
    // produces a malformed full-text query. They are word-breakers in full-text search,
    // so replace them with spaces and collapse runs of whitespace to a single space.
    private static string SanitizeFullText(string s) =>
        Regex.Replace(s.Replace('"', ' '), @"\s+", " ").Trim();

    // Builds the inner term of a  System.FileName LIKE '%...%'  pattern.
    // Apostrophe/quote variants are canonicalized to the LIKE single-character wildcard
    // '_' so a query typed with a straight apostrophe (U+0027) still matches a filename
    // stored with a typographic one (U+2019) and vice versa — LIKE compares the indexed
    // text literally, so canonicalizing only the query to a single quote would not match.
    // Double quotes (which cannot occur in Windows filenames) are dropped to spaces.
    private static string BuildLikePattern(string query)
    {
        var sb = new StringBuilder(query.Length);
        foreach (var ch in query)
        {
            switch (ch)
            {
                // Single-quote / apostrophe family -> match any single character.
                case '\'' or '‘' or '’' or 'ʼ' or '`' or '´':
                    sb.Append('_');
                    break;
                // Double-quote family -> word break.
                case '"' or '“' or '”':
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        var normalized = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return EscapeSqlLiteral(normalized);
    }

    private static string RenderFilter(SearchFilter f)
    {
        if (f is null) throw new ArgumentException("Filter cannot be null.", nameof(f));
        ValidatePropertyName(f.Property);
        var op = WebUtility.HtmlDecode((f.Op ?? "=").Trim());
        var opUpper = op.ToUpperInvariant();

        if (opUpper is "IS NULL" or "IS NOT NULL")
            return $"{f.Property} {opUpper}";

        if (opUpper == "CONTAINS")
        {
            if (f.Value is null)
                throw new ArgumentException(
                    $"Operator 'CONTAINS' requires a value (property: {f.Property}).");
            var term = SanitizeFullText(f.Value);
            if (term.Length == 0)
                throw new ArgumentException(
                    $"CONTAINS value for '{f.Property}' has no searchable characters.");
            return $"CONTAINS({f.Property}, '{EscapeSqlLiteral(term)}')";
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

    [McpServerTool(Name = "find_files")]
    [Description("Search the Windows Search index. Returns a JSON array of matching file paths.")]
    public static string Search(
        [Description(
            "Full-text search term (e.g. 'Erin Dawn'). Pass empty string to search by 'filters' only. " +
            "Quotes are handled for you: straight and typographic apostrophes/quotes are treated as " +
            "interchangeable, so \"Samantha's\" matches a file named with either ' or ’. Just type the term naturally.")]
            string text,
        [Description(
            "Where to search. THREE distinct cases:\n" +
            "  (1) Omit it, or pass the literal \"<UNSET>\"  -> search the ENTIRE Windows Search index: every indexed location, all drives. This is the default.\n" +
            "  (2) Pass an empty string \"\"                 -> search the ENTIRE Windows Search index: every indexed location, all drives.\n" +
            "  (3) Pass an absolute folder path (e.g. 'C:\\\\Users\\\\me\\\\Documents' or 'D:\\\\Photos') -> search ONLY that folder and its subfolders.")]
            string scope = "<UNSET>",
        [Description("Max results. Default 50.")]
            int maxResults = 50,
        [Description("'contains' (default), 'filename', or 'raw'.")]
            string mode = "contains",
        [Description("Extra System.* columns to return (e.g. 'System.DateModified,System.Size'). Comma-separated.")]
            string columns = "",
        [Description("Property filters as JSON, e.g. '[{\"property\":\"System.Kind\",\"op\":\"=\",\"value\":\"picture\"}]'. Pass empty string for no filters.")]
            string filters = "",
        [Description("System.* property to sort by. Pass empty string for no sort.")]
            string orderBy = "",
        [Description("'ASC' or 'DESC'. Default ASC.")]
            string orderDir = "ASC",
        [Description("Logical operator to combine query and filter clauses. 'AND' (default) or 'OR'.")]
            string filterLogic = "AND")
    {
        // Some MCP hosts (notably the MSIX build of Claude Desktop) launch the server with
        // a stripped environment that omits %CommonProgramFiles%. The Windows Search OLE DB
        // provider needs it to load the OLE DB core components during query execution, so
        // restore it before touching COM. See EnsureSearchComEnvironment.
        EnsureSearchComEnvironment();

        string? scopeArg = scope == "<UNSET>" ? null : scope;
        string[]? columnsArg = string.IsNullOrEmpty(columns) ? null : columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        SearchFilter[]? filtersArg;
        try
        {
            filtersArg = string.IsNullOrEmpty(filters)
                ? null
                : JsonSerializer.Deserialize<SearchFilter[]>(filters, FilterJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new McpException(
                "Invalid filters JSON. Pass a JSON string containing an array; each filter value must be a string, " +
                "for example: [{\"property\":\"System.Size\",\"op\":\">\",\"value\":\"104857600\",\"valueType\":\"number\"}].",
                ex);
        }
        string? orderByArg = string.IsNullOrEmpty(orderBy) ? null : orderBy;
        string? orderDirArg = string.IsNullOrEmpty(orderDir) ? null : orderDir;
        // Always fetch System.ItemUrl so we can recover the path for DP:// symlink entries
        // where System.ItemPathDisplay is empty.
        const string itemUrl = "System.ItemUrl";
        var fetchUrl = columnsArg is null || !columnsArg.Contains(itemUrl, StringComparer.OrdinalIgnoreCase);
        if (fetchUrl)
        {
            var extra = new string[(columnsArg?.Length ?? 0) + 1];
            columnsArg?.CopyTo(extra, 0);
            extra[extra.Length - 1] = itemUrl;
            columnsArg = extra;
        }

        string sql;
        List<string> cols;
        try
        {
            (sql, _, cols) = BuildSql(text, scopeArg, maxResults, mode, columnsArg, filtersArg, orderByArg, orderDirArg, filterLogic);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message, ex);
        }

        try
        {
            return ExecuteComQuery(sql, cols, fetchUrl);
        }
        catch (Exception ex) when (IsClassNotRegistered(ex))
        {
            // If this still fires, the OLE DB core components could not be activated even
            // after restoring %CommonProgramFiles% — surface an actionable message rather
            // than the opaque "Class not registered".
            Diag.Log($"find_files: REGDB_E_CLASSNOTREG after EnsureSearchComEnvironment. CommonProgramFiles={Environment.GetEnvironmentVariable("CommonProgramFiles")}");
            throw new McpException(
                "Windows Search query failed to activate the OLE DB provider (REGDB_E_CLASSNOTREG). " +
                "This usually means %CommonProgramFiles% is missing from the environment and could not be restored.",
                ex);
        }
            catch (Exception ex)
            {
                Diag.Log($"find_files: query execution failed: {ex.Message}");
                throw new McpException($"Windows Search query failed: {ex.Message}", ex);
            }
    }

    // The Windows Search OLE DB provider (Search.CollatorDSO) resolves the OLE DB core
    // components from %CommonProgramFiles%\System\Ole DB during conn.Execute. Hosts that
    // launch the server with a minimal environment (e.g. the MSIX Claude Desktop build)
    // omit that variable, which makes execution fail with REGDB_E_CLASSNOTREG. Repopulate
    // the *ProgramFiles common-files variables from the known-folder paths, which are read
    // from the shell folders registry and do not depend on the environment block.
    private static void EnsureSearchComEnvironment()
    {
        SetIfMissing("CommonProgramFiles", Environment.SpecialFolder.CommonProgramFiles);
        SetIfMissing("CommonProgramFiles(x86)", Environment.SpecialFolder.CommonProgramFilesX86);

        // On 64-bit Windows CommonProgramW6432 is the native Common Files path.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CommonProgramW6432")))
        {
            var native = Environment.GetEnvironmentVariable("CommonProgramFiles");
            if (!string.IsNullOrEmpty(native))
                Environment.SetEnvironmentVariable("CommonProgramW6432", native);
        }
    }

    private static void SetIfMissing(string name, Environment.SpecialFolder folder)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) return;
        var path = Environment.GetFolderPath(folder);
        if (!string.IsNullOrEmpty(path))
        {
            Environment.SetEnvironmentVariable(name, path);
            Diag.Log($"EnsureSearchComEnvironment: set {name}={path}");
        }
    }

    // Executes the SQL over the Windows Search OLE DB provider (Search.CollatorDSO)
    // via ADODB and serializes the rows to JSON. Throws COMException with HRESULT
    // REGDB_E_CLASSNOTREG when the provider cannot be activated in this context.
    private static string ExecuteComQuery(string sql, List<string> cols, bool fetchUrl)
    {
        const string itemUrl = "System.ItemUrl";

        var connType = Type.GetTypeFromProgID("ADODB.Connection")
            ?? throw new InvalidOperationException(
                "ADODB.Connection ProgID not found. Is the Windows Search service installed and running?");
        Diag.Log($"ExecuteComQuery: ADODB.Connection ProgID resolved to CLSID {connType.GUID}");

        dynamic conn = Activator.CreateInstance(connType)!;
        Diag.Log("ExecuteComQuery: ADODB.Connection instance created; opening Search.CollatorDSO provider");
        try
        {
            conn.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            Diag.Log("ExecuteComQuery: provider opened; executing query");
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

    // True if the exception (or any inner exception) is a COMException carrying
    // REGDB_E_CLASSNOTREG (0x80040154).
    private static bool IsClassNotRegistered(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Runtime.InteropServices.COMException &&
                unchecked((uint)e.HResult) == 0x80040154u)
            {
                return true;
            }
        }
        return false;
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
