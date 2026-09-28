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

    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        var user = await GetUserAsync();
        if (user is null) return Unauthorized();

        try
        {
            return Ok(await simpleFin.SyncAsync(user, ct));
        }
        catch (KeyNotFoundException) { return NotFound(new { message = "SimpleFIN isn't connected." }); }
        catch (SimpleFinConflictException ex) { return Conflict(new { message = ex.Message }); }
        catch (SimpleFinException ex) { return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message }); }
    }
}
