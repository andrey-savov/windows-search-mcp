using System.Text.RegularExpressions;

namespace WsSearchMcp.Security;

/// <summary>Validation rules for the human-friendly client names.</summary>
public static class ClientNames
{
    public const int MaxLength = 64;

    // Letters, digits, space, '-', '_', '.' — none of which need escaping in an X.500 name.
    private static readonly Regex Allowed = new(@"^[\p{L}\p{N} _.\-]+$", RegexOptions.Compiled);

    /// <summary>Validates and trims <paramref name="name"/>.</summary>
    public static bool TryNormalize(string? name, out string normalized, out string? error)
    {
        normalized = (name ?? "").Trim();
        if (normalized.Length == 0)
        {
            error = "Client name must not be empty.";
            return false;
        }
        if (normalized.Length > MaxLength)
        {
            error = $"Client name must be at most {MaxLength} characters.";
            return false;
        }
        if (!Allowed.IsMatch(normalized))
        {
            error = "Client name may contain only letters, digits, spaces, '-', '_' and '.'.";
            return false;
        }
        error = null;
        return true;
    }

    public static bool Equal(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
