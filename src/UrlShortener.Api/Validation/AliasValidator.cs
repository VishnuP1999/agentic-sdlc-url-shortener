using System.Text.RegularExpressions;

namespace UrlShortener.Api.Validation;

/// <summary>Validates user supplied custom aliases.</summary>
public static partial class AliasValidator
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "health", "admin", "swagger", "static", "favicon.ico", "robots.txt", "r"
    };

    [GeneratedRegex("^[A-Za-z0-9_-]{3,32}$")]
    private static partial Regex AliasPattern();

    public static bool IsReserved(string code) => Reserved.Contains(code);

    public static string? Validate(string alias)
    {
        if (!AliasPattern().IsMatch(alias))
            return "alias must be 3-32 characters of letters, digits, '-' or '_'.";
        if (IsReserved(alias)) return "alias is reserved.";
        return null;
    }
}
