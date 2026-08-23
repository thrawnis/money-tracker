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
public class PayeesController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager,
    IAuditService audit) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var payees = await db.Payees
            .Where(p => p.UserId == userId)
            .ToListAsync();

        var payeeIds = payees.Select(p => p.Id).ToList();
        // Covers full history (including future-dated transactions), matching the
        // First/Last Transaction semantics used on the Accounts and Categories pages.
        var stats = await db.Transactions
            .Where(t => t.Account.UserId == userId && t.PayeeId != null && payeeIds.Contains(t.PayeeId!.Value))
            .GroupBy(t => t.PayeeId!.Value)
            .Select(g => new { PayeeId = g.Key, First = g.Min(t => t.Date), Last = g.Max(t => t.Date), Count = g.Count() })
            .ToDictionaryAsync(x => x.PayeeId, x => x);

        var result = payees
            .Select(p => new
            {
                id                = p.Id,
                name              = encryption.Decrypt(p.NameEncrypted, user.EncryptedDataKey),
                defaultCategoryId = p.DefaultCategoryId,
                blockAutoDefaultCategory = p.BlockAutoDefaultCategory,
                firstUsed         = stats.TryGetValue(p.Id, out var s) ? s.First : (DateOnly?)null,
                lastUsed          = stats.TryGetValue(p.Id, out var s2) ? s2.Last : (DateOnly?)null,
                transactionCount  = stats.TryGetValue(p.Id, out var s3) ? s3.Count : 0,
            })
            .OrderBy(p => p.name);

        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create(PayeeDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var existing = await db.Payees.Where(p => p.UserId == userId).ToListAsync();
        var duplicate = existing.Any(p =>
            string.Equals(encryption.Decrypt(p.NameEncrypted, user.EncryptedDataKey), dto.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
            return Conflict(new { message = $"A payee named \"{dto.Name}\" already exists." });

        if (dto.DefaultCategoryId.HasValue &&
            !await db.Categories.AnyAsync(c => c.Id == dto.DefaultCategoryId.Value && c.UserId == userId))
            return BadRequest(new { message = "Category not found." });

        // A payee created with the block flag already set can't also arrive
        // with a default — same rule Update enforces (see there for why).
        var payee = new Payee
        {
            UserId                   = userId,
            NameEncrypted            = encryption.Encrypt(dto.Name, user.EncryptedDataKey)!,
            DefaultCategoryId        = dto.BlockAutoDefaultCategory ? null : dto.DefaultCategoryId,
            BlockAutoDefaultCategory = dto.BlockAutoDefaultCategory,
        };

        db.Payees.Add(payee);
        await db.SaveChangesAsync();

        await audit.LogAsync("CREATE", "Payee", payee.Id, new { defaultCategoryId = payee.DefaultCategoryId, blockAutoDefaultCategory = payee.BlockAutoDefaultCategory });

        return CreatedAtAction(nameof(GetAll), new { },
            new { id = payee.Id, name = dto.Name, payee.DefaultCategoryId, payee.BlockAutoDefaultCategory });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, PayeeDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var payee = await db.Payees.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (payee is null) return NotFound();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var existing = await db.Payees.Where(p => p.UserId == userId && p.Id != id).ToListAsync();
        var duplicate = existing.Any(p =>
            string.Equals(encryption.Decrypt(p.NameEncrypted, user.EncryptedDataKey), dto.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
            return Conflict(new { message = $"A payee named \"{dto.Name}\" already exists." });

        if (dto.DefaultCategoryId.HasValue &&
            !await db.Categories.AnyAsync(c => c.Id == dto.DefaultCategoryId.Value && c.UserId == userId))
            return BadRequest(new { message = "Category not found." });

        payee.NameEncrypted     = encryption.Encrypt(dto.Name, user.EncryptedDataKey)!;
        // Blocking and having a default at the same time is a contradiction a
        // user would immediately want undone — turning the block on clears
        // whatever default (auto-set or manual) was already there, rather than
        // leaving a stale suggestion in place that the block is supposedly
        // preventing.
        payee.DefaultCategoryId        = dto.BlockAutoDefaultCategory ? null : dto.DefaultCategoryId;
        payee.BlockAutoDefaultCategory = dto.BlockAutoDefaultCategory;

        await db.SaveChangesAsync();

        await audit.LogAsync("UPDATE", "Payee", id, new { defaultCategoryId = payee.DefaultCategoryId, blockAutoDefaultCategory = payee.BlockAutoDefaultCategory });

        return Ok(new { id = payee.Id, name = dto.Name, payee.DefaultCategoryId, payee.BlockAutoDefaultCategory });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var payee = await db.Payees.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (payee is null) return NotFound();

        db.Payees.Remove(payee);
        await db.SaveChangesAsync();

        await audit.LogAsync("DELETE", "Payee", id);

        return NoContent();
    }
}

public record PayeeDto(string Name, int? DefaultCategoryId, bool BlockAutoDefaultCategory = false);
