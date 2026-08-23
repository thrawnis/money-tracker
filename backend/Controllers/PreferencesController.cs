using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MoneyTracker.Auth;
using MoneyTracker.Auth.Dtos;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/preferences")]
public class PreferencesController(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    JwtService jwtService,
    IAuditService audit) : ControllerBase
{
    private static readonly string[] ValidSortFields = ["date", "payee", "category", "memo", "amount", "status"];
    private static readonly string[] ValidSortDirs = ["asc", "desc"];

    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        return Ok(new
        {
            defaultRegisterSortBy       = user.DefaultRegisterSortBy,
            defaultRegisterSortDir      = user.DefaultRegisterSortDir,
            defaultFutureDays           = user.DefaultFutureDays,
            autoCreateFutureTransactions = user.AutoCreateFutureTransactions,
            autoCreateFutureDays         = user.AutoCreateFutureDays,
            timeZoneId                   = user.TimeZoneId,
        });
    }

    /// <summary>Every IANA zone this host can resolve, for the Settings picker.</summary>
    // Reachable before a timezone is chosen — it's what the mandatory picker lists.
    [AllowWithoutTimeZone]
    [HttpGet("timezones")]
    public IActionResult GetTimeZones() =>
        Ok(TimeZoneInfo.GetSystemTimeZones()
            .Select(tz => new { id = tz.Id, displayName = tz.DisplayName })
            .OrderBy(tz => tz.id));

    /// <summary>
    /// Sets just the timezone, and re-mints the access token so the new "tz"
    /// claim takes effect immediately rather than after the current one
    /// expires. This is the one write a gated user is allowed to make, which
    /// is why it's separate from the full preferences PUT — that would let
    /// them change unrelated settings while still gated.
    /// </summary>
    [AllowWithoutTimeZone]
    [HttpPut("timezone")]
    public async Task<IActionResult> SetTimeZone(TimeZoneDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(dto.TimeZoneId) || !UserClock.IsValidTimeZone(dto.TimeZoneId))
            return BadRequest(new { message = "A valid time zone is required." });

        user.TimeZoneId = dto.TimeZoneId;
        await db.SaveChangesAsync();

        var roles = await userManager.GetRolesAsync(user);
        var role  = roles.Contains(Roles.Admin) ? Roles.Admin : Roles.Standard;
        var (accessToken, expiry) = jwtService.GenerateAccessToken(user, role);

        await audit.LogAsync("SET_TIMEZONE", "User", null, new { timeZoneId = dto.TimeZoneId });

        return Ok(new TokenResponse(accessToken, expiry, role, user.MfaEnrolled, RequiresTimeZone: false));
    }

    [HttpPut]
    public async Task<IActionResult> Update(PreferencesDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        if (dto.DefaultRegisterSortBy is not null && !ValidSortFields.Contains(dto.DefaultRegisterSortBy))
            return BadRequest(new { message = "Invalid sort field." });
        if (dto.DefaultRegisterSortDir is not null && !ValidSortDirs.Contains(dto.DefaultRegisterSortDir))
            return BadRequest(new { message = "Invalid sort direction." });
        if (dto.DefaultFutureDays is int days && (days < 1 || days > 3650))
            return BadRequest(new { message = "Days ahead must be between 1 and 3650." });
        if (dto.AutoCreateFutureDays is int autoDays && (autoDays < 1 || autoDays > 3650))
            return BadRequest(new { message = "Auto-create days ahead must be between 1 and 3650." });
        if (!string.IsNullOrWhiteSpace(dto.TimeZoneId) && !UserClock.IsValidTimeZone(dto.TimeZoneId))
            return BadRequest(new { message = "Unknown timezone." });

        user.DefaultRegisterSortBy        = dto.DefaultRegisterSortBy;
        user.DefaultRegisterSortDir       = dto.DefaultRegisterSortDir;
        user.DefaultFutureDays            = dto.DefaultFutureDays;
        user.AutoCreateFutureTransactions = dto.AutoCreateFutureTransactions;
        user.AutoCreateFutureDays         = dto.AutoCreateFutureDays;
        user.TimeZoneId                   = string.IsNullOrWhiteSpace(dto.TimeZoneId) ? null : dto.TimeZoneId;
        await db.SaveChangesAsync();

        return Ok(new
        {
            defaultRegisterSortBy       = user.DefaultRegisterSortBy,
            defaultRegisterSortDir      = user.DefaultRegisterSortDir,
            defaultFutureDays           = user.DefaultFutureDays,
            autoCreateFutureTransactions = user.AutoCreateFutureTransactions,
            autoCreateFutureDays         = user.AutoCreateFutureDays,
            timeZoneId                   = user.TimeZoneId,
        });
    }
}

// Null fields mean "use the built-in default" — lets the user explicitly reset
// to (date, newest first, 31 days) instead of just omitting a field they never set.
public record PreferencesDto(
    string? DefaultRegisterSortBy,
    string? DefaultRegisterSortDir,
    int? DefaultFutureDays,
    bool AutoCreateFutureTransactions = false,
    int? AutoCreateFutureDays = null,
    // IANA id ("America/Chicago"). Null/empty means UTC — see UserClock.
    string? TimeZoneId = null);

public record TimeZoneDto(string TimeZoneId);
