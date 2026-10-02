using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// Records the value of every balance-only bank account once a day, so the app
/// builds a history of investment and retirement balances without anyone
/// pressing Sync. Each user's update runs after their chosen hour
/// (SimpleFinConnection.DailyUpdateHour, default 8 pm) in their own time zone.
///
/// Checks every 10 minutes. A user is due when it's past their hour today and
/// today's update hasn't succeeded yet. After a failure it waits an hour before
/// trying again, and stops at midnight: a missed day stays missed, since SimpleFIN
/// only reports today's balance. A server that was down at 8 pm catches up
/// as soon as it starts, any time before midnight.
/// </summary>
public class SimpleFinDailyUpdateService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<SimpleFinDailyUpdateService> logger) : BackgroundService
{
    // Overridable (SimpleFin:DailyUpdateCheckSeconds) only so tests needn't wait.
    private TimeSpan Interval => TimeSpan.FromSeconds(config.GetValue("SimpleFin:DailyUpdateCheckSeconds", 600));
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Daily SimpleFIN balance update failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    public async Task RunDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Connections with the daily update on and at least one active
        // balance-only account linked; the time-of-day check is per user below.
        var candidates = await db.SimpleFinConnections
            .Where(c => c.DailyUpdateHour != null
                && c.Accounts.Any(a => a.BalanceOnly && a.LinkedAccount != null && a.LinkedAccount.IsActive))
            .Select(c => new { c.Id, c.UserId, c.DailyUpdateHour, c.LastAutoUpdateDate, c.LastAutoAttemptAt })
            .ToListAsync(ct);

        foreach (var c in candidates)
        {
            // Each user gets a fresh scope so one user's tracked entities and
            // failures can't leak into the next.
            using var userScope = scopeFactory.CreateScope();
            var userDb = userScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = userScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(c.UserId);
            if (user is null || string.IsNullOrWhiteSpace(user.TimeZoneId)) continue;

            var now = UserClock.Now(user.TimeZoneId);
            var today = DateOnly.FromDateTime(now);
            if (now.Hour < c.DailyUpdateHour) continue;
            if (c.LastAutoUpdateDate == today) continue;
            if (c.LastAutoAttemptAt is DateTime last && DateTime.UtcNow - last < RetryAfter) continue;

            var conn = await userDb.SimpleFinConnections.FirstAsync(x => x.Id == c.Id, ct);
            conn.LastAutoAttemptAt = DateTime.UtcNow;
            await userDb.SaveChangesAsync(ct);

            try
            {
                var service = userScope.ServiceProvider.GetRequiredService<SimpleFinService>();
                var recorded = await service.UpdateBalancesAsync(user, ct);
                conn.LastAutoUpdateDate = today;
                await userDb.SaveChangesAsync(ct);
                logger.LogInformation("Daily SimpleFIN update recorded {Count} balance(s) for a user", recorded);
            }
            catch (SimpleFinException ex)
            {
                // Already saved to the connection's errors for the user to see.
                logger.LogWarning("Daily SimpleFIN update failed for a user: {Message}", ex.Message);
            }
        }
    }
}
