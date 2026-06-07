using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/audit")]
public class AuditController(AppDbContext db) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);
    private bool IsAdmin() => User.IsInRole(Roles.Admin);

    [HttpGet]
    public async Task<IActionResult> GetAuditLog(
        [FromQuery] int       page       = 1,
        [FromQuery] int       pageSize   = 50,
        [FromQuery] string?   userId     = null,
        [FromQuery] string?   action     = null,
        [FromQuery] string?   entityType = null,
        [FromQuery] DateTime? from       = null,
        [FromQuery] DateTime? to         = null,
        [FromQuery] string?   search     = null)
    {
        if (pageSize > 200) pageSize = 200;
        if (page < 1) page = 1;

        var currentUserId = GetUserId();
        if (currentUserId is null) return Unauthorized();

        var query = db.AuditLogs.AsQueryable();

        // Non-admins can only see their own logs
        if (!IsAdmin())
            query = query.Where(l => l.UserId == currentUserId);
        else if (userId is not null)
            query = query.Where(l => l.UserId == userId);

        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(l => l.Action == action);

        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(l => l.EntityType == entityType);

        if (from.HasValue)
            query = query.Where(l => l.Timestamp >= from.Value);

        if (to.HasValue)
            query = query.Where(l => l.Timestamp <= to.Value);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(l =>
                (l.Details != null && l.Details.Contains(search)) ||
                (l.UserEmail != null && l.UserEmail.Contains(search)));

        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(l => l.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new
            {
                l.Id,
                l.UserId,
                l.UserEmail,
                l.Action,
                l.EntityType,
                l.EntityId,
                l.Details,
                l.IpAddress,
                timestamp = l.Timestamp.ToString("O"),
                l.IsSystem,
            })
            .ToListAsync();

        return Ok(new { total, page, pageSize, items });
    }
}
