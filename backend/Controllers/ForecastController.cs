using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

/// <summary>Cash-flow forecast: projected balances from future-dated transactions and scheduled bills.</summary>
[ApiController]
[Authorize]
[Route("api/forecast")]
public class ForecastController(
    AppDbContext db,
    CashFlowForecast forecast,
    UserManager<ApplicationUser> userManager,
    IAuditService audit) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    // watchedOnly: just checking, savings and cash (the Dashboard tile).
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int days = 30, [FromQuery] bool watchedOnly = false)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var accounts = await forecast.ForUserAsync(user, days, watchedOnly ? CashFlowForecast.WatchedTypes : null);
        return Ok(new { asOf = UserClock.Today(user), days = Math.Clamp(days, 1, 366), accounts });
    }

    public record ThresholdDto(decimal? Threshold);

    // Sets the balance the forecast warns below (null = the default, $0).
    [HttpPut("accounts/{id}/threshold")]
    public async Task<IActionResult> SetThreshold(int id, ThresholdDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId);
        if (account is null) return NotFound();
        account.LowBalanceThreshold = dto.Threshold;
        await db.SaveChangesAsync();
        await audit.LogAsync("UPDATE", "Account", id, new { lowBalanceThreshold = dto.Threshold });
        return NoContent();
    }
}
