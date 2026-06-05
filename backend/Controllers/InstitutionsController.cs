using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class InstitutionsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await db.Institutions.OrderBy(i => i.Name).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Create(Institution institution)
    {
        db.Institutions.Add(institution);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetAll), new { }, institution);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, Institution updated)
    {
        var institution = await db.Institutions.FindAsync(id);
        if (institution is null) return NotFound();

        institution.Name = updated.Name;
        await db.SaveChangesAsync();
        return Ok(institution);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var institution = await db.Institutions.FindAsync(id);
        if (institution is null) return NotFound();

        bool inUse = await db.Accounts.AnyAsync(a => a.InstitutionId == id);
        if (inUse)
            return Conflict("Institution is assigned to one or more accounts and cannot be deleted.");

        db.Institutions.Remove(institution);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
