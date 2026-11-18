using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

/// <summary>
/// A Loan account's terms (rate, term, payment, escrow, automatic charges) and
/// what the loan panel needs: the balance owed now — the lender's reported
/// balance for "balance only" synced loans, else the register's — and the
/// interest charged so far this year. Projections are worked out in the browser.
/// </summary>
[ApiController]
[Authorize]
[Route("api/accounts/{accountId:int}/loan")]
public class LoanController(AppDbContext db, UserManager<ApplicationUser> userManager, LoanService loans, IAuditService audit) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    public record LoanDto(
        decimal AnnualRate, decimal? OriginalAmount, int? TermMonths, DateOnly? FirstPaymentDate, decimal? MonthlyPayment,
        decimal EscrowAmount, bool AutoCharges, DateOnly? AutoChargesFrom, int? InterestCategoryId, int? EscrowCategoryId);

    private Task<Account?> LoanAccountAsync(int accountId, string userId) =>
        db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == userId && a.Type == AccountType.Loan);

    [HttpGet]
    public async Task<IActionResult> Get(int accountId)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var account = await LoanAccountAsync(accountId, userId);
        if (account is null) return NotFound();
        var user = await userManager.FindByIdAsync(userId);
        var today = user is null ? DateOnly.FromDateTime(DateTime.UtcNow) : UserClock.Today(user);

        var d = await db.LoanDetails.AsNoTracking().FirstOrDefaultAsync(l => l.AccountId == accountId && l.UserId == userId);
        var reported = (await ReportedBalances.ForUserAsync(db, userId)).GetValueOrDefault(accountId);
        var registerBalance = account.OpeningBalance + await db.Transactions
            .Where(t => t.AccountId == accountId && !t.IsVoided && (t.PostDate ?? t.Date) <= today).SumAsync(t => (decimal?)t.Amount) ?? 0;
        var balance = reported?.Balance ?? registerBalance;
        var yearStart = new DateOnly(today.Year, 1, 1);
        var interestThisYear = -(await db.Transactions
            .Where(t => t.AccountId == accountId && !t.IsVoided && t.LoanChargeKind == LoanService.Interest && t.Date >= yearStart && t.Date <= today)
            .SumAsync(t => (decimal?)t.Amount) ?? 0);

        DateOnly? next = null;
        if (d?.FirstPaymentDate is DateOnly first)
        {
            next = first;
            for (var i = 0; next < today && i < 1200; i++) next = first.AddMonths(i + 1);
        }
        return Ok(new
        {
            details = d is null ? null : new
            {
                d.AnnualRate, d.OriginalAmount, d.TermMonths, d.FirstPaymentDate, d.MonthlyPayment, d.EscrowAmount,
                d.AutoCharges, d.AutoChargesFrom, d.InterestCategoryId, d.EscrowCategoryId,
            },
            monthlyPayment = d is null ? null : LoanMath.PaymentFor(d),
            owed = Math.Max(0, -balance),
            balanceSource = reported is null ? "register" : "bank",
            balanceDate = reported?.Date ?? today,
            nextPaymentDate = next,
            interestThisYear,
            today,
        });
    }

    [HttpPut]
    public async Task<IActionResult> Save(int accountId, LoanDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        if (await LoanAccountAsync(accountId, userId) is null) return NotFound();
        var user = await userManager.FindByIdAsync(userId);

        if (dto.AnnualRate is < 0 or > 40) return BadRequest(new { message = "Enter an interest rate between 0% and 40%." });
        if (dto.TermMonths is < 1 or > 600) return BadRequest(new { message = "The term must be between 1 and 600 months." });
        if (dto.OriginalAmount is < 0 or > 100_000_000 || dto.MonthlyPayment is < 0 or > 10_000_000 || dto.EscrowAmount is < 0 or > 1_000_000)
            return BadRequest(new { message = "One of the amounts isn't valid." });
        foreach (var c in new[] { dto.InterestCategoryId, dto.EscrowCategoryId })
            if (c is int cid && !await db.Categories.AnyAsync(x => x.Id == cid && x.UserId == userId))
                return BadRequest(new { message = "Category not found." });
        if (dto.AutoCharges && dto.InterestCategoryId is null)
            return BadRequest(new { message = "Choose a category for the interest charges (e.g. Mortgage Interest)." });
        if (dto.AutoCharges && dto.EscrowAmount > 0 && dto.EscrowCategoryId is null)
            return BadRequest(new { message = "Choose a category for the escrow charges (e.g. Property Tax & Insurance)." });

        var d = await db.LoanDetails.FirstOrDefaultAsync(l => l.AccountId == accountId && l.UserId == userId);
        if (d is null) { d = new LoanDetails { UserId = userId, AccountId = accountId }; db.LoanDetails.Add(d); }
        d.AnnualRate = Math.Round(dto.AnnualRate, 4);
        d.OriginalAmount = dto.OriginalAmount is > 0 ? dto.OriginalAmount : null;
        d.TermMonths = dto.TermMonths;
        d.FirstPaymentDate = dto.FirstPaymentDate;
        d.MonthlyPayment = dto.MonthlyPayment is > 0 ? dto.MonthlyPayment : null;
        d.EscrowAmount = dto.EscrowAmount;
        d.AutoCharges = dto.AutoCharges;
        d.AutoChargesFrom = dto.AutoChargesFrom ?? (user is null ? DateOnly.FromDateTime(DateTime.UtcNow) : UserClock.Today(user));
        d.InterestCategoryId = dto.InterestCategoryId;
        d.EscrowCategoryId = dto.EscrowCategoryId;
        d.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.LogAsync("LOAN_DETAILS_SAVED", "Account", accountId);
        await loans.SyncAsync(userId, accountId);
        return await Get(accountId);
    }

    /// <summary>Removes the loan's details. Charges already added stay in the register as ordinary transactions.</summary>
    [HttpDelete]
    public async Task<IActionResult> Remove(int accountId)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var n = await db.LoanDetails.Where(l => l.AccountId == accountId && l.UserId == userId).ExecuteDeleteAsync();
        if (n == 0) return NotFound();
        await db.Transactions.Where(t => t.AccountId == accountId && t.LoanChargeForTransactionId != null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LoanChargeForTransactionId, (int?)null).SetProperty(t => t.LoanChargeKind, (string?)null));
        await audit.LogAsync("LOAN_DETAILS_REMOVED", "Account", accountId);
        return NoContent();
    }

    /// <summary>Recomputes the automatic charges now (also happens after any change).</summary>
    [HttpPost("recalculate")]
    public async Task<IActionResult> Recalculate(int accountId)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        if (await LoanAccountAsync(accountId, userId) is null) return NotFound();
        return Ok(new { changed = await loans.SyncAsync(userId, accountId) });
    }
}
