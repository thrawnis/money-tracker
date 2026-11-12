using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// Finds pairs of ordinary transactions that look like the two sides of a
/// transfer between the user's own accounts — money out of one account and the
/// same amount into another within a few days — and links them as a transfer.
/// Used by Utilities → Possible Transfers, the Dashboard and the email digests.
///
/// Candidates: not already a transfer, not voided, not split. By default a pair
/// needs at least one uncategorized side (that's what slips through); with
/// includeCategorized, categorized pairs count too (e.g. a card payment a
/// payee default already filed under a category). Each transaction is used in
/// at most one pair, closest dates first; pairs the user dismissed are skipped.
/// </summary>
public class TransferMatcher(AppDbContext db, IEncryptionService encryption)
{
    public const int DefaultDays = 3;
    public const int MaxDays = 5;

    public record Side(int Id, int AccountId, string AccountName, DateOnly Date, decimal Amount,
        string? Payee, string? Memo, string? Category);

    /// <summary>From = money out, To = money in. Alternatives: other candidates either side could have paired with.</summary>
    public record Match(Side From, Side To, int DayGap, int Alternatives);

    public async Task<List<Match>> FindAsync(ApplicationUser user, int days = DefaultDays, bool includeCategorized = false)
    {
        days = Math.Clamp(days, 0, MaxDays);
        var txs = await db.Transactions
            .Where(t => t.Account.UserId == user.Id && t.TransferTransactionId == null && !t.IsVoided
                && !t.Splits.Any() && t.Amount != 0)
            .Include(t => t.Account).Include(t => t.Payee)
            .AsNoTracking()
            .ToListAsync();
        var categories = await db.Categories.Where(c => c.UserId == user.Id).AsNoTracking().ToListAsync();
        var catName = categories.ToDictionary(c => c.Id, c => encryption.Decrypt(c.NameEncrypted, user.EncryptedDataKey) ?? "");
        var catLabel = categories.ToDictionary(c => c.Id,
            c => c.ParentId is int p && catName.TryGetValue(p, out var parent) ? $"{parent}: {catName[c.Id]}" : catName[c.Id]);
        var dismissed = (await db.DismissedTransferPairs.Where(d => d.UserId == user.Id)
                .Select(d => new { d.TransactionIdA, d.TransactionIdB }).ToListAsync())
            .Select(d => (d.TransactionIdA, d.TransactionIdB)).ToHashSet();

        // Every possible pair: opposite amounts, different accounts, dates close enough.
        var possible = new List<(Transaction Out, Transaction In, int Gap)>();
        foreach (var byAmount in txs.GroupBy(t => Math.Abs(t.Amount)))
        {
            var outs = byAmount.Where(t => t.Amount < 0).ToList();
            var ins = byAmount.Where(t => t.Amount > 0).ToList();
            foreach (var o in outs)
                foreach (var i in ins)
                {
                    if (o.AccountId == i.AccountId) continue;
                    var gap = Math.Abs(o.Date.DayNumber - i.Date.DayNumber);
                    if (gap > days) continue;
                    if (!includeCategorized && o.CategoryId != null && i.CategoryId != null) continue;
                    if (dismissed.Contains((Math.Min(o.Id, i.Id), Math.Max(o.Id, i.Id)))) continue;
                    possible.Add((o, i, gap));
                }
        }

        // How many possible partners each transaction has, to flag guesses.
        var partners = possible.SelectMany(p => new[] { p.Out.Id, p.In.Id }).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());

        // Closest dates first; each transaction used once.
        var used = new HashSet<int>();
        var matches = new List<Match>();
        foreach (var p in possible.OrderBy(p => p.Gap).ThenByDescending(p => p.Out.Date).ThenBy(p => p.Out.Id).ThenBy(p => p.In.Id))
        {
            if (used.Contains(p.Out.Id) || used.Contains(p.In.Id)) continue;
            used.Add(p.Out.Id); used.Add(p.In.Id);
            var alternatives = partners[p.Out.Id] - 1 + partners[p.In.Id] - 1;
            matches.Add(new Match(Describe(p.Out, user, catLabel), Describe(p.In, user, catLabel), p.Gap, alternatives));
        }
        return matches.OrderByDescending(m => m.From.Date > m.To.Date ? m.From.Date : m.To.Date).ToList();
    }

    public async Task<int> CountAsync(ApplicationUser user) => (await FindAsync(user)).Count;

    private Side Describe(Transaction t, ApplicationUser user, Dictionary<int, string> catLabel)
    {
        var dek = user.EncryptedDataKey;
        return new Side(t.Id, t.AccountId, t.Account.Name, t.Date, t.Amount,
            t.Payee is null ? null : encryption.Decrypt(t.Payee.NameEncrypted, dek),
            encryption.Decrypt(t.MemoEncrypted, dek),
            t.CategoryId is int c ? catLabel.GetValueOrDefault(c) : null);
    }

    /// <summary>
    /// Links two of the user's transactions as the two sides of a transfer, as
    /// if it had been entered as one: each points at the other and its account,
    /// and the category is cleared (transfers don't have one). Dates, payee,
    /// memo and status are kept. Returns an error message, or null when linked.
    /// Re-checks everything, so a stale suggestion can't link the wrong things.
    /// </summary>
    public async Task<string?> LinkAsync(ApplicationUser user, int outId, int inId)
    {
        var pair = await db.Transactions
            .Where(t => (t.Id == outId || t.Id == inId) && t.Account.UserId == user.Id)
            .Include(t => t.Splits)
            .ToListAsync();
        var o = pair.FirstOrDefault(t => t.Id == outId);
        var i = pair.FirstOrDefault(t => t.Id == inId);
        if (o is null || i is null || outId == inId) return "Transaction not found.";
        if (o.TransferTransactionId != null || i.TransferTransactionId != null) return "One of these is already part of a transfer.";
        if (o.IsVoided || i.IsVoided) return "Voided transactions can't be linked.";
        if (o.Splits.Count > 0 || i.Splits.Count > 0) return "Split transactions can't be transfers.";
        if (o.AccountId == i.AccountId) return "Both transactions are in the same account.";
        if (o.Amount >= 0 || i.Amount != -o.Amount) return "The amounts don't match (one must be money out, the other the same amount in).";

        o.TransferTransactionId = i.Id; o.TransferAccountId = i.AccountId;
        i.TransferTransactionId = o.Id; i.TransferAccountId = o.AccountId;
        o.CategoryId = null; i.CategoryId = null;
        o.UpdatedAt = i.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return null;
    }
}
