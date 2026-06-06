using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class CategoriesController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

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

        // Last transaction date per category id (including subcategories)
        var allIds = categories.SelectMany(c => new[] { c.Id }.Concat(c.SubCategories.Select(s => s.Id))).ToList();
        var lastUsed = await db.Transactions
            .Where(t => t.Account.UserId == userId && t.CategoryId != null && allIds.Contains(t.CategoryId!.Value))
            .GroupBy(t => t.CategoryId!.Value)
            .Select(g => new { CategoryId = g.Key, LastDate = g.Max(t => t.Date) })
            .ToDictionaryAsync(x => x.CategoryId, x => x.LastDate);

        return Ok(categories.Select(c => new
        {
            id   = c.Id,
            name = encryption.Decrypt(c.NameEncrypted, user.EncryptedDataKey),
            lastUsed = lastUsed.TryGetValue(c.Id, out var d) ? d : (DateOnly?)null,
            subCategories = c.SubCategories.Select(s => new
            {
                id       = s.Id,
                name     = encryption.Decrypt(s.NameEncrypted, user.EncryptedDataKey),
                parentId = s.ParentId,
                lastUsed = lastUsed.TryGetValue(s.Id, out var sd) ? sd : (DateOnly?)null,
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

        var category = new Category
        {
            UserId        = userId,
            NameEncrypted = encryption.Encrypt(dto.Name, user.EncryptedDataKey)!,
            ParentId      = dto.ParentId,
        };

        db.Categories.Add(category);
        await db.SaveChangesAsync();

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

        category.NameEncrypted = encryption.Encrypt(dto.Name, user.EncryptedDataKey)!;
        category.ParentId      = dto.ParentId;

        await db.SaveChangesAsync();

        return Ok(new { id = category.Id, name = dto.Name, category.ParentId });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (category is null) return NotFound();

        db.Categories.Remove(category);
        await db.SaveChangesAsync();
        return NoContent();
    }
}

public record CategoryDto(string Name, int? ParentId);
