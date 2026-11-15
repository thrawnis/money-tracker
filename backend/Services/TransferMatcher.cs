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
/// Candidates: any transaction that isn't already a transfer, voided or split —
/// categorized or not (a card payment a payee default filed under a category
/// is still a transfer). Pairs the user dismissed are skipped. Pairs that share
/// a transaction are grouped, so when there's more than one possible match the
/// user sees them together and picks the right one (if any).
/// </summary>
public class TransferMatcher(AppDbContext db, IEncryptionService encryption)
{
    public const int DefaultDays = 3;
    public const int MaxDays = 5;

    public record Side(int Id, int AccountId, string AccountName, DateOnly Date, decimal Amount,
        string? Payee, string? Memo, string? Category);

    /// <summary>From = money out, To = money in.</summary>
    public record Match(Side From, Side To, int DayGap);

    /// <summary>
    /// Possible pairs linked by shared transactions (one transaction that could
    /// pair with several others), closest dates first. A group with one option
    /// is a plain suggestion; with several, at most one option per transaction
    /// can be the real transfer.
    /// </summary>
    public record Group(List<Match> Options);

    public async Task<List<Group>> FindAsync(ApplicationUser user, int days = DefaultDays)
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
                    if (dismissed.Contains((Math.Min(o.Id, i.Id), Math.Max(o.Id, i.Id)))) continue;
                    possible.Add((o, i, gap));
                }
        }

        // Group pairs that share a transaction (union-find over transaction ids).
        var parent = new Dictionary<int, int>();
        int Root(int x)
        {
            while (parent[x] != x) x = parent[x];
            return x;
        }
        foreach (var p in possible)
        {
            parent.TryAdd(p.Out.Id, p.Out.Id); parent.TryAdd(p.In.Id, p.In.Id);
            int a = Root(p.Out.Id), b = Root(p.In.Id);
            if (a != b) parent[a] = b;
        }

        return possible
            .GroupBy(p => Root(p.Out.Id))
            .Select(g => new Group(g
                .OrderBy(p => p.Gap).ThenByDescending(p => p.Out.Date).ThenBy(p => p.Out.Id).ThenBy(p => p.In.Id)
                .Select(p => new Match(Describe(p.Out, user, catLabel), Describe(p.In, user, catLabel), p.Gap))
                .ToList()))
            // Newest first, by the latest date in the group.
            .OrderByDescending(g => g.Options.Max(m => m.From.Date > m.To.Date ? m.From.Date : m.To.Date))
            .ToList();
    }

    /// <summary>How many suggestions (groups) Utilities → Possible Transfers would show.</summary>
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
