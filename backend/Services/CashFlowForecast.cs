using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// Projects account balances forward from today using what's already known
/// about the future: transactions already dated in the future (by effective
/// date, the same rule as current balances) and the occurrences of active
/// scheduled bills/transfers that haven't been created as transactions yet.
/// Bank-reported (balance-only / linked investment) accounts are left out,
/// since their balance isn't driven by transactions.
/// Used by the forecast report, the Dashboard tile and the email digest.
/// </summary>
public class CashFlowForecast(AppDbContext db, IEncryptionService encryption)
{
    /// <summary>Account types the Dashboard tile and digest watch for low balances.</summary>
    public static readonly AccountType[] WatchedTypes = [AccountType.Checking, AccountType.Savings, AccountType.Cash];

    /// <summary>Account types that can be forecast at all (excludes investments and loans).</summary>
    public static readonly AccountType[] ForecastTypes =
        [AccountType.Checking, AccountType.Savings, AccountType.Cash, AccountType.CreditCard, AccountType.Other];

    public record Event(DateOnly Date, string Description, decimal Amount, decimal Balance, bool Scheduled);

    public record AccountForecast(
        int AccountId, string Name, string Type, decimal CurrentBalance, decimal? Threshold,
        decimal LowestBalance, DateOnly LowestDate, DateOnly? FirstBelowThreshold,
        decimal EndBalance, List<Event> Events);

    public async Task<List<AccountForecast>> ForUserAsync(ApplicationUser user, int days, IEnumerable<AccountType>? types = null)
    {
        var dek = user.EncryptedDataKey;
        var today = UserClock.Today(user);
        var end = today.AddDays(Math.Clamp(days, 1, 366));
        var typeList = (types ?? ForecastTypes).ToList();

        var reported = await ReportedBalances.ForUserAsync(db, user.Id);
        var accounts = (await db.Accounts
                .Where(a => a.UserId == user.Id && a.IsActive && typeList.Contains(a.Type))
                .ToListAsync())
            .Where(a => !reported.ContainsKey(a.Id))
            .ToList();
        if (accounts.Count == 0) return [];
        var ids = accounts.Select(a => a.Id).ToList();

        var sums = await db.Transactions
            .Where(t => ids.Contains(t.AccountId) && !t.IsVoided && (t.PostDate ?? t.Date) <= today)
            .GroupBy(t => t.AccountId)
            .Select(g => new { g.Key, Sum = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum);

        var future = await db.Transactions
            .Where(t => ids.Contains(t.AccountId) && !t.IsVoided && (t.PostDate ?? t.Date) > today && (t.PostDate ?? t.Date) <= end)
            .Include(t => t.Payee)
            .Select(t => new { t.AccountId, Date = t.PostDate ?? t.Date, t.Amount, t.Payee, t.MemoEncrypted, t.TransferAccountId, t.ScheduledTransactionId })
            .ToListAsync();

        var schedules = await db.ScheduledTransactions
            .Where(s => s.UserId == user.Id && s.IsActive && s.NextDueDate <= end
                && (ids.Contains(s.AccountId) || (s.TransferAccountId != null && ids.Contains(s.TransferAccountId.Value))))
            .ToListAsync();
        var scheduleIds = schedules.Select(s => s.Id).ToList();
        // Occurrences already created as transactions (ahead of time, or just posted).
        var materialized = (await db.Transactions
                .Where(t => t.ScheduledTransactionId != null && scheduleIds.Contains(t.ScheduledTransactionId.Value) && t.Date >= today.AddDays(-31))
                .Select(t => new { Id = t.ScheduledTransactionId!.Value, t.Date })
                .ToListAsync())
            .Select(x => (x.Id, x.Date)).ToHashSet();

        var names = accounts.ToDictionary(a => a.Id, a => a.Name);
        var allNames = await db.Accounts.Where(a => a.UserId == user.Id).ToDictionaryAsync(a => a.Id, a => a.Name);
        var events = ids.ToDictionary(id => id, _ => new List<(DateOnly Date, string Desc, decimal Amount, bool Scheduled)>());

        foreach (var t in future)
        {
            var desc = t.Payee is not null ? encryption.Decrypt(t.Payee.NameEncrypted, dek) ?? ""
                : t.TransferAccountId is int other ? $"Transfer {(t.Amount < 0 ? "to" : "from")} {allNames.GetValueOrDefault(other, "account")}"
                : encryption.Decrypt(t.MemoEncrypted, dek) ?? "Transaction";
            events[t.AccountId].Add((t.Date, desc, t.Amount, false));
        }

        foreach (var s in schedules)
        {
            var date = s.NextDueDate;
            for (var guard = 0; date <= end && guard < 400; guard++)
            {
                if (!materialized.Contains((s.Id, date)))
                {
                    // Due today or overdue but not posted yet: it's about to land.
                    var when = date < today ? today : date;
                    if (s.TransferAccountId is int to)
                    {
                        var amt = Math.Abs(s.Amount);
                        if (events.ContainsKey(s.AccountId)) events[s.AccountId].Add((when, s.Name, -amt, true));
                        if (events.ContainsKey(to)) events[to].Add((when, s.Name, amt, true));
                    }
                    else if (events.ContainsKey(s.AccountId))
                        events[s.AccountId].Add((when, s.Name, s.Amount, true));
                }
                var next = ScheduledTransactionPostingService.Advance(date, s);
                if (next <= date) break;
                date = next;
            }
        }

        return accounts.Select(a =>
        {
            var balance = a.OpeningBalance + sums.GetValueOrDefault(a.Id);
            var current = balance;
            // Default warning level: $0 for checking, savings and cash; none for
            // credit cards and other accounts, which normally run negative.
            decimal? threshold = a.LowBalanceThreshold ?? (WatchedTypes.Contains(a.Type) ? 0m : null);
            decimal lowest = balance; var lowestDate = today; DateOnly? firstBelow = balance < threshold ? today : null;
            var list = new List<Event>();
            // Money in before money out on the same day, so a payday and a bill
            // due the same day don't show a dip that wouldn't happen.
            foreach (var e in events[a.Id].OrderBy(e => e.Date).ThenByDescending(e => e.Amount))
            {
                balance += e.Amount;
                list.Add(new Event(e.Date, e.Desc, e.Amount, balance, e.Scheduled));
                if (balance < lowest) { lowest = balance; lowestDate = e.Date; }
                if (firstBelow is null && balance < threshold) firstBelow = e.Date;
            }
            return new AccountForecast(a.Id, names[a.Id], a.Type.ToString(), current, threshold,
                lowest, lowestDate, firstBelow, balance, list);
        })
        .OrderBy(f => f.Name)
        .ToList();
    }
}
