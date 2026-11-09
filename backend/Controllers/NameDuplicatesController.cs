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
/// Utilities → Find Duplicate Names: suggests payees and categories that are
/// probably the same (see NameSimilarity) and merges the ones the user picks.
/// Exact duplicates (same name ignoring case) are merged automatically at
/// sign-in, so what shows here are near-matches.
/// </summary>
[ApiController]
[Authorize]
[Route("api/name-duplicates")]
public class NameDuplicatesController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager,
    DuplicateNameMergeService merger,
    IAuditService audit) : ControllerBase
{
    private async Task<ApplicationUser?> CurrentUserAsync() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) is string id ? await userManager.FindByIdAsync(id) : null;

    [HttpGet]
    public async Task<IActionResult> Find()
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        // ── Payees ──
        var payees = (await db.Payees.Where(p => p.UserId == user.Id).ToListAsync())
            .Select(p => new { p.Id, Name = encryption.Decrypt(p.NameEncrypted, dek) ?? "", p.DefaultCategoryId })
            .ToList();
        var payeeTx = await db.Transactions.Where(t => t.Account.UserId == user.Id && t.PayeeId != null)
            .GroupBy(t => t.PayeeId!.Value).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
        var payeeGroups = NameSimilarity.Group(payees, p => p.Name, allowPrefix: true)
            .Select(g => g
                .Select(p => new { id = p.Id, name = p.Name, transactionCount = payeeTx.GetValueOrDefault(p.Id), hasDefaultCategory = p.DefaultCategoryId != null })
                .OrderByDescending(p => p.transactionCount).ThenBy(p => p.name)
                .ToList())
            .OrderBy(g => g[0].name)
            .ToList();

        // ── Categories (only compared with siblings: same parent, or both top-level) ──
        var cats = (await db.Categories.Where(c => c.UserId == user.Id).ToListAsync())
            .Select(c => new { c.Id, c.ParentId, Name = encryption.Decrypt(c.NameEncrypted, dek) ?? "" })
            .ToList();
        var names = cats.ToDictionary(c => c.Id, c => c.Name);
        var catTx = await db.Transactions.Where(t => t.Account.UserId == user.Id && t.CategoryId != null)
            .GroupBy(t => t.CategoryId!.Value).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
        var splitTx = await db.TransactionSplits.Where(s => s.Transaction.Account.UserId == user.Id && s.CategoryId != null)
            .GroupBy(s => s.CategoryId!.Value).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
        var childCount = cats.Where(c => c.ParentId != null).GroupBy(c => c.ParentId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var categoryGroups = cats.GroupBy(c => c.ParentId)
            .SelectMany(siblings => NameSimilarity.Group(siblings.ToList(), c => c.Name, allowPrefix: false))
            .Select(g => g
                .Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    parentName = c.ParentId is int pid ? names.GetValueOrDefault(pid) : null,
                    transactionCount = catTx.GetValueOrDefault(c.Id) + splitTx.GetValueOrDefault(c.Id),
                    subcategoryCount = childCount.GetValueOrDefault(c.Id),
                })
                .OrderByDescending(c => c.transactionCount).ThenBy(c => c.name)
                .ToList())
            .OrderBy(g => g[0].parentName ?? "").ThenBy(g => g[0].name)
            .ToList();

        return Ok(new { payees = payeeGroups, categories = categoryGroups });
    }

    public record MergeDto(int KeepId, int[] MergeIds);

    [HttpPost("payees/merge")]
    public async Task<IActionResult> MergePayees(MergeDto dto)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        try
        {
            var n = await merger.MergePayeesIntoAsync(user, dto.KeepId, dto.MergeIds ?? []);
            await audit.LogAsync("PAYEES_MERGED", "Payee", dto.KeepId, new { merged = dto.MergeIds });
            return Ok(new { merged = n });
        }
        catch (KeyNotFoundException) { return NotFound(new { message = "Payee not found." }); }
    }

    [HttpPost("categories/merge")]
    public async Task<IActionResult> MergeCategories(MergeDto dto)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        try
        {
            var n = await merger.MergeCategoriesIntoAsync(user, dto.KeepId, dto.MergeIds ?? []);
            await audit.LogAsync("CATEGORIES_MERGED", "Category", dto.KeepId, new { merged = dto.MergeIds });
            return Ok(new { merged = n });
        }
        catch (KeyNotFoundException) { return NotFound(new { message = "Category not found." }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }
}
