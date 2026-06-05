using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class PayeesController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await db.Payees.OrderBy(p => p.Name).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Create(Payee payee)
    {
        db.Payees.Add(payee);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetAll), new { }, payee);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, Payee updated)
    {
        var payee = await db.Payees.FindAsync(id);
        if (payee is null) return NotFound();

        payee.Name = updated.Name;
        payee.DefaultCategoryId = updated.DefaultCategoryId;
        await db.SaveChangesAsync();
        return Ok(payee);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var payee = await db.Payees.FindAsync(id);
        if (payee is null) return NotFound();

        db.Payees.Remove(payee);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
