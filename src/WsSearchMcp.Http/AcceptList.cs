using System.Text.RegularExpressions;

namespace WsSearchMcp.Http;

/// <summary>
/// The MIME types a client says it can read natively (the <c>accept</c> argument of get_file).
/// Text is implicitly always acceptable: every MCP client handles a text block, so <c>text/*</c> is
/// part of every list. Wildcards (<c>image/*</c>, <c>*/*</c>) are supported; parameters
/// (<c>;q=0.5</c>) are ignored.
/// </summary>
public sealed class AcceptList
{
    public const string Default = "text/*";
    private const int MaxTokens = 32;
    private const int MaxLength = 1000;

    private static readonly Regex Token = new(
        @"^(\*|[a-z0-9][a-z0-9!#$&^_.+-]*)/(\*|[a-z0-9][a-z0-9!#$&^_.+-]*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<(string Type, string Subtype)> _entries = new();

    /// <summary>The effective list, normalised (lower case, de-duplicated, text first).</summary>
    public string Normalized { get; }

    private AcceptList(List<string> tokens)
    {
        Normalized = string.Join(",", tokens);
        foreach (var t in tokens)
        {
            var slash = t.IndexOf('/');
            _entries.Add((t[..slash], t[(slash + 1)..]));
        }
    }

    /// <exception cref="FormatException">The text is not a valid comma-separated MIME list.</exception>
    public static AcceptList Parse(string? text)
    {
        var tokens = new List<string> { Default };
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (text.Length > MaxLength) throw new FormatException("accept is too long.");
            foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var semi = raw.IndexOf(';');
                var t = (semi >= 0 ? raw[..semi] : raw).Trim().ToLowerInvariant();
                if (t.Length == 0) continue;
                if (!Token.IsMatch(t) || t.StartsWith("*/", StringComparison.Ordinal) && t != "*/*")
                    throw new FormatException(
                        $"'{raw}' is not a valid MIME type for accept (use e.g. \"text/*,image/*,application/pdf\").");
                if (!tokens.Contains(t)) tokens.Add(t);
                if (tokens.Count > MaxTokens) throw new FormatException("accept lists too many types.");
            }
        }
        return new AcceptList(tokens);
    }

    /// <summary>True if <paramref name="mime"/> (a concrete type such as <c>application/pdf</c>) is covered by the list.</summary>
    public bool Accepts(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return false;
        var slash = mime.IndexOf('/');
        if (slash <= 0) return false;
        var type = mime[..slash].Trim().ToLowerInvariant();
        var sub = mime[(slash + 1)..].Trim().ToLowerInvariant();
        var semi = sub.IndexOf(';');
        if (semi >= 0) sub = sub[..semi].Trim();
        foreach (var (t, s) in _entries)
        {
            if (t == "*" && s == "*") return true;
            if (t != type) continue;
            if (s == "*" || s == sub) return true;
        }
        return false;
    }
}
