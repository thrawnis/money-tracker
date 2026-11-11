using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// Calculations shared by the Reports API and the weekly email digest, so both
/// show the same numbers: spending trends and the inputs for balance history.
/// </summary>
public class ReportData(AppDbContext db, IEncryptionService encryption)
{
    public record TrendRow(int? CategoryId, string Name, decimal ThisMonth, decimal Average,
        decimal ExpectedSoFar, decimal AheadOfPace, decimal[] Monthly);

    public record Trends(List<string> Months, DateOnly AsOf, double MonthFraction,
        decimal TotalThisMonth, decimal TotalAverage, decimal TotalExpectedSoFar, List<TrendRow> Categories);

    /// <summary>See ReportsController.SpendingTrends.</summary>
    public async Task<Trends> SpendingTrendsAsync(ApplicationUser user, int months, int[]? accountIds = null)
    {
        var dek = user.EncryptedDataKey;
        months = Math.Clamp(months, 1, 24);

        var today = UserClock.Today(user);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var from = thisMonth.AddMonths(-months);
        var monthFraction = (double)today.Day / DateTime.DaysInMonth(today.Year, today.Month);

        var categories = await db.Categories.Where(c => c.UserId == user.Id).ToListAsync();
        var parentOf = categories.ToDictionary(c => c.Id, c => c.ParentId ?? c.Id);
        var names = categories.ToDictionary(c => c.Id, c => encryption.Decrypt(c.NameEncrypted, dek) ?? "");

        var query = db.Transactions
            .Where(t => t.Account.UserId == user.Id && !t.IsVoided && t.TransferTransactionId == null
                && t.Date >= from && t.Date <= today);
        if (accountIds is { Length: > 0 })
            query = query.Where(t => accountIds.Contains(t.AccountId));
        var txs = await query.Select(t => new
        {
            t.Date, t.Amount, t.CategoryId,
            Splits = t.Splits.Select(s => new { s.CategoryId, s.Amount }).ToList(),
        }).ToListAsync();

        // (top-level category id or 0 for uncategorized, month index 0..months) → spent
        var monthKeys = Enumerable.Range(0, months + 1).Select(i => from.AddMonths(i)).ToList();
        var spent = new Dictionary<int, decimal[]>();
        var spentByDay = new Dictionary<int, decimal>();   // previous months, up to today's day-of-month
        void Add(int? categoryId, DateOnly date, decimal amount)
        {
            var key = categoryId is int id && parentOf.TryGetValue(id, out var top) ? top : 0;
            var idx = (date.Year - from.Year) * 12 + date.Month - from.Month;
            if (!spent.TryGetValue(key, out var arr)) spent[key] = arr = new decimal[months + 1];
            arr[idx] -= amount;   // spending is negative; store it as a positive amount
            if (idx < months && date.Day <= today.Day)
                spentByDay[key] = spentByDay.GetValueOrDefault(key) - amount;
        }
        foreach (var t in txs)
        {
            if (t.Splits.Count > 0) foreach (var s in t.Splits) Add(s.CategoryId, t.Date, s.Amount);
            else Add(t.CategoryId, t.Date, t.Amount);
        }

        var rows = spent
            .Select(kv =>
            {
                var monthly = kv.Value.Select(v => Math.Round(v, 2)).ToArray();
                var average = Math.Round(monthly.Take(months).Sum() / months, 2);
                var current = monthly[months];
                var expected = Math.Round(spentByDay.GetValueOrDefault(kv.Key) / months, 2);
                // AheadOfPace > 0: spending faster than usual for this point in the month.
                return new TrendRow(kv.Key == 0 ? null : kv.Key, kv.Key == 0 ? "Uncategorized" : names.GetValueOrDefault(kv.Key, "?"),
                    current, average, expected, current - expected, monthly);
            })
            // Net income categories (salary, refunds) aren't spending.
            .Where(r => r.Average > 0 || r.ThisMonth > 0)
            .OrderByDescending(r => r.AheadOfPace)
            .ToList();

        return new Trends(
            monthKeys.Select(m => m.ToString("yyyy-MM")).ToList(), today, Math.Round(monthFraction, 4),
            rows.Sum(r => r.ThisMonth), rows.Sum(r => r.Average), rows.Sum(r => r.ExpectedSoFar), rows);
    }

    /// <summary>
    /// Balance history inputs for the user's accounts (or one account): daily
    /// transaction sums by effective date, and value history for bank-reported
    /// accounts. Empty when <paramref name="accountId"/> isn't the user's.
    /// </summary>
    public async Task<List<BalanceHistory.AccountInput>> BalanceInputsAsync(ApplicationUser user, int? accountId = null)
    {
        var accountsQuery = db.Accounts.Where(a => a.UserId == user.Id);
        if (accountId.HasValue) accountsQuery = accountsQuery.Where(a => a.Id == accountId.Value);
        var accounts = await accountsQuery.ToListAsync();
        var ids = accounts.Select(a => a.Id).ToList();

        var today = UserClock.Today(user);
        // Same as-of rule as current balances: effective date, voided excluded.
        var daily = (await db.Transactions
                .Where(t => ids.Contains(t.AccountId) && !t.IsVoided && (t.PostDate ?? t.Date) <= today)
                .GroupBy(t => new { t.AccountId, Day = t.PostDate ?? t.Date })
                .Select(g => new { g.Key.AccountId, g.Key.Day, Sum = g.Sum(t => t.Amount) })
                .ToListAsync())
            .ToLookup(x => x.AccountId, x => (x.Day, x.Sum));

        // Accounts whose balance is the bank's reported value use their value history.
        var reportedIds = (await ReportedBalances.ForUserAsync(db, user.Id)).Keys.ToHashSet();
        var values = (await db.AccountValueSnapshots
                .Where(v => v.UserId == user.Id && reportedIds.Contains(v.AccountId) && ids.Contains(v.AccountId) && v.Date <= today)
                .Select(v => new { v.AccountId, v.Date, v.Balance })
                .ToListAsync())
            .ToLookup(v => v.AccountId, v => (v.Date, v.Balance));

        return accounts.Select(a => new BalanceHistory.AccountInput(
            a.Id, a.Name, a.Type.ToString(), a.OpeningBalance,
            UserClock.ToLocalDate(new DateTimeOffset(DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc)), user.TimeZoneId),
            daily[a.Id].ToList(),
            reportedIds.Contains(a.Id) ? values[a.Id].ToList() : null)).ToList();

    }

    /// <summary>
    /// Net amount per top-level category (subcategories roll up; null key =
    /// uncategorized) between two dates inclusive, positive = money out.
    /// Split-aware; transfers and voided transactions excluded — the same rules
    /// as spending trends. Categories that net negative are income.
    /// </summary>
    public async Task<(Dictionary<int, decimal> ByCategory, Dictionary<int, string> Names)> SpendingByCategoryAsync(
        ApplicationUser user, DateOnly from, DateOnly to)
    {
        var categories = await db.Categories.Where(c => c.UserId == user.Id).ToListAsync();
        var parentOf = categories.ToDictionary(c => c.Id, c => c.ParentId ?? c.Id);
        var names = categories.ToDictionary(c => c.Id, c => encryption.Decrypt(c.NameEncrypted, user.EncryptedDataKey) ?? "");
        names[0] = "Uncategorized";

        var txs = await db.Transactions
            .Where(t => t.Account.UserId == user.Id && !t.IsVoided && t.TransferTransactionId == null && t.Date >= from && t.Date <= to)
            .Select(t => new { t.Amount, t.CategoryId, Splits = t.Splits.Select(s => new { s.CategoryId, s.Amount }).ToList() })
            .ToListAsync();
        var spent = new Dictionary<int, decimal>();
        void Add(int? categoryId, decimal amount)
        {
            var key = categoryId is int id && parentOf.TryGetValue(id, out var top) ? top : 0;
            spent[key] = spent.GetValueOrDefault(key) - amount;
        }
        foreach (var t in txs)
        {
            if (t.Splits.Count > 0) foreach (var s in t.Splits) Add(s.CategoryId, s.Amount);
            else Add(t.CategoryId, t.Amount);
        }
        return (spent, names);
    }
}
