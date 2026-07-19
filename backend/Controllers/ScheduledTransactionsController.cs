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
public class ScheduledTransactionsController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager,
    ScheduledTransactionPostingService postingService) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// Verifies all FK references in the DTO belong to the calling user.
    /// Returns an error message, or null when everything checks out.
    /// </summary>
    private async Task<string?> ValidateReferences(ScheduledTransactionDto dto, string userId)
    {
        if (!await db.Accounts.AnyAsync(a => a.Id == dto.AccountId && a.UserId == userId))
            return "Account not found.";

        if (dto.PayeeId.HasValue &&
            !await db.Payees.AnyAsync(p => p.Id == dto.PayeeId.Value && p.UserId == userId))
            return "Payee not found.";

        if (dto.CategoryId.HasValue &&
            !await db.Categories.AnyAsync(c => c.Id == dto.CategoryId.Value && c.UserId == userId))
            return "Category not found.";

        if (dto.TransferAccountId.HasValue &&
            !await db.Accounts.AnyAsync(a => a.Id == dto.TransferAccountId.Value && a.UserId == userId))
            return "Transfer account not found.";

        if (dto.DaysOfWeekMask is int mask && (mask <= 0 || mask > 0b1111111))
            return "Invalid days-of-week selection.";

        return null;
    }

    // Only meaningful for Weeks — normalized here so it can never linger
    // stale on a schedule that's since been switched to Days/Months/Years.
    private static int? NormalizedDaysOfWeekMask(ScheduledTransactionDto dto) =>
        dto.FrequencyUnit == FrequencyUnit.Weeks ? dto.DaysOfWeekMask : null;

    // occurrenceDate overrides NextDueDate when this represents one of possibly
    // several projected occurrences of the same schedule (see GetUpcoming) —
    // the stored record only ever holds the schedule's single earliest due date.
    private object MapScheduled(ScheduledTransaction s, string dek, DateOnly? occurrenceDate = null) => new
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
            id       = s.Category.Id,
            name     = encryption.Decrypt(s.Category.NameEncrypted, dek),
            parentId = s.Category.ParentId,
        },
        memo         = encryption.Decrypt(s.MemoEncrypted, dek),
        // Transfers always debit the source account when posted (see
        // CreateTransferPairAsync), regardless of the sign stored on the
        // schedule — mirror that here so the preview matches what actually
        // gets created.
        amount              = s.TransferAccountId.HasValue ? -Math.Abs(s.Amount) : s.Amount,
        frequencyInterval   = s.FrequencyInterval,
        frequencyUnit       = s.FrequencyUnit,
        daysOfWeekMask      = s.DaysOfWeekMask,
        nextDueDate         = occurrenceDate ?? s.NextDueDate,
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

        // NextDueDate is always each schedule's EARLIEST occurrence (the posting
        // job keeps it current), so "NextDueDate <= cutoff" is both necessary and
        // sufficient to find every schedule that has at least one occurrence in
        // the window — occurrences only ever move forward from there.
        var query = db.ScheduledTransactions
            .Where(s => s.UserId == userId && s.IsActive && s.NextDueDate <= cutoff);

        if (accountId.HasValue)
            query = query.Where(s => s.AccountId == accountId.Value);

        var schedules = await query
            .Include(s => s.Payee)
            .Include(s => s.Category)
            .Include(s => s.Account)
            .ToListAsync();

        // Occurrences already materialized as real transactions (posted when
        // due, or pre-created by the auto-create-future preference) shouldn't
        // also show as a projected "upcoming" preview — they're showing up as
        // real, editable transactions in the register instead.
        var scheduleIds = schedules.Select(s => s.Id).ToList();
        var materialized = await db.Transactions
            .Where(t => t.ScheduledTransactionId != null && scheduleIds.Contains(t.ScheduledTransactionId!.Value)
                     && t.Date >= from && t.Date <= cutoff)
            .Select(t => new { ScheduledTransactionId = t.ScheduledTransactionId!.Value, t.Date })
            .ToListAsync();
        var materializedSet = materialized.Select(m => (m.ScheduledTransactionId, m.Date)).ToHashSet();

        // Project every occurrence of each schedule within [from, cutoff] — not
        // just the next one — so e.g. a weekly bill shows every week it's due
        // within the selected "days ahead" range, not a single row.
        var occurrences = new List<(ScheduledTransaction Sched, DateOnly Date)>();
        foreach (var s in schedules)
        {
            var date = s.NextDueDate;
            var guard = 0;
            while (date <= cutoff && guard++ < 366)
            {
                if (date >= from && !materializedSet.Contains((s.Id, date))) occurrences.Add((s, date));
                date = ScheduledTransactionPostingService.Advance(date, s);
            }
        }

        var page = occurrences
            .OrderBy(o => o.Date).ThenBy(o => o.Sched.Id)
            .Skip(skip)
            .Take(limit)
            .ToList();

        return Ok(page.Select(o => MapScheduled(o.Sched, user.EncryptedDataKey, o.Date)));
    }

    [HttpPost]
    public async Task<IActionResult> Create(ScheduledTransactionDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        if (await ValidateReferences(dto, userId) is string refError)
            return BadRequest(new { message = refError });

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
            DaysOfWeekMask     = NormalizedDaysOfWeekMask(dto),
            NextDueDate        = dto.NextDueDate,
            ReminderDays  = dto.ReminderDays,
            CreatedAt     = DateTime.UtcNow,
        };

        db.ScheduledTransactions.Add(scheduled);
        await db.SaveChangesAsync();

        // Materialize immediately rather than waiting for the next 6-hour
        // background tick — a bill due today/soon, or within the user's
        // auto-create window, should show as a real transaction right away.
        await postingService.PostDueAsync(HttpContext.RequestAborted);
        await postingService.CreateFutureAsync(HttpContext.RequestAborted);

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

        if (await ValidateReferences(dto, userId) is string refError)
            return BadRequest(new { message = refError });

        scheduled.Name          = dto.Name;
        scheduled.AccountId     = dto.AccountId;
        scheduled.PayeeId       = dto.PayeeId;
        scheduled.CategoryId    = dto.CategoryId;
        scheduled.TransferAccountId = dto.TransferAccountId;
        scheduled.MemoEncrypted = encryption.Encrypt(dto.Memo, user.EncryptedDataKey);
        scheduled.Amount            = dto.Amount;
        scheduled.FrequencyInterval = dto.FrequencyInterval;
        scheduled.FrequencyUnit     = dto.FrequencyUnit;
        scheduled.DaysOfWeekMask    = NormalizedDaysOfWeekMask(dto);
        scheduled.NextDueDate       = dto.NextDueDate;
        scheduled.ReminderDays  = dto.ReminderDays;

        await db.SaveChangesAsync();

        // Same reasoning as Create: an edited NextDueDate/amount/etc. should
        // be reflected in materialized transactions immediately, not after
        // up to 6 hours.
        await postingService.PostDueAsync(HttpContext.RequestAborted);
        await postingService.CreateFutureAsync(HttpContext.RequestAborted);

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
    int? TransferAccountId,
    int? DaysOfWeekMask = null);
