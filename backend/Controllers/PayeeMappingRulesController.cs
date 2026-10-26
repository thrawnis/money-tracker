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
/// Rules that map a raw payee string seen in an imported file (e.g.
/// "AMAZON F98797") to one of the user's existing Payees, so future imports
/// resolve straight to the right payee instead of creating a near-duplicate.
/// </summary>
[ApiController]
[Authorize]
[Route("api/payee-mapping-rules")]
public class PayeeMappingRulesController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        var rules = await db.PayeeMappingRules
            .Where(r => r.UserId == userId)
            .Include(r => r.TargetPayee)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return Ok(rules.Select(r => new
        {
            id = r.Id,
            pattern = encryption.Decrypt(r.PatternEncrypted, dek),
            isRegex = r.IsRegex,
            targetPayeeId = r.TargetPayeeId,
            targetPayeeName = encryption.Decrypt(r.TargetPayee.NameEncrypted, dek),
            createdAt = r.CreatedAt,
        }));
    }

    public record RuleDto(string Pattern, bool IsRegex, int TargetPayeeId);

    [HttpPost]
    public async Task<IActionResult> Create(RuleDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        if (string.IsNullOrWhiteSpace(dto.Pattern))
            return BadRequest(new { message = "Pattern is required." });

        if (!await db.Payees.AnyAsync(p => p.Id == dto.TargetPayeeId && p.UserId == userId))
            return BadRequest(new { message = "Target payee not found." });

        var rule = new PayeeMappingRule
        {
            UserId = userId,
            PatternEncrypted = encryption.Encrypt(dto.Pattern.Trim(), dek)!,
            IsRegex = dto.IsRegex,
            TargetPayeeId = dto.TargetPayeeId,
        };
        db.PayeeMappingRules.Add(rule);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), new { }, new { id = rule.Id });
    }

    /// <summary>
    /// Runs the mapping rules over existing transactions that have no category:
    /// a payee whose name matches a rule is replaced by the rule's payee, and the
    /// (possibly new) payee's default category is filled in. Transfers, splits and
    /// voided transactions are left alone. Payees left unused by the rename are
    /// removed unless they carry a default category or are a rule's target.
    /// </summary>
    [HttpPost("apply")]
    public async Task<IActionResult> ApplyToUncategorized()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        var rules = await db.PayeeMappingRules.Where(r => r.UserId == userId).OrderBy(r => r.Id).ToListAsync();
        var patterns = rules.ToDictionary(r => r.Id, r => encryption.Decrypt(r.PatternEncrypted, dek) ?? "");
        var payees = await db.Payees.Where(p => p.UserId == userId).ToDictionaryAsync(p => p.Id);
        var payeeNames = payees.Values.ToDictionary(p => p.Id, p => encryption.Decrypt(p.NameEncrypted, dek) ?? "");

        var txs = await db.Transactions
            .Where(t => t.Account.UserId == userId && t.CategoryId == null && t.PayeeId != null
                && t.TransferTransactionId == null && t.TransferAccountId == null
                && !t.IsVoided && !t.Splits.Any())
            .ToListAsync();

        int renamed = 0, categorized = 0;
        var oldPayeeIds = new HashSet<int>();
        foreach (var tx in txs)
        {
            var rule = PayeePatternMatcher.FindMatch(rules, payeeNames[tx.PayeeId!.Value], r => patterns[r.Id], r => r.IsRegex);
            if (rule is not null && rule.TargetPayeeId != tx.PayeeId)
            {
                oldPayeeIds.Add(tx.PayeeId.Value);
                tx.PayeeId = rule.TargetPayeeId;
                renamed++;
            }
            if (payees.TryGetValue(tx.PayeeId.Value, out var payee) && payee.DefaultCategoryId is int catId)
            {
                tx.CategoryId = catId;
                categorized++;
            }
            if (db.Entry(tx).State == EntityState.Modified) tx.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        var removedPayees = 0;
        foreach (var id in oldPayeeIds)
        {
            if (payees[id].DefaultCategoryId.HasValue
                || await db.Transactions.AnyAsync(t => t.PayeeId == id)
                || await db.ScheduledTransactions.AnyAsync(s => s.PayeeId == id)
                || await db.PayeeMappingRules.AnyAsync(r => r.TargetPayeeId == id)) continue;
            db.Payees.Remove(payees[id]);
            removedPayees++;
        }
        await db.SaveChangesAsync();

        return Ok(new { checkedCount = txs.Count, renamed, categorized, removedPayees });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var rule = await db.PayeeMappingRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (rule is null) return NotFound();

        db.PayeeMappingRules.Remove(rule);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
