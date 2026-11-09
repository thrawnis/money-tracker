using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/dashboard")]
public class DashboardController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        // ── Account balances ──────────────────────────────────────────────────

        var accounts = await db.Accounts
            .Where(a => a.UserId == userId && a.IsActive)
            .Include(a => a.Institution)
            .ToListAsync();

        // "As of today" by effective date (PostDate ?? Date) — exclude future-dated
        // transactions, matching AccountsController and the register header
        var balanceCutoff = UserClock.Today(user);
        var transactionSums = await db.Transactions
            .Where(t => t.Account.UserId == userId && (t.PostDate ?? t.Date) <= balanceCutoff && !t.IsVoided)
            .GroupBy(t => t.AccountId)
            .Select(g => new { AccountId = g.Key, Sum = g.Sum(t => t.Amount) })
            .ToListAsync();

        var sumByAccount = transactionSums.ToDictionary(x => x.AccountId, x => x.Sum);

        var reported = await ReportedBalances.ForUserAsync(db, userId);

        var accountSummaries = accounts.Select(a => new
        {
            id              = a.Id,
            name            = a.Name,
            type            = a.Type.ToString(),
            institution     = a.Institution?.Name,
            openingBalance  = a.OpeningBalance,
            currentBalance  = reported.GetValueOrDefault(a.Id)?.Balance
                              ?? a.OpeningBalance + sumByAccount.GetValueOrDefault(a.Id, 0m),
            balanceAsOf     = reported.GetValueOrDefault(a.Id)?.Date,
        })
        .OrderBy(a => a.type)
        .ThenBy(a => a.name)
        .ToList();

        // ── Upcoming bills (next 14 days) ─────────────────────────────────────

        var today  = UserClock.Today(user);
        var cutoff = today.AddDays(14);

        var upcomingBills = await db.ScheduledTransactions
            .Where(s => s.UserId == userId && s.IsActive && s.NextDueDate <= cutoff)
            .Include(s => s.Payee)
            .OrderBy(s => s.NextDueDate)
            .ToListAsync();

        var billSummaries = upcomingBills.Select(s => new
        {
            id          = s.Id,
            name        = s.Name,
            payee       = s.Payee is null ? null : encryption.Decrypt(s.Payee.NameEncrypted, dek),
            amount      = s.Amount,
            nextDueDate = s.NextDueDate,
            daysUntilDue = s.NextDueDate.DayNumber - today.DayNumber,
            isOverdue   = s.NextDueDate < today,
        }).ToList();

        // ── Uncategorized transactions ─────────────────────────────────────────

        var uncategorizedCount = await db.Transactions
            .CountAsync(t => t.Account.UserId == userId && t.CategoryId == null && t.TransferTransactionId == null && !t.IsVoided && !t.Splits.Any());

        var recentUncategorized = await db.Transactions
            .Where(t => t.Account.UserId == userId && t.CategoryId == null && t.TransferTransactionId == null && !t.IsVoided && !t.Splits.Any())
            .Include(t => t.Payee)
            .Include(t => t.Account)
            .OrderByDescending(t => t.Date)
            .Take(5)
            .ToListAsync();

        // For "also use this category for future <payee> transactions": how many
        // other uncategorized transactions each sample's payee has.
        var samplePayeeIds = recentUncategorized.Where(t => t.PayeeId != null).Select(t => t.PayeeId!.Value).Distinct().ToList();
        var uncategorizedByPayee = await db.Transactions
            .Where(t => t.Account.UserId == userId && t.PayeeId != null && samplePayeeIds.Contains(t.PayeeId.Value)
                && t.CategoryId == null && t.TransferTransactionId == null && !t.IsVoided && !t.Splits.Any())
            .GroupBy(t => t.PayeeId!.Value)
            .Select(g => new { PayeeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PayeeId, x => x.Count);

        var uncategorizedSamples = recentUncategorized.Select(t => new
        {
            payeeId = t.PayeeId,
            payeeHasDefaultCategory = t.Payee?.DefaultCategoryId != null,
            payeeUncategorizedCount = t.PayeeId is int pid ? uncategorizedByPayee.GetValueOrDefault(pid) : 0,
            id      = t.Id,
            accountId = t.AccountId,
            accountName = t.Account.Name,
            date    = t.Date,
            payee   = t.Payee is null ? null : encryption.Decrypt(t.Payee.NameEncrypted, dek),
            amount  = t.Amount,
        }).ToList();

        return Ok(new
        {
            accounts             = accountSummaries,
            upcomingBills        = billSummaries,
            uncategorizedCount,
            uncategorizedSamples,
        });
    }
}
