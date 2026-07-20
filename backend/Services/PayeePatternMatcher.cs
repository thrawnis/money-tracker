using System.Text.RegularExpressions;

namespace MoneyTracker.Services;

/// <summary>
/// Matches a raw imported payee string against a PayeeMappingRule's pattern.
/// Non-regex patterns support '*' as a wildcard (e.g. "Amazon*", "*Amazon*");
/// without a '*' the whole string must match exactly. Regex patterns are used
/// as-is. Case-insensitive either way. Never throws — a bad/timing-out pattern
/// (e.g. user-supplied regex) just fails to match rather than breaking import.
/// </summary>
public static class PayeePatternMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public static bool IsMatch(string pattern, bool isRegex, string rawText)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(rawText)) return false;

        try
        {
            if (isRegex)
                return Regex.IsMatch(rawText, pattern, RegexOptions.IgnoreCase, MatchTimeout);

            if (!pattern.Contains('*'))
                return string.Equals(pattern, rawText, StringComparison.OrdinalIgnoreCase);

            var globAsRegex = "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$";
            return Regex.IsMatch(rawText, globAsRegex, RegexOptions.IgnoreCase, MatchTimeout);
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>First rule (in list order) whose pattern matches, or null.</summary>
    public static T? FindMatch<T>(IEnumerable<T> rules, string rawText, Func<T, string> pattern, Func<T, bool> isRegex)
    {
        foreach (var rule in rules)
            if (IsMatch(pattern(rule), isRegex(rule), rawText))
                return rule;
        return default;
    }
}
