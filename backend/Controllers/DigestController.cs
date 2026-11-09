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
    };

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var user = await CurrentUserAsync();
        return user is null ? Unauthorized() : Ok(Settings(user));
    }

    public record DigestSettingsDto(bool Enabled, int Hour, bool IncludeAmounts, bool OnlyWhenNews, bool? LoginAlerts = null);

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings(DigestSettingsDto dto)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        if (dto.Hour is < 0 or > 23) return BadRequest(new { message = "Hour must be 0-23." });
        if (dto.Enabled && IsDemo(user)) return BadRequest(new { message = "The demo account can't receive email." });

        // Turning it on (or moving the hour) shouldn't wait until tomorrow just
        // because today's slot was already "used".
        if (dto.Enabled && (!user.DigestEnabled || dto.Hour != user.DigestHour)) user.DigestLastSentOn = null;
        user.DigestEnabled = dto.Enabled;
        user.DigestHour = dto.Hour;
        user.DigestIncludeAmounts = dto.IncludeAmounts;
        user.DigestOnlyWhenNews = dto.OnlyWhenNews;
        if (dto.LoginAlerts is bool la) user.LoginAlertsEnabled = la;
        await userManager.UpdateAsync(user);
        await audit.LogAsync("DIGEST_SETTINGS", details: new { dto.Enabled, dto.Hour, dto.IncludeAmounts, dto.OnlyWhenNews, dto.LoginAlerts });
        return Ok(Settings(user));
    }

    // Sends today's digest right now (even on a quiet day), to check delivery.
    [HttpPost("test")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> SendTest()
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        if (IsDemo(user)) return BadRequest(new { message = "The demo account can't receive email." });
        if (!sender.IsConfigured)
            return BadRequest(new { message = "Email isn't set up on the server yet. Add the SMTP settings to .env and rebuild." });
        var digest = await builder.BuildAsync(user);
        try
        {
            await sender.SendAsync(user.Email!, "[Test] " + digest.Subject, digest.Text, digest.Html);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Sending failed: {ex.Message}" });
        }
        return Ok(new { sentTo = user.Email });
    }
}
