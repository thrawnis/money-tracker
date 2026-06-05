using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Controllers;

[ApiController]
[Route("api/accounts/{accountId}/transactions")]
public class TransactionsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetByAccount(
        int accountId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var query = db.Transactions
            .Where(t => t.AccountId == accountId)
            .Include(t => t.Payee)
            .Include(t => t.Category)
            .AsQueryable();

        if (from.HasValue) query = query.Where(t => t.Date >= from.Value);
        if (to.HasValue)   query = query.Where(t => t.Date <= to.Value);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { total, page, pageSize, items });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int accountId, int id)
    {
        var tx = await db.Transactions
            .Include(t => t.Payee)
            .Include(t => t.Category)
            .FirstOrDefaultAsync(t => t.Id == id && t.AccountId == accountId);

        return tx is null ? NotFound() : Ok(tx);
    }

    [HttpPost]
    public async Task<IActionResult> Create(int accountId, Transaction transaction)
    {
        transaction.AccountId = accountId;
        transaction.CreatedAt = DateTime.UtcNow;
        transaction.UpdatedAt = DateTime.UtcNow;
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { accountId, id = transaction.Id }, transaction);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int accountId, int id, Transaction updated)
    {
        var tx = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.AccountId == accountId);
        if (tx is null) return NotFound();

        tx.Date = updated.Date;
        tx.CheckNumber = updated.CheckNumber;
        tx.PayeeId = updated.PayeeId;
        tx.CategoryId = updated.CategoryId;
        tx.Memo = updated.Memo;
        tx.Amount = updated.Amount;
        tx.Status = updated.Status;
        tx.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return Ok(tx);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int accountId, int id)
    {
        var tx = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.AccountId == accountId);
        if (tx is null) return NotFound();

        db.Transactions.Remove(tx);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
