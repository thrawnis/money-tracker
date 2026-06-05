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
public class PayeesController(
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

        var payees = await db.Payees
            .Where(p => p.UserId == userId)
            .ToListAsync();

        var result = payees
            .Select(p => new
            {
                id                = p.Id,
                name              = encryption.Decrypt(p.NameEncrypted, user.EncryptedDataKey),
                defaultCategoryId = p.DefaultCategoryId,
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

        var payee = new Payee
        {
            UserId            = userId,
            NameEncrypted     = encryption.Encrypt(dto.Name, user.EncryptedDataKey)!,
            DefaultCategoryId = dto.DefaultCategoryId,
        };

        db.Payees.Add(payee);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), new { },
            new { id = payee.Id, name = dto.Name, payee.DefaultCategoryId });
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

        payee.NameEncrypted     = encryption.Encrypt(dto.Name, user.EncryptedDataKey)!;
        payee.DefaultCategoryId = dto.DefaultCategoryId;

        await db.SaveChangesAsync();

        return Ok(new { id = payee.Id, name = dto.Name, payee.DefaultCategoryId });
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
        return NoContent();
    }
}

public record PayeeDto(string Name, int? DefaultCategoryId);
