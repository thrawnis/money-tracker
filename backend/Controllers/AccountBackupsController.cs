using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/account-backups")]
public class AccountBackupsController(AppDbContext db, IAccountBackupService accountBackups) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    [HttpGet]
    public IActionResult List()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        return Ok(accountBackups.ListBackups(userId));
    }

    // Downloading a backup requires the same re-authenticated, single-use
    // exportToken as Export (POST /api/export/confirm-identity) — the file is
    // still ciphertext, but the user asked for password re-entry on every
    // backup download too, as defense in depth.
    [HttpGet("{fileName}/download")]
    public async Task<IActionResult> Download(string fileName, [FromQuery] string exportToken)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var stored = await db.ExportTokens
            .FirstOrDefaultAsync(t => t.Token == exportToken && t.UserId == userId
                                   && !t.IsUsed && t.ExpiresAt > DateTime.UtcNow);
        if (stored is null) return Unauthorized("Identity verification required or expired.");

        var bytes = accountBackups.ReadBackup(userId, fileName);
        if (bytes is null) return NotFound();

        stored.IsUsed = true;
        await db.SaveChangesAsync();

        return File(bytes, "application/json", fileName);
    }
}
