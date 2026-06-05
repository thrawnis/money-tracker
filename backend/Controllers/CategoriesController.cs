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

        return Ok(categories.Select(c => MapCategory(c, user.EncryptedDataKey)));
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
