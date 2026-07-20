using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

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
