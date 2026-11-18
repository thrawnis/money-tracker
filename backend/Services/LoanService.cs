using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>Standard fixed-rate loan maths (monthly compounding).</summary>
public static class LoanMath
{
    /// <summary>Monthly principal + interest for <paramref name="principal"/> over <paramref name="months"/> at <paramref name="annualRate"/>%.</summary>
    public static decimal Payment(decimal principal, decimal annualRate, int months)
    {
        if (principal <= 0 || months <= 0) return 0;
        if (annualRate <= 0) return Math.Round(principal / months, 2);
        var i = (double)annualRate / 1200d;
        var p = (double)principal * i / (1 - Math.Pow(1 + i, -months));
        return Math.Round((decimal)p, 2);
    }

    /// <summary>One month's interest on <paramref name="owed"/>.</summary>
    public static decimal MonthlyInterest(decimal owed, decimal annualRate) =>
        owed <= 0 || annualRate <= 0 ? 0 : Math.Round(owed * annualRate / 1200m, 2);

    public record Projection(bool Covers, int Months, decimal TotalInterest);

    /// <summary>Months to pay off <paramref name="owed"/> paying <paramref name="payment"/> (P&amp;I) a month, and the interest that costs.</summary>
    public static Projection Project(decimal owed, decimal annualRate, decimal payment, int maxMonths = 1200)
    {
        if (owed <= 0) return new(true, 0, 0);
        if (payment <= MonthlyInterest(owed, annualRate)) return new(false, 0, 0);
        decimal interest = 0; var months = 0;
        while (owed > 0 && months < maxMonths)
        {
            var i = MonthlyInterest(owed, annualRate);
            interest += i;
            owed -= Math.Min(owed + i, payment) - i;
            months++;
        }
        return new(owed <= 0, months, interest);
    }

    /// <summary>The P&amp;I payment the details imply: the one entered, else worked out from amount, rate and term.</summary>
    public static decimal? PaymentFor(LoanDetails d) =>
        d.MonthlyPayment is > 0 ? d.MonthlyPayment
        : d.OriginalAmount is > 0 && d.TermMonths is > 0 ? Payment(d.OriginalAmount.Value, d.AnnualRate, d.TermMonths.Value)
        : null;
}

/// <summary>
/// Automatic interest and escrow charges on Loan accounts with LoanDetails.
/// A payment is money into the loan by transfer (e.g. from checking), on or
/// after AutoChargesFrom; for each, the loan gets an interest charge (the
/// month's interest on what was owed just before it) and, if set, an escrow
/// charge, in the chosen categories, dated with the payment and linked to it
/// (LoanChargeForTransactionId — deleted with it). The loan balance then falls
/// by the principal only.
///
/// SyncAsync recomputes every automatic charge on a loan from its data, in
/// date order, so it can run after any change (AutoLoanChargesFilter runs it
/// after every successful write request; the bill posting service after it
/// posts). Charges it made are kept in step: edited by hand they're put back.
/// </summary>
public class LoanService(AppDbContext db)
{
    public const string Interest = "interest", Escrow = "escrow";

    /// <summary>Brings every auto-charge loan of the user up to date.</summary>
    public async Task SyncUserAsync(string userId)
    {
        var ids = await db.LoanDetails.Where(l => l.UserId == userId && l.AutoCharges).Select(l => l.AccountId).ToListAsync();
        foreach (var id in ids) await SyncAsync(userId, id);
    }

    /// <summary>Every user's auto-charge loans (after background posting).</summary>
    public async Task SyncAllAsync()
    {
        var loans = await db.LoanDetails.Where(l => l.AutoCharges).Select(l => new { l.UserId, l.AccountId }).ToListAsync();
        foreach (var l in loans) await SyncAsync(l.UserId, l.AccountId);
    }

    /// <summary>Recomputes the automatic charges on one loan; returns how many charges were added, changed or removed.</summary>
    public async Task<int> SyncAsync(string userId, int accountId)
    {
        var details = await db.LoanDetails.Include(l => l.Account)
            .FirstOrDefaultAsync(l => l.AccountId == accountId && l.UserId == userId && l.Account.UserId == userId);
        if (details is null || !details.AutoCharges || details.Account.Type != AccountType.Loan) return 0;
        // A loan synced as "balance only" has no payments here; the bank's balance is the truth.
        if ((await ReportedBalances.ForUserAsync(db, userId)).ContainsKey(accountId)) return 0;

        var txs = await db.Transactions.Where(t => t.AccountId == accountId).OrderBy(t => t.Date).ThenBy(t => t.Id).ToListAsync();
        var chargesFor = txs.Where(t => t.LoanChargeForTransactionId != null)
            .GroupBy(t => t.LoanChargeForTransactionId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var balance = details.Account.OpeningBalance;
        var changes = 0;

        foreach (var t in txs.Where(t => t.LoanChargeForTransactionId == null))
        {
            var existing = chargesFor.GetValueOrDefault(t.Id) ?? [];
            var isPayment = !t.IsVoided && t.Amount > 0 && t.TransferTransactionId != null && t.Date >= details.AutoChargesFrom;
            if (!isPayment)
            {
                // No longer a payment (voided, moved earlier, unlinked…): its charges go.
                if (!t.IsVoided) balance += t.Amount;
                foreach (var c in existing) { db.Transactions.Remove(c); changes++; }
                continue;
            }

            var owed = Math.Max(0, -balance);   // what was owed just before this payment
            var escrow = Math.Min(Math.Max(0, details.EscrowAmount), t.Amount);
            var interest = Math.Min(LoanMath.MonthlyInterest(owed, details.AnnualRate), t.Amount - escrow);
            changes += Upsert(t, existing, Interest, interest, details.InterestCategoryId);
            changes += Upsert(t, existing, Escrow, escrow, details.EscrowCategoryId);
            balance += t.Amount - interest - escrow;
        }
        if (changes > 0) await db.SaveChangesAsync();
        return changes;
    }

    private int Upsert(Transaction payment, List<Transaction> existing, string kind, decimal amount, int? categoryId)
    {
        var mine = existing.Where(c => c.LoanChargeKind == kind).ToList();
        var keep = mine.FirstOrDefault();
        var changes = 0;
        foreach (var extra in mine.Skip(1)) { db.Transactions.Remove(extra); changes++; }
        if (amount <= 0)
        {
            if (keep is not null) { db.Transactions.Remove(keep); changes++; }
            return changes;
        }
        if (keep is null)
        {
            db.Transactions.Add(new Transaction
            {
                AccountId = payment.AccountId, Date = payment.Date, Amount = -amount, CategoryId = categoryId,
                PayeeId = payment.PayeeId, Status = payment.Status,
                LoanChargeForTransactionId = payment.Id, LoanChargeKind = kind,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            return changes + 1;
        }
        if (keep.Amount != -amount || keep.Date != payment.Date || keep.CategoryId != categoryId || keep.IsVoided
            || keep.PayeeId != payment.PayeeId || keep.Status != payment.Status)
        {
            keep.Amount = -amount; keep.Date = payment.Date; keep.CategoryId = categoryId; keep.IsVoided = false;
            keep.PayeeId = payment.PayeeId; keep.Status = payment.Status; keep.UpdatedAt = DateTime.UtcNow;
            changes++;
        }
        return changes;
    }
}

/// <summary>
/// After every successful write request (POST/PUT/PATCH/DELETE) by a signed-in
/// user, keeps their loans' automatic charges in step — whatever the change
/// was (a transaction edited, a transfer created or linked, an import, …).
/// Users without loan details pay one small query.
/// </summary>
public class AutoLoanChargesFilter(LoanService loans, ILogger<AutoLoanChargesFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return;
        if (executed.Exception is not null && !executed.ExceptionHandled) return;
        var status = (executed.Result as IStatusCodeActionResult)?.StatusCode ?? 200;
        if (status >= 400) return;
        var userId = context.HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return;
        try { await loans.SyncUserAsync(userId); }
        catch (Exception ex) { logger.LogError(ex, "Couldn't update automatic loan charges for user {UserId}", userId); }
    }
}
