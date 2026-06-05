using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class ScheduledTransactionsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await db.ScheduledTransactions
            .Where(s => s.IsActive)
            .Include(s => s.Payee)
            .Include(s => s.Category)
            .OrderBy(s => s.NextDueDate)
            .ToListAsync());

    [HttpGet("upcoming")]
    public async Task<IActionResult> GetUpcoming([FromQuery] int days = 14)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(days));
        var today  = DateOnly.FromDateTime(DateTime.UtcNow);

        var items = await db.ScheduledTransactions
            .Where(s => s.IsActive && s.NextDueDate <= cutoff)
            .Include(s => s.Payee)
            .Include(s => s.Category)
            .Include(s => s.Account)
            .OrderBy(s => s.NextDueDate)
            .ToListAsync();

        return Ok(items);
    }

    [HttpPost]
    public async Task<IActionResult> Create(ScheduledTransaction scheduled)
    {
        scheduled.CreatedAt = DateTime.UtcNow;
        db.ScheduledTransactions.Add(scheduled);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetAll), new { }, scheduled);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, ScheduledTransaction updated)
    {
        var scheduled = await db.ScheduledTransactions.FindAsync(id);
        if (scheduled is null) return NotFound();

        scheduled.Name = updated.Name;
        scheduled.AccountId = updated.AccountId;
        scheduled.PayeeId = updated.PayeeId;
        scheduled.CategoryId = updated.CategoryId;
        scheduled.Memo = updated.Memo;
        scheduled.Amount = updated.Amount;
        scheduled.Frequency = updated.Frequency;
        scheduled.NextDueDate = updated.NextDueDate;
        scheduled.ReminderDays = updated.ReminderDays;

        await db.SaveChangesAsync();
        return Ok(scheduled);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Deactivate(int id)
    {
        var scheduled = await db.ScheduledTransactions.FindAsync(id);
        if (scheduled is null) return NotFound();

        scheduled.IsActive = false;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
