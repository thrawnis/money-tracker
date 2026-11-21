using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

/// <summary>
/// Settings → Backups: the user's daily backups (UserBackupService) — what
/// they include, the list, "Back up now", restoring (all or part, accounts
/// replaced or as copies) and downloading. Restoring and downloading require
/// the same re-authentication as deleting an account (X-Export-Token).
/// </summary>
[ApiController]
[Authorize]
[Route("api/backups")]
public class BackupsController(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    UserBackupService backups,
    UserBackupRestore restorer,
    IAuditService audit,
    ILogger<BackupsController> logger) : ControllerBase
{
    private async Task<ApplicationUser?> CurrentUserAsync() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) is string id ? await userManager.FindByIdAsync(id) : null;

    /// <summary>Uses up a re-authentication token (POST /api/export/confirm-identity); false if missing or expired.</summary>
    private async Task<bool> ConsumeTokenAsync(string userId, string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var stored = await db.ExportTokens.FirstOrDefaultAsync(t => t.Token == token && t.UserId == userId && !t.IsUsed && t.ExpiresAt > DateTime.UtcNow);
        if (stored is null) return false;
        stored.IsUsed = true;
        await db.SaveChangesAsync();
        return true;
    }

    [HttpGet]
    public async Task<IActionResult> Overview()
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var included = backups.SectionsFor(user).ToList();
        return Ok(new
        {
            sections = BackupSections.All.Select(s => new { id = s, label = BackupSections.Label(s), included = included.Contains(s) }),
            backups = backups.List(user.Id).Select(b => new
            {
                b.FileName, b.CreatedAt, b.Kind, b.Sections, b.Counts, b.SizeBytes,
                full = BackupSections.All.All(b.Sections.Contains),
                // The manifest: every area, whether it's in this backup, and what it holds.
                manifest = BackupSections.All.Select(sec => new
                {
                    id = sec, label = BackupSections.Label(sec), included = b.Sections.Contains(sec),
                    detail = b.Sections.Contains(sec) ? UserBackupService.Detail(sec, b.Counts) : null,
                }),
            }),
            keep = UserBackupService.Keep,
            dailyAt = $"{DailyBackupService.Hour % 12}:{DailyBackupService.Minute:00} pm Pacific Time",
            lastDailyBackupOn = user.LastDailyBackupOn,
            lastError = user.BackupLastError,
            available = SevenZip.Available,
        });
    }

    public record SettingsDto(List<string> IncludedSections);

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings(SettingsDto dto)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var included = dto.IncludedSections.Where(BackupSections.All.Contains).ToHashSet();
        if (included.Count == 0) return BadRequest(new { message = "Choose at least one thing to back up." });
        var excluded = BackupSections.All.Where(s => !included.Contains(s)).ToList();
        user.BackupExcludedSections = excluded.Count == 0 ? null : string.Join(',', excluded);
        await userManager.UpdateAsync(user);
        await audit.LogAsync("BACKUP_SETTINGS_CHANGED", details: new { excluded });
        return await Overview();
    }

    /// <summary>Back up now (counts toward the 7 kept).</summary>
    [HttpPost]
    public async Task<IActionResult> BackUpNow()
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        if (!SevenZip.Available) return StatusCode(503, new { message = "Backups aren't available: 7-Zip isn't installed on the server." });
        try
        {
            var name = await UserBackupService.ExclusiveAsync(user.Id, () => backups.CreateAsync(user, "manual"));
            await audit.LogAsync("BACKUP_CREATED", details: new { file = name });
            return Ok(new { fileName = name });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Manual backup failed for user {UserId}", user.Id);
            return StatusCode(500, new { message = $"The backup failed: {ex.GetBaseException().Message}" });
        }
    }

    /// <summary>What's in a backup, for the restore screen: each account (and whether it still exists), and counts.</summary>
    [HttpGet("{fileName}")]
    public async Task<IActionResult> Summary(string fileName)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        if (backups.PathFor(user.Id, fileName) is null) return NotFound();
        BackupSnapshot snap;
        try { snap = await UserBackupService.ExclusiveAsync(user.Id, () => backups.ReadAsync(user, fileName)); }
        catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
        var current = await db.Accounts.AsNoTracking().Where(a => a.UserId == user.Id).Select(a => new { a.Id, a.Name }).ToListAsync();
        var inBackup = snap.Accounts.Select(a => a.Id).ToHashSet();
        return Ok(new
        {
            fileName, snap.CreatedAt, snap.Kind, snap.Sections,
            counts = snap.Counts(),
            accounts = snap.Accounts.Select(a => new
            {
                id = a.Id, name = a.Name, type = a.Type.ToString(), isActive = a.IsActive,
                transactions = snap.Transactions.Count(t => t.AccountId == a.Id),
                balance = a.OpeningBalance + snap.Transactions.Where(t => t.AccountId == a.Id && !t.IsVoided).Sum(t => t.Amount),
                current = current.FirstOrDefault(c => c.Id == a.Id)?.Name
                    ?? current.FirstOrDefault(c => c.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase) && !inBackup.Contains(c.Id))?.Name,
            }),
            // Current accounts a whole restore would remove (not in this backup).
            notInBackup = current.Where(c => !snap.Accounts.Any(a => a.Id == c.Id || a.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))).Select(c => c.Name),
        });
    }

    /// <summary>
    /// Restores from a backup (see UserBackupRestore). A full "before-restore"
    /// backup of the current data is taken first — if that fails, nothing is restored.
    /// </summary>
    [HttpPost("{fileName}/restore")]
    public async Task<IActionResult> Restore(string fileName, RestoreRequest dto, [FromHeader(Name = "X-Export-Token")] string? exportToken)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        if (backups.PathFor(user.Id, fileName) is null) return NotFound();
        if (!await ConsumeTokenAsync(user.Id, exportToken)) return Unauthorized(new { message = "Identity verification required or expired." });
        if (dto.Sections is not { Count: > 0 }) return BadRequest(new { message = "Choose what to restore." });
        if (dto.Sections.Contains(BackupSections.Accounts) && dto.Accounts is not { Count: > 0 } && !dto.RemoveOtherAccounts)
            return BadRequest(new { message = "Choose which accounts to restore." });

        try
        {
            var (safety, result) = await UserBackupService.ExclusiveAsync(user.Id, async () =>
            {
                var snap = await backups.ReadAsync(user, fileName);
                // Undo point: everything as it is right now.
                var safetyFile = await backups.CreateAsync(user, "before-restore", BackupSections.All);
                var r = await restorer.RestoreAsync(user, snap, dto with { Accounts = dto.Accounts ?? [] });
                return (safetyFile, r);
            });
            await audit.LogAsync("BACKUP_RESTORED", details: new { file = fileName, dto.Sections, accounts = dto.Accounts?.Count ?? 0, dto.RemoveOtherAccounts, safety });
            return Ok(new { lines = result.Lines, warnings = result.Warnings, safetyBackup = safety });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore of {File} failed for user {UserId}", fileName, user.Id);
            return StatusCode(500, new { message = $"Nothing was changed — the restore failed: {ex.GetBaseException().Message}" });
        }
    }

    public record DownloadDto(string? Password);

    /// <summary>A copy of the backup to keep: 7z encrypted with the password given now, or unencrypted when it's blank.</summary>
    [HttpPost("{fileName}/download")]
    public async Task<IActionResult> Download(string fileName, DownloadDto dto, [FromHeader(Name = "X-Export-Token")] string? exportToken)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        if (backups.PathFor(user.Id, fileName) is null) return NotFound();
        if (!await ConsumeTokenAsync(user.Id, exportToken)) return Unauthorized(new { message = "Identity verification required or expired." });
        if (dto.Password is { Length: > 0 and < 8 }) return BadRequest(new { message = "Use a password of at least 8 characters (or none)." });
        if (dto.Password?.Length > 200) return BadRequest(new { message = "That password is too long." });
        try
        {
            var bytes = await UserBackupService.ExclusiveAsync(user.Id, () => backups.DownloadAsync(user, fileName, dto.Password));
            await audit.LogAsync("BACKUP_DOWNLOADED", details: new { file = fileName, encrypted = !string.IsNullOrEmpty(dto.Password) });
            Response.Headers.CacheControl = "no-store";
            return File(bytes, "application/x-7z-compressed", fileName);
        }
        catch (Exception ex) { return StatusCode(500, new { message = $"The download couldn't be prepared: {ex.Message}" }); }
    }
}
