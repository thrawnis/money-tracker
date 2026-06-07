using Microsoft.AspNetCore.Mvc;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

[ApiController]
[Route("api/demo")]
public class DemoController(DemoSeeder seeder, IConfiguration config) : ControllerBase
{
    private bool IsDemoMode => config["DEMO_MODE"] == "true";

    [HttpGet("info")]
    public IActionResult Info() =>
        Ok(IsDemoMode
            ? new { isDemoMode = true, email = DemoSeeder.DemoEmail }
            : new { isDemoMode = false, email = (string?)null });

    [HttpPost("reset")]
    public async Task<IActionResult> Reset()
    {
        if (!IsDemoMode) return NotFound();
        await seeder.ResetAsync();
        return Ok(new { message = "Demo data reset successfully." });
    }
}
