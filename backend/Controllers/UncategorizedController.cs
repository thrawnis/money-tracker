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

/// <summary>
/// Utilities → Uncategorized by Payee: every uncategorized transaction grouped
/// by payee, and assigning categories to single transactions, whole payees or
/// several payees at once. "Uncategorized" matches the Dashboard: no category,
/// not a transfer, not split (splits carry their own categories), not voided.
/// </summary>
[ApiController]
[Authorize]
[Route("api/uncategorized")]
public class UncategorizedController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager,
    IAuditService audit) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private IQueryable<Transaction> Uncategorized(string userId) =>
        db.Transactions.Where(t => t.Account.UserId == userId && t.CategoryId == null
            && t.TransferTransactionId == null && !t.IsVoided && !t.Splits.Any());

    [HttpGet]
    public async Task<IActionResult> GetGroups()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        var txs = await Uncategorized(userId)
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.Id)
            .Select(t => new { t.Id, t.AccountId, AccountName = t.Account.Name, t.Date, t.Amount, t.MemoEncrypted, t.PayeeId, t.Payee })
            .ToListAsync();

        var groups = txs
            .GroupBy(t => t.PayeeId)
            .Select(g =>
            {
                var payee = g.First().Payee;
                return new
                {
                    payeeId = g.Key,
                    payeeName = payee is null ? null : encryption.Decrypt(payee.NameEncrypted, dek),
                    hasDefaultCategory = payee?.DefaultCategoryId != null,
                    count = g.Count(),
                    total = g.Sum(t => t.Amount),
                    transactions = g.Select(t => new
                    {
                        id = t.Id,
                        accountId = t.AccountId,
                        accountName = t.AccountName,
                        date = t.Date,
                        amount = t.Amount,
                        memo = encryption.Decrypt(t.MemoEncrypted, dek),
                    }).ToList(),
                };
            })
            // Most transactions first: those are the biggest wins.
            .OrderByDescending(g => g.count).ThenBy(g => g.payeeName ?? "￿", StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new { total = txs.Count, groups });
    }

    /// <summary>
    /// Sets a category on uncategorized transactions: the listed transaction ids,
    /// plus every uncategorized transaction of the listed payees (and those with
    /// no payee when includeNoPayee) as of now — so nothing that arrived after
    /// the list was loaded is missed. SetPayeeDefault also makes the category
    /// those payees' default, so future imports and bank syncs arrive categorized.
    /// Transactions that are no longer uncategorized are left alone.
    /// </summary>
    public record AssignDto(int CategoryId, int[]? TransactionIds, int[]? PayeeIds, bool IncludeNoPayee = false, bool SetPayeeDefault = false);

    [HttpPost("assign")]
    public async Task<IActionResult> Assign(AssignDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        if (!await db.Categories.AnyAsync(c => c.Id == dto.CategoryId && c.UserId == userId))
            return BadRequest(new { message = "Category not found." });

        var txIds = (dto.TransactionIds ?? []).Distinct().ToList();
        var payeeIds = (dto.PayeeIds ?? []).Distinct().ToList();
        if (txIds.Count == 0 && payeeIds.Count == 0 && !dto.IncludeNoPayee)
            return BadRequest(new { message = "Choose transactions or payees." });
        if (payeeIds.Count > 0 && await db.Payees.CountAsync(p => p.UserId == userId && payeeIds.Contains(p.Id)) != payeeIds.Count)
            return NotFound(new { message = "Payee not found." });

        var txs = await Uncategorized(userId)
            .Where(t => txIds.Contains(t.Id)
                || (t.PayeeId != null && payeeIds.Contains(t.PayeeId.Value))
                || (dto.IncludeNoPayee && t.PayeeId == null))
            .ToListAsync();
        foreach (var t in txs)
        {
            t.CategoryId = dto.CategoryId;
            t.UpdatedAt = DateTime.UtcNow;
        }

        var defaultsSet = 0;
        if (dto.SetPayeeDefault && payeeIds.Count > 0)
        {
            var payees = await db.Payees.Where(p => p.UserId == userId && payeeIds.Contains(p.Id)).ToListAsync();
            foreach (var p in payees)
            {
                p.DefaultCategoryId = dto.CategoryId;
                p.BlockAutoDefaultCategory = false;   // an explicit choice overrides the block
            }
            defaultsSet = payees.Count;
        }

        await db.SaveChangesAsync();
        await audit.LogAsync("UNCATEGORIZED_ASSIGNED", "Category", dto.CategoryId,
            new { updated = txs.Count, payees = payeeIds.Count, dto.IncludeNoPayee, defaultsSet });
        return Ok(new { updated = txs.Count, defaultsSet });
    }
}
