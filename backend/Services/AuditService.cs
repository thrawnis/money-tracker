using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

public interface IAuditService
{
    Task LogAsync(string action, string? entityType = null, int? entityId = null, object? details = null);
    Task LogSystemAsync(string action, string? entityType = null, object? details = null);
}

public class AuditService(AppDbContext db, IHttpContextAccessor httpContextAccessor) : IAuditService
{
    public async Task LogAsync(string action, string? entityType = null, int? entityId = null, object? details = null)
    {
        try
        {
            var context = httpContextAccessor.HttpContext;
            var userId    = context?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var userEmail = context?.User.FindFirstValue(ClaimTypes.Email);
            var ip        = GetIpAddress(context);

            var entry = new AuditLog
            {
                UserId     = userId,
                UserEmail  = userEmail,
                Action     = action,
                EntityType = entityType,
                EntityId   = entityId,
                Details    = details is null ? null : JsonSerializer.Serialize(details),
                IpAddress  = ip,
                Timestamp  = DateTime.UtcNow,
                IsSystem   = false,
            };

            db.AuditLogs.Add(entry);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AuditService] Failed to log audit entry: {ex.Message}");
        }
    }

    public async Task LogSystemAsync(string action, string? entityType = null, object? details = null)
    {
        try
        {
            var entry = new AuditLog
            {
                UserId     = null,
                UserEmail  = null,
                Action     = action,
                EntityType = entityType,
                EntityId   = null,
                Details    = details is null ? null : JsonSerializer.Serialize(details),
                IpAddress  = null,
                Timestamp  = DateTime.UtcNow,
                IsSystem   = true,
            };

            db.AuditLogs.Add(entry);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AuditService] Failed to log system audit entry: {ex.Message}");
        }
    }

    private static string? GetIpAddress(HttpContext? context)
    {
        // RemoteIpAddress only — deliberately NOT the raw X-Forwarded-For
        // header. UseForwardedHeaders (see Program.cs) has already rewritten
        // RemoteIpAddress from X-Forwarded-For when the request actually came
        // through a trusted RFC1918 proxy. Reading the header directly here
        // bypassed that trust check entirely, so any client could forge the IP
        // recorded against its own audit entries just by sending the header.
        return context?.Connection.RemoteIpAddress?.ToString();
    }
}
