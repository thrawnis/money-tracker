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
public class InstitutionsController(
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

        return Ok(await db.Institutions
            .Where(i => i.UserId == userId)
            .OrderBy(i => i.Name)
            .Select(i => new { i.Id, i.Name })
            .ToListAsync());
    }

    [HttpPost]
    public async Task<IActionResult> Create(InstitutionDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var duplicate = await db.Institutions.AnyAsync(i =>
            i.UserId == userId && i.Name.ToLower() == dto.Name.ToLower());
        if (duplicate)
            return Conflict(new { message = $"A bank/institution named \"{dto.Name}\" already exists." });

        var institution = new Institution
        {
            UserId = userId,
            Name   = dto.Name,
        };

        db.Institutions.Add(institution);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), new { },
            new { institution.Id, institution.Name });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, InstitutionDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var institution = await db.Institutions.FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId);
        if (institution is null) return NotFound();

        var duplicate = await db.Institutions.AnyAsync(i =>
            i.UserId == userId && i.Id != id && i.Name.ToLower() == dto.Name.ToLower());
        if (duplicate)
            return Conflict(new { message = $"A bank/institution named \"{dto.Name}\" already exists." });

        institution.Name = dto.Name;
        await db.SaveChangesAsync();

        return Ok(new { institution.Id, institution.Name });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var institution = await db.Institutions.FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId);
        if (institution is null) return NotFound();

        bool inUse = await db.Accounts.AnyAsync(a => a.InstitutionId == id && a.UserId == userId);
        if (inUse)
            return Conflict("Institution is assigned to one or more accounts and cannot be deleted.");

        db.Institutions.Remove(institution);
        await db.SaveChangesAsync();
        return NoContent();
    }
}

public record InstitutionDto(string Name);
