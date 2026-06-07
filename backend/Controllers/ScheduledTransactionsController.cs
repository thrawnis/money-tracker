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
public class ScheduledTransactionsController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private object MapScheduled(ScheduledTransaction s, string dek) => new
    {
        id           = s.Id,
        name         = s.Name,
        accountId    = s.AccountId,
        account      = s.Account is null ? null : new { s.Account.Id, s.Account.Name },
        payeeId      = s.PayeeId,
        payee        = s.Payee is null ? null : new
        {
            id   = s.Payee.Id,
            name = encryption.Decrypt(s.Payee.NameEncrypted, dek),
        },
        categoryId   = s.CategoryId,
        category     = s.Category is null ? null : new
        {
            id   = s.Category.Id,
            name = encryption.Decrypt(s.Category.NameEncrypted, dek),
        },
        memo         = encryption.Decrypt(s.MemoEncrypted, dek),
        amount              = s.Amount,
        frequencyInterval   = s.FrequencyInterval,
        frequencyUnit       = s.FrequencyUnit,
        nextDueDate         = s.NextDueDate,
        reminderDays = s.ReminderDays,
        isActive     = s.IsActive,
        transferAccountId = s.TransferAccountId,
        createdAt    = s.CreatedAt,
    };

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var items = await db.ScheduledTransactions
            .Where(s => s.UserId == userId && s.IsActive)
            .Include(s => s.Account)
            .Include(s => s.Payee)
            .Include(s => s.Category)
            .OrderBy(s => s.NextDueDate)
            .ToListAsync();

        return Ok(items.Select(s => MapScheduled(s, user.EncryptedDataKey)));
    }

    [HttpGet("upcoming")]
    public async Task<IActionResult> GetUpcoming(
        [FromQuery] int  days      = 14,
        [FromQuery] int? accountId = null,
        [FromQuery] int  limit     = 5,
        [FromQuery] int  skip      = 0)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var from   = DateOnly.FromDateTime(DateTime.UtcNow);
        var cutoff = from.AddDays(days);

        var query = db.ScheduledTransactions
            .Where(s => s.UserId == userId && s.IsActive && s.NextDueDate >= from && s.NextDueDate <= cutoff);

        if (accountId.HasValue)
            query = query.Where(s => s.AccountId == accountId.Value);

        var items = await query
            .Include(s => s.Payee)
            .Include(s => s.Category)
            .Include(s => s.Account)
            .OrderBy(s => s.NextDueDate)
            .Skip(skip)
            .Take(limit)
            .ToListAsync();

        return Ok(items.Select(s => MapScheduled(s, user.EncryptedDataKey)));
    }

    [HttpPost]
    public async Task<IActionResult> Create(ScheduledTransactionDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var scheduled = new ScheduledTransaction
        {
            UserId        = userId,
            Name          = dto.Name,
            AccountId     = dto.AccountId,
            PayeeId       = dto.PayeeId,
            CategoryId    = dto.CategoryId,
            TransferAccountId = dto.TransferAccountId,
            MemoEncrypted      = encryption.Encrypt(dto.Memo, user.EncryptedDataKey),
            Amount             = dto.Amount,
            FrequencyInterval  = dto.FrequencyInterval,
            FrequencyUnit      = dto.FrequencyUnit,
            NextDueDate        = dto.NextDueDate,
            ReminderDays  = dto.ReminderDays,
            CreatedAt     = DateTime.UtcNow,
        };

        db.ScheduledTransactions.Add(scheduled);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), new { },
            MapScheduled(scheduled, user.EncryptedDataKey));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, ScheduledTransactionDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var scheduled = await db.ScheduledTransactions
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (scheduled is null) return NotFound();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        scheduled.Name          = dto.Name;
        scheduled.AccountId     = dto.AccountId;
        scheduled.PayeeId       = dto.PayeeId;
        scheduled.CategoryId    = dto.CategoryId;
        scheduled.TransferAccountId = dto.TransferAccountId;
        scheduled.MemoEncrypted = encryption.Encrypt(dto.Memo, user.EncryptedDataKey);
        scheduled.Amount            = dto.Amount;
        scheduled.FrequencyInterval = dto.FrequencyInterval;
        scheduled.FrequencyUnit     = dto.FrequencyUnit;
        scheduled.NextDueDate       = dto.NextDueDate;
        scheduled.ReminderDays  = dto.ReminderDays;

        await db.SaveChangesAsync();

        return Ok(MapScheduled(scheduled, user.EncryptedDataKey));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Deactivate(int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var scheduled = await db.ScheduledTransactions
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (scheduled is null) return NotFound();

        scheduled.IsActive = false;
        await db.SaveChangesAsync();
        return NoContent();
    }
}

public record ScheduledTransactionDto(
    string Name,
    int AccountId,
    int? PayeeId,
    int? CategoryId,
    string? Memo,
    decimal Amount,
    int FrequencyInterval,
    FrequencyUnit FrequencyUnit,
    DateOnly NextDueDate,
    int ReminderDays,
    int? TransferAccountId);
