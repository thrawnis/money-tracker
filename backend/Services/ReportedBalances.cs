using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;

namespace MoneyTracker.Services;

/// <summary>
/// Balances that come from the bank rather than from adding up transactions:
/// accounts linked to a balance-only SimpleFIN account use their latest
/// recorded value. Used wherever a "current balance" is shown (Accounts,
/// Dashboard, register header) so they all agree.
/// </summary>
public static class ReportedBalances
{
    public record Value(decimal Balance, DateOnly Date);

    public static async Task<Dictionary<int, Value>> ForUserAsync(AppDbContext db, string userId)
    {
        var accountIds = await db.SimpleFinAccounts
            .Where(a => a.UserId == userId && a.BalanceOnly && a.LinkedAccountId != null)
            .Select(a => a.LinkedAccountId!.Value)
            .ToListAsync();
        if (accountIds.Count == 0) return [];

        return (await db.AccountValueSnapshots
                .Where(v => v.UserId == userId && accountIds.Contains(v.AccountId))
                .GroupBy(v => v.AccountId)
                .Select(g => g.OrderByDescending(v => v.Date).First())
                .ToListAsync())
            .ToDictionary(v => v.AccountId, v => new Value(v.Balance, v.Date));
    }
}
