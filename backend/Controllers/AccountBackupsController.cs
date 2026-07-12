using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/account-backups")]
public class AccountBackupsController(IAccountBackupService accountBackups) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    [HttpGet]
    public IActionResult List()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        return Ok(accountBackups.ListBackups(userId));
    }

    [HttpGet("{fileName}/download")]
    public IActionResult Download(string fileName)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var bytes = accountBackups.ReadBackup(userId, fileName);
        if (bytes is null) return NotFound();

        return File(bytes, "application/json", fileName);
    }
}
