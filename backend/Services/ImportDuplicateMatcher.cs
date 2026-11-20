using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// Finds the transaction already in an account that an incoming import row
/// (file or bank sync) duplicates, comparing against both what's in the
/// register now and what it looked like when it was imported (Imported* fields):
///
///  - <b>strong</b>: the same bank / file transaction id (ImportExternalId).
///    Always skipped.
///  - <b>likely</b>: the same amount (now or as imported), dated within
///    ±<see cref="DayWindow"/> days (now or as imported), and the payee text
///    matches the original raw payee or the current payee. Skipped unless the
///    user ticks it.
///  - <b>plain</b>: the same date and amount, payee not matching (the old rule).
///    Skipped unless ticked; the review screen ticks these by default.
///
/// Each existing transaction is the duplicate of at most one row (the caller
/// tracks that through <c>available</c>). Used identically by the import
/// preview and the import itself, so what the review shows is what happens.
/// </summary>
public class ImportDuplicateMatcher(AppDbContext db, IEncryptionService encryption)
{
    public const int DayWindow = 3;
    public const string Strong = "strong", Likely = "likely", Plain = "plain";

    public record Match(Transaction Tx, string Strength, string Reason);

    public async Task<Match?> FindAsync(int accountId, DateOnly date, decimal amount, string? rawPayee, string? externalId,
        string dek, Func<Transaction, bool> available)
    {
        if (!string.IsNullOrWhiteSpace(externalId))
        {
            var id = externalId.Trim();
            var byId = (await db.Transactions.Include(t => t.Payee).Include(t => t.Splits)
                    .Where(t => t.AccountId == accountId && t.ImportExternalId == id).OrderBy(t => t.Id).ToListAsync())
                .FirstOrDefault(available);
            if (byId is not null) return new Match(byId, Strong, "Same bank transaction ID — already imported");
        }

        DateOnly from = date.AddDays(-DayWindow), to = date.AddDays(DayWindow);
        var candidates = (await db.Transactions.Include(t => t.Payee).Include(t => t.Splits)
                .Where(t => t.AccountId == accountId
                    && (t.Amount == amount || t.ImportedAmount == amount)
                    && ((t.Date >= from && t.Date <= to) || (t.ImportedDate >= from && t.ImportedDate <= to)))
                .OrderBy(t => t.Id).ToListAsync())
            .Where(available).ToList();
        if (candidates.Count == 0) return null;

        Match? likely = null; var likelyGap = int.MaxValue;
        Match? plain = null;
        foreach (var t in candidates)
        {
            var original = encryption.Decrypt(t.ImportedPayeeEncrypted, dek);
            var current = t.Payee is null ? null : encryption.Decrypt(t.Payee.NameEncrypted, dek);
            var gap = Math.Min(Gap(t.Date, date), t.ImportedDate is DateOnly d ? Gap(d, date) : int.MaxValue);
            string? why = PayeeMatches(rawPayee, original) ? $"matches the original payee \"{original}\""
                : PayeeMatches(rawPayee, current) ? $"same payee ({current})" : null;
            if (why is not null && gap < likelyGap)
            {
                likelyGap = gap;
                var when = gap == 0 ? "same date" : $"{gap} day{(gap == 1 ? "" : "s")} apart";
                likely = new Match(t, Likely, $"Same amount, {when}, and {why}");
            }
            var sameDay = (t.Date == date && t.Amount == amount) || (t.ImportedDate == date && t.ImportedAmount == amount);
            if (why is null && sameDay && plain is null)
                plain = new Match(t, Plain, "Same date and amount" + (current is null ? "" : $", but a different payee ({current})"));
        }
        return likely ?? plain;
    }

    private static int Gap(DateOnly a, DateOnly b) => Math.Abs(a.DayNumber - b.DayNumber);

    private static readonly char[] Separators = [' ', '*', '#', '-', '.', '/', '\'', ','];

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    { "the", "pos", "debit", "credit", "purchase", "card", "ach", "payment", "online", "recurring", "checkcard", "visa", "sq", "tst", "pp", "www", "com" };

    /// <summary>
    /// Whether two payee texts look like the same merchant: equal once only
    /// letters and digits are kept, one containing the other (4+ characters),
    /// or the same first significant word ("AMZN MKTP US*2K4" vs "AMZN Mktp US*9P1").
    /// </summary>
    public static bool PayeeMatches(string? a, string? b)
    {
        string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        string na = Norm(a), nb = Norm(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        if (na == nb) return true;
        if (Math.Min(na.Length, nb.Length) >= 4 && (na.Contains(nb) || nb.Contains(na))) return true;
        static string? FirstWord(string s) => s.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetter).ToArray()))
            .FirstOrDefault(w => w.Length >= 3 && !Noise.Contains(w));
        var fa = FirstWord(a); var fb = FirstWord(b);
        return fa is not null && fa.Length >= 4 && string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase);
    }
}
