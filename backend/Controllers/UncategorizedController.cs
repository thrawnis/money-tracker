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

        // How each listed payee's other transactions are categorized, for reference
        // and one-click reuse: per category, how many transactions use it (a split
        // counts toward each of its categories). Transfers and voided ones aren't counted.
        var payeeIds = txs.Where(t => t.PayeeId != null).Select(t => t.PayeeId!.Value).Distinct().ToList();
        var used = await db.Transactions
            .Where(t => t.Account.UserId == userId && t.PayeeId != null && payeeIds.Contains(t.PayeeId.Value)
                && t.TransferTransactionId == null && !t.IsVoided)
            .Select(t => new { PayeeId = t.PayeeId!.Value, t.CategoryId, SplitCategories = t.Splits.Select(s => s.CategoryId).ToList() })
            .ToListAsync();
        var categories = await db.Categories.Where(c => c.UserId == userId).ToListAsync();
        var catName = categories.ToDictionary(c => c.Id, c => encryption.Decrypt(c.NameEncrypted, dek) ?? "");
        var catLabel = categories.ToDictionary(c => c.Id,
            c => c.ParentId is int p && catName.TryGetValue(p, out var parent) ? $"{parent}: {catName[c.Id]}" : catName[c.Id]);
        var usedByPayee = used
            .SelectMany(t => (t.SplitCategories.Count > 0 ? t.SplitCategories.Distinct() : [t.CategoryId])
                .Where(c => c is not null).Select(c => (t.PayeeId, CategoryId: c!.Value)))
            .GroupBy(x => x.PayeeId)
            .ToDictionary(g => g.Key, g => g
                .GroupBy(x => x.CategoryId)
                .Select(c => new { categoryId = c.Key, label = catLabel.GetValueOrDefault(c.Key, "?"), count = c.Count() })
                .OrderByDescending(c => c.count).ThenBy(c => c.label, StringComparer.OrdinalIgnoreCase)
                .ToList());

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
                    usedCategories = g.Key is int pid && usedByPayee.TryGetValue(pid, out var u) ? u : [],
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
            // By payee name (the page re-sorts with natural number order); no payee last.
            .OrderBy(g => g.payeeName is null).ThenBy(g => g.payeeName, StringComparer.OrdinalIgnoreCase)
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
