using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

/// <summary>Utilities → Possible Transfers: see TransferMatcher.</summary>
[ApiController]
[Authorize]
[Route("api/transfer-matches")]
public class TransferMatchesController(
    AppDbContext db,
    TransferMatcher matcher,
    UserManager<ApplicationUser> userManager,
    IAuditService audit) : ControllerBase
{
    private async Task<ApplicationUser?> CurrentUserAsync() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) is string id ? await userManager.FindByIdAsync(id) : null;

    [HttpGet]
    public async Task<IActionResult> Find([FromQuery] int days = TransferMatcher.DefaultDays, [FromQuery] bool includeCategorized = false)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var matches = await matcher.FindAsync(user, days, includeCategorized);
        return Ok(new { days = Math.Clamp(days, 0, TransferMatcher.MaxDays), matches });
    }

    public record PairDto(int OutId, int InId);
    public record LinkDto(PairDto[] Pairs);

    // Links one or more pairs. Each is re-checked; ones that no longer qualify
    // are reported rather than failing the rest.
    [HttpPost("link")]
    public async Task<IActionResult> Link(LinkDto dto)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        if (dto.Pairs is not { Length: > 0 }) return BadRequest(new { message = "Choose at least one pair." });

        var linked = 0;
        var problems = new List<string>();
        foreach (var p in dto.Pairs)
        {
            if (await matcher.LinkAsync(user, p.OutId, p.InId) is string error) problems.Add(error);
            else linked++;
        }
        if (linked > 0) await audit.LogAsync("TRANSFERS_LINKED", details: new { linked, pairs = dto.Pairs });
        if (linked == 0) return BadRequest(new { message = problems.FirstOrDefault() ?? "Nothing was linked." });
        return Ok(new { linked, problems });
    }

    // "Not a transfer": stop suggesting this pair.
    [HttpPost("dismiss")]
    public async Task<IActionResult> Dismiss(PairDto dto)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var ids = new[] { dto.OutId, dto.InId };
        if (await db.Transactions.CountAsync(t => ids.Contains(t.Id) && t.Account.UserId == user.Id) != 2) return NotFound();
        int a = Math.Min(dto.OutId, dto.InId), b = Math.Max(dto.OutId, dto.InId);
        if (!await db.DismissedTransferPairs.AnyAsync(d => d.UserId == user.Id && d.TransactionIdA == a && d.TransactionIdB == b))
        {
            db.DismissedTransferPairs.Add(new DismissedTransferPair { UserId = user.Id, TransactionIdA = a, TransactionIdB = b });
            await db.SaveChangesAsync();
        }
        return NoContent();
    }

    [HttpPost("undismiss-all")]
    public async Task<IActionResult> UndismissAll()
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var n = await db.DismissedTransferPairs.Where(d => d.UserId == user.Id).ExecuteDeleteAsync();
        return Ok(new { restored = n });
    }
}
