using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MoneyTracker.Auth;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

[ApiController]
[Route("api/demo")]
[EnableRateLimiting("auth")]
public class DemoController(DemoSeeder seeder, IConfiguration config) : ControllerBase
{
    private bool IsDemoMode => config["DEMO_MODE"] == "true";

    [HttpGet("info")]
    public IActionResult Info() =>
        Ok(new { isDemoMode = IsDemoMode, email = IsDemoMode ? DemoSeeder.DemoEmail : null });

    [HttpPost("reset")]
    [Authorize]
    public async Task<IActionResult> Reset()
    {
        if (!IsDemoMode) return NotFound();

        // Only the demo user itself (or an admin) may trigger a reset —
        // it wipes and reseeds the demo account's entire dataset.
        var email = User.FindFirstValue(ClaimTypes.Email)
                 ?? User.FindFirstValue("email");
        if (email != DemoSeeder.DemoEmail && !User.IsInRole(Roles.Admin))
            return Forbid();

        await seeder.ResetAsync();
        return Ok(new { message = "Demo data reset successfully." });
    }
}
