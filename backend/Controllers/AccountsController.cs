using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AccountsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await db.Accounts
            .Where(a => a.IsActive)
            .Include(a => a.Institution)
            .OrderBy(a => a.Name)
            .ToListAsync());

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var account = await db.Accounts
            .Include(a => a.Institution)
            .FirstOrDefaultAsync(a => a.Id == id);
        return account is null ? NotFound() : Ok(account);
    }

    [HttpPost]
    public async Task<IActionResult> Create(Account account)
    {
        account.CreatedAt = DateTime.UtcNow;
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        await db.Entry(account).Reference(a => a.Institution).LoadAsync();
        return CreatedAtAction(nameof(GetById), new { id = account.Id }, account);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, Account updated)
    {
        var account = await db.Accounts.FindAsync(id);
        if (account is null) return NotFound();

        account.Name = updated.Name;
        account.Type = updated.Type;
        account.InstitutionId = updated.InstitutionId;
        account.AccountNumber = updated.AccountNumber;
        account.Notes = updated.Notes;

        await db.SaveChangesAsync();
        await db.Entry(account).Reference(a => a.Institution).LoadAsync();
        return Ok(account);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Deactivate(int id)
    {
        var account = await db.Accounts.FindAsync(id);
        if (account is null) return NotFound();

        account.IsActive = false;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
