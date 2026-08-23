using System.Text.RegularExpressions;

namespace MoneyTracker.Services;

/// <summary>
/// Builds the case-insensitive matcher used for every user-supplied text
/// filter over an encrypted field (payee name, memo, check number).
///
/// Semantics — deliberately shared by the account register and the
/// Transactions search page, which previously had two near-identical copies
/// of this that disagreed: plain text with no wildcard was a substring match
/// in one and an unanchored regex in the other, and only one anchored its
/// wildcard patterns. The same query returned different results depending on
/// which screen you typed it into.
///
///  * matches any sequence of characters, ? matches exactly one.
///  A pattern containing either is anchored (^...$), so "Amazon*" means
///  "starts with Amazon" rather than "contains Amazon anywhere".
///  A pattern with neither is a plain substring (contains) search.
///
/// Regex metacharacters in the user's text are escaped, so "/", "(", "." and
/// friends are matched literally. A match timeout keeps a pathological
/// pattern from pinning a request thread.
/// </summary>
public static class WildcardPattern
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public static Regex Build(string pattern)
    {
        bool hasWildcard = pattern.Contains('*') || pattern.Contains('?');
        string regexStr = hasWildcard
            ? "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$"
            : Regex.Escape(pattern); // substring match — IsMatch finds it anywhere

        return new Regex(regexStr, RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout);
    }

    /// <summary>
    /// Match that treats a timeout as "no match" rather than throwing — a bad
    /// pattern should return nothing, not 500 the whole search.
    /// </summary>
    public static bool Matches(Regex rx, string? value)
    {
        try { return rx.IsMatch(value ?? ""); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
