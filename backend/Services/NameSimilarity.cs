using System.Text;
using System.Text.RegularExpressions;

namespace MoneyTracker.Services;

/// <summary>
/// "Probably the same name" for payees and categories: what an exact,
/// case-insensitive comparison misses — store numbers, punctuation, legal
/// suffixes, plurals, run-together words and one-letter typos. Used to
/// suggest duplicates for the user to merge, never to merge on its own.
/// </summary>
public static class NameSimilarity
{
    private static readonly HashSet<string> Noise =
        ["inc", "llc", "ltd", "co", "corp", "corporation", "company", "com", "www", "the", "store", "stores"];

    /// <summary>Lowercased words with numbers, punctuation and noise words removed, plurals made singular.</summary>
    public static string Normalize(string name)
    {
        var s = name.ToLowerInvariant().Replace("&", " and ").Replace("'", "").Replace("’", "");
        s = Regex.Replace(s, @"[^\p{L}\p{N}]+", " ");
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Any(char.IsDigit) && !Noise.Contains(w))
            .Select(Singular);
        return string.Join(' ', words);
    }

    private static string Singular(string w) =>
        w.Length > 4 && w.EndsWith("ies") ? w[..^3] + "y"
        : w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("us") ? w[..^1]
        : w;

    /// <param name="allowPrefix">
    /// Also match when one name is the other plus extra words ("Starbucks" /
    /// "Starbucks Coffee"). Right for payees; too loose for categories
    /// ("Auto" / "Auto Insurance").
    /// </param>
    public static bool Similar(string a, string b, bool allowPrefix) =>
        SimilarNormalized(Normalize(a), Normalize(b), allowPrefix);

    private static bool SimilarNormalized(string na, string nb, bool allowPrefix)
    {
        if (na.Length == 0 || nb.Length == 0) return false;
        if (na == nb) return true;
        var ca = na.Replace(" ", "");
        var cb = nb.Replace(" ", "");
        if (ca == cb) return true;   // "Wal Mart" / "Walmart"

        if (allowPrefix)
        {
            var (shortN, longN) = na.Length <= nb.Length ? (na, nb) : (nb, na);
            if (shortN.Length >= 4 && longN.StartsWith(shortN + " ")) return true;
        }

        var min = Math.Min(ca.Length, cb.Length);
        var allowed = min >= 10 ? 2 : min >= 5 ? 1 : 0;
        return allowed > 0 && Math.Abs(ca.Length - cb.Length) <= allowed && Distance(ca, cb) <= allowed;
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Groups items whose names are similar (transitively); only groups of 2 or more.</summary>
    public static List<List<T>> Group<T>(IReadOnlyList<T> items, Func<T, string> name, bool allowPrefix)
    {
        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        var names = items.Select(i => Normalize(name(i))).ToList();
        for (var i = 0; i < items.Count; i++)
            for (var j = i + 1; j < items.Count; j++)
                if (Find(i) != Find(j) && SimilarNormalized(names[i], names[j], allowPrefix))
                    parent[Find(i)] = Find(j);
        return Enumerable.Range(0, items.Count)
            .GroupBy(Find)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(i => items[i]).ToList())
            .ToList();
    }
}
