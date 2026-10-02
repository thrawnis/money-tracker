using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MoneyTracker.Models;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

/// <summary>
/// Bank sync through SimpleFIN Bridge. Syncing only stages import drafts;
/// nothing reaches the register until the user reviews and commits a draft
/// through the regular import flow (see SimpleFinService).
/// </summary>
[ApiController]
[Authorize]
[Route("api/simplefin")]
public class SimpleFinController(
    UserManager<ApplicationUser> userManager,
    SimpleFinService simpleFin) : ControllerBase
{
    private async Task<ApplicationUser?> GetUserAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return userId is null ? null : await userManager.FindByIdAsync(userId);
    }

    [HttpGet]
    public async Task<IActionResult> Status()
    {
        var user = await GetUserAsync();
        if (user is null) return Unauthorized();
        return Ok(await simpleFin.GetStatusAsync(user));
    }

    public record ConnectDto(string SetupToken);

    [HttpPost("connect")]
    public async Task<IActionResult> Connect(ConnectDto dto, CancellationToken ct)
    {
        var user = await GetUserAsync();
        if (user is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(dto.SetupToken))
            return BadRequest(new { message = "Paste the setup token from SimpleFIN Bridge." });

        try
        {
            await simpleFin.ConnectAsync(user, dto.SetupToken, ct);
        }
        catch (SimpleFinConflictException ex) { return Conflict(new { message = ex.Message }); }
        catch (SimpleFinException ex) { return BadRequest(new { message = ex.Message }); }

        return Ok(await simpleFin.GetStatusAsync(user));
    }

    [HttpDelete]
    public async Task<IActionResult> Disconnect()
    {
        var user = await GetUserAsync();
        if (user is null) return Unauthorized();
        await simpleFin.DisconnectAsync(user);
        return NoContent();
    }

    public record LinkDto(int? AccountId);

    [HttpPut("accounts/{id}/link")]
    public async Task<IActionResult> Link(int id, LinkDto dto)
    {
        var user = await GetUserAsync();
        if (user is null) return Unauthorized();

        try
        {
            await simpleFin.LinkAsync(user, id, dto.AccountId);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (SimpleFinConflictException ex) { return Conflict(new { message = ex.Message }); }

        return Ok(await simpleFin.GetStatusAsync(user));
    }

    public record ModeDto(bool BalanceOnly);

    // Switches a bank account between importing transactions and recording
    // only its balance (for investment/retirement accounts).
    [HttpPut("accounts/{id}/mode")]
    public async Task<IActionResult> SetMode(int id, ModeDto dto)
    {
        var user = await GetUserAsync();
        if (user is null) return Unauthorized();
        try
        {
            await simpleFin.SetBalanceOnlyAsync(user, id, dto.BalanceOnly);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        return Ok(await simpleFin.GetStatusAsync(user));
    }

    // Hour: 0-23 in the user's time zone, or null to turn the daily
    // automatic balance update off.
    public record DailyUpdateDto(int? Hour);

    [HttpPut("daily-update")]
    public async Task<IActionResult> SetDailyUpdate(DailyUpdateDto dto)
    {
        var user = await GetUserAsync();
        if (user is null) return Unauthorized();
        if (dto.Hour is < 0 or > 23) return BadRequest(new { message = "Choose an hour between 0 and 23." });
        try
        {
            await simpleFin.SetDailyUpdateHourAsync(user, dto.Hour);
        }
        catch (KeyNotFoundException) { return NotFound(new { message = "SimpleFIN isn't connected." }); }
        return Ok(await simpleFin.GetStatusAsync(user));
    }

    // AccountIds: the SimpleFIN accounts to sync this time (the Sync screen's
    // checkboxes). Omitted or null syncs every linked account.
    public record SyncDto(int[]? AccountIds);

    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] SyncDto? dto, CancellationToken ct)
    {
        var user = await GetUserAsync();
        if (user is null) return Unauthorized();

        try
        {
            return Ok(await simpleFin.SyncAsync(user, dto?.AccountIds, ct));
        }
        catch (KeyNotFoundException) { return NotFound(new { message = "SimpleFIN isn't connected." }); }
        catch (SimpleFinConflictException ex) { return Conflict(new { message = ex.Message }); }
        catch (SimpleFinException ex) { return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message }); }
        catch (SimpleFinRateLimitException ex)
        {
            if (ex.RetryAt is DateTime at)
                Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((at - DateTime.UtcNow).TotalSeconds)).ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = ex.Message, retryAt = ex.RetryAt });
        }
    }

    // Forget that a transaction was skipped, so the next sync offers it again.
    [HttpDelete("skipped/{id}")]
    public async Task<IActionResult> RestoreSkipped(int id)
    {
        var user = await GetUserAsync();
        if (user is null) return Unauthorized();

        try
        {
            await simpleFin.RestoreSkippedAsync(user, id);
        }
        catch (KeyNotFoundException) { return NotFound(); }

        return Ok(await simpleFin.GetStatusAsync(user));
    }
}
