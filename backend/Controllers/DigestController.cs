using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MoneyTracker.Models;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

/// <summary>The daily email digest: per-user settings and a test send.</summary>
[ApiController]
[Authorize]
[Route("api/digest")]
public class DigestController(
    UserManager<ApplicationUser> userManager,
    EmailSender sender,
    EmailDigestBuilder builder,
    WeeklyDigestBuilder weekly,
    IConfiguration config,
    IAuditService audit) : ControllerBase
{
    private async Task<ApplicationUser?> CurrentUserAsync() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) is string id ? await userManager.FindByIdAsync(id) : null;

    private bool IsDemo(ApplicationUser u) =>
        config["DEMO_MODE"] == "true" && string.Equals(u.Email, "demo@example.com", StringComparison.OrdinalIgnoreCase);

    private object Settings(ApplicationUser u) => new
    {
        enabled = u.DigestEnabled,
        hour = u.DigestHour,
        includeAmounts = u.DigestIncludeAmounts,
        onlyWhenNews = u.DigestOnlyWhenNews,
        email = u.Email,
        emailConfigured = sender.IsConfigured,
        lastSentOn = u.DigestLastSentOn,
        loginAlerts = u.LoginAlertsEnabled,
        syncAlerts = u.SyncAlertsEnabled,
        largeTxAlerts = u.LargeTxAlertsEnabled,
        largeTxThreshold = u.LargeTxThreshold,
        weeklyEnabled = u.WeeklyDigestEnabled,
        weeklyDay = u.WeeklyDigestDay,
        weeklyHour = u.WeeklyDigestHour,
    };

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var user = await CurrentUserAsync();
        return user is null ? Unauthorized() : Ok(Settings(user));
    }

    public record DigestSettingsDto(
        bool Enabled, int Hour, bool IncludeAmounts, bool OnlyWhenNews, bool? LoginAlerts = null,
        bool? SyncAlerts = null, bool? LargeTxAlerts = null, decimal? LargeTxThreshold = null,
        bool? WeeklyEnabled = null, int? WeeklyDay = null, int? WeeklyHour = null);

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings(DigestSettingsDto dto)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        if (dto.Hour is < 0 or > 23) return BadRequest(new { message = "Hour must be 0-23." });
        if (dto.WeeklyDay is < 0 or > 6 || dto.WeeklyHour is < 0 or > 23) return BadRequest(new { message = "Invalid weekly schedule." });
        if (dto.LargeTxThreshold is <= 0) return BadRequest(new { message = "The large-transaction amount must be more than $0." });
        var turningOn = dto.Enabled || dto.SyncAlerts == true || dto.LargeTxAlerts == true || dto.WeeklyEnabled == true;
        if (turningOn && IsDemo(user)) return BadRequest(new { message = "The demo account can't receive email." });

        // Turning it on (or moving the hour) shouldn't wait until tomorrow just
        // because today's slot was already "used".
        if (dto.Enabled && (!user.DigestEnabled || dto.Hour != user.DigestHour)) user.DigestLastSentOn = null;
        user.DigestEnabled = dto.Enabled;
        user.DigestHour = dto.Hour;
        user.DigestIncludeAmounts = dto.IncludeAmounts;
        user.DigestOnlyWhenNews = dto.OnlyWhenNews;
        if (dto.LoginAlerts is bool la) user.LoginAlertsEnabled = la;
        if (dto.SyncAlerts is bool sa) user.SyncAlertsEnabled = sa;
        if (dto.LargeTxAlerts is bool lt) user.LargeTxAlertsEnabled = lt;
        if (dto.LargeTxThreshold is decimal amt) user.LargeTxThreshold = Math.Round(amt, 2);
        if (dto.WeeklyEnabled is bool we)
        {
            // As with the daily digest: turning it on or moving it shouldn't skip this week's send.
            if (we && (!user.WeeklyDigestEnabled || dto.WeeklyDay != user.WeeklyDigestDay || dto.WeeklyHour != user.WeeklyDigestHour))
                user.WeeklyDigestLastSentOn = null;
            user.WeeklyDigestEnabled = we;
        }
        if (dto.WeeklyDay is int wd) user.WeeklyDigestDay = wd;
        if (dto.WeeklyHour is int wh) user.WeeklyDigestHour = wh;
        await userManager.UpdateAsync(user);
        await audit.LogAsync("DIGEST_SETTINGS", details: dto);
        return Ok(Settings(user));
    }

    // Sends today's digest (or, with ?kind=weekly, this week's) right now — even
    // on a quiet day — to check delivery and see what it looks like.
    [HttpPost("test")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> SendTest([FromQuery] string? kind = null)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        if (IsDemo(user)) return BadRequest(new { message = "The demo account can't receive email." });
        if (!sender.IsConfigured)
            return BadRequest(new { message = "Email isn't set up on the server yet. Add the SMTP settings to .env and rebuild." });
        string subject, text, html;
        if (kind == "weekly")
            (subject, text, html) = await weekly.BuildAsync(user);
        else
        {
            var daily = await builder.BuildAsync(user);
            (subject, text, html) = (daily.Subject, daily.Text, daily.Html);
        }
        try
        {
            await sender.SendAsync(user.Email!, "[Test] " + subject, text, html);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Sending failed: {ex.Message}" });
        }
        return Ok(new { sentTo = user.Email });
    }
}
