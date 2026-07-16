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
[Route("api/[controller]")]
public class CategoriesController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager,
    IAuditService audit) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// ParentId, when set, must be the caller's own top-level category —
    /// cross-user parents are an IDOR, and parenting under a subcategory
    /// would create a third hierarchy level the app doesn't support.
    /// </summary>
    private async Task<string?> ValidateParent(int? parentId, string userId)
    {
        if (!parentId.HasValue) return null;
        var parent = await db.Categories.FirstOrDefaultAsync(c => c.Id == parentId.Value && c.UserId == userId);
        if (parent is null) return "Parent category not found.";
        if (parent.ParentId is not null) return "Subcategories cannot have their own subcategories.";
        return null;
    }

    private object MapCategory(Category c, string dek) => new
    {
        id         = c.Id,
        name       = encryption.Decrypt(c.NameEncrypted, dek),
        parentId   = c.ParentId,
        subCategories = c.SubCategories.Select(s => new
        {
            id       = s.Id,
            name     = encryption.Decrypt(s.NameEncrypted, dek),
            parentId = s.ParentId,
        }),
    };

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var categories = await db.Categories
            .Where(c => c.UserId == userId && c.ParentId == null)
            .Include(c => c.SubCategories)
            .ToListAsync();

        // First/last transaction date and transaction count per category id (including
        // subcategories), combining direct transactions and split lines that reference
        // the category. Dates cover the full history (including future-dated rows),
        // matching how First/Last Transaction work on the Accounts page.
        var allIds = categories.SelectMany(c => new[] { c.Id }.Concat(c.SubCategories.Select(s => s.Id))).ToList();

        var directStats = await db.Transactions
            .Where(t => t.Account.UserId == userId && t.CategoryId != null && allIds.Contains(t.CategoryId!.Value))
            .GroupBy(t => t.CategoryId!.Value)
            .Select(g => new { CategoryId = g.Key, First = g.Min(t => t.Date), Last = g.Max(t => t.Date), Count = g.Count() })
            .ToListAsync();

        var splitStats = await db.TransactionSplits
            .Where(s => s.Transaction.Account.UserId == userId && s.CategoryId != null && allIds.Contains(s.CategoryId!.Value))
            .GroupBy(s => s.CategoryId!.Value)
            .Select(g => new { CategoryId = g.Key, First = g.Min(s => s.Transaction.Date), Last = g.Max(s => s.Transaction.Date), Count = g.Count() })
            .ToListAsync();

        var combined = directStats.Concat(splitStats).GroupBy(x => x.CategoryId).ToDictionary(
            g => g.Key,
            g => new { First = g.Min(x => x.First), Last = g.Max(x => x.Last), Count = g.Sum(x => x.Count) });

        DateOnly? FirstOf(int id) => combined.TryGetValue(id, out var s) ? s.First : null;
        DateOnly? LastOf(int id)  => combined.TryGetValue(id, out var s) ? s.Last : null;
        int CountOf(int id)       => combined.TryGetValue(id, out var s) ? s.Count : 0;

        return Ok(categories.Select(c => new
        {
            id               = c.Id,
            name             = encryption.Decrypt(c.NameEncrypted, user.EncryptedDataKey),
            firstUsed        = FirstOf(c.Id),
            lastUsed         = LastOf(c.Id),
            transactionCount = CountOf(c.Id),
            subCategories = c.SubCategories.Select(s => new
            {
                id               = s.Id,
                name             = encryption.Decrypt(s.NameEncrypted, user.EncryptedDataKey),
                parentId         = s.ParentId,
                firstUsed        = FirstOf(s.Id),
                lastUsed         = LastOf(s.Id),
                transactionCount = CountOf(s.Id),
            }),
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CategoryDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        if (await ValidateParent(dto.ParentId, userId) is string parentError)
            return BadRequest(new { message = parentError });

        var siblings = await db.Categories
            .Where(c => c.UserId == userId && c.ParentId == dto.ParentId)
            .ToListAsync();
        var duplicate = siblings.Any(c =>
            string.Equals(encryption.Decrypt(c.NameEncrypted, user.EncryptedDataKey), dto.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
            return Conflict(new { message = $"A category named \"{dto.Name}\" already exists." });

        var category = new Category
        {
            UserId        = userId,
            NameEncrypted = encryption.Encrypt(dto.Name, user.EncryptedDataKey)!,
            ParentId      = dto.ParentId,
        };

        db.Categories.Add(category);
        await db.SaveChangesAsync();

        await audit.LogAsync("CREATE", "Category", category.Id, new { parentId = dto.ParentId });

        return CreatedAtAction(nameof(GetAll), new { },
            new { id = category.Id, name = dto.Name, category.ParentId });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, CategoryDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (category is null) return NotFound();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        if (dto.ParentId == id)
            return BadRequest(new { message = "A category cannot be its own parent." });
        if (dto.ParentId.HasValue && await db.Categories.AnyAsync(c => c.ParentId == id))
            return BadRequest(new { message = "A category with subcategories cannot become a subcategory itself." });
        if (await ValidateParent(dto.ParentId, userId) is string parentError)
            return BadRequest(new { message = parentError });

        var siblings = await db.Categories
            .Where(c => c.UserId == userId && c.ParentId == dto.ParentId && c.Id != id)
            .ToListAsync();
        var duplicate = siblings.Any(c =>
            string.Equals(encryption.Decrypt(c.NameEncrypted, user.EncryptedDataKey), dto.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
            return Conflict(new { message = $"A category named \"{dto.Name}\" already exists." });

        category.NameEncrypted = encryption.Encrypt(dto.Name, user.EncryptedDataKey)!;
        category.ParentId      = dto.ParentId;

        await db.SaveChangesAsync();

        await audit.LogAsync("UPDATE", "Category", id, new { parentId = dto.ParentId });

        return Ok(new { id = category.Id, name = dto.Name, category.ParentId });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (category is null) return NotFound();

        if (await db.Categories.AnyAsync(c => c.ParentId == id))
            return Conflict(new { message = "Cannot delete a category that has subcategories." });

        var inUse = await db.Transactions.AnyAsync(t => t.CategoryId == id)
            || await db.TransactionSplits.AnyAsync(s => s.CategoryId == id)
            || await db.ScheduledTransactions.AnyAsync(s => s.CategoryId == id);
        if (inUse)
            return Conflict(new { message = "Cannot delete a category that is in use." });

        db.Categories.Remove(category);
        await db.SaveChangesAsync();

        await audit.LogAsync("DELETE", "Category", id);

        return NoContent();
    }
}

public record CategoryDto(string Name, int? ParentId);
