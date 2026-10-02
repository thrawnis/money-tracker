using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// The nightly sync: once a day, after the chosen hour (SimpleFinConnection.
/// DailyUpdateHour, default 8 pm) in Pacific Time for every user, syncs every
/// linked bank account without anyone pressing Sync. Transaction accounts
/// get their new transactions staged for review (a waiting review grows to
/// cover every day not yet approved, keeping the choices already made);
/// balance-only accounts get the day's value recorded, building their history.
///
/// Users are processed one after another, never in parallel, and each fetch
/// also goes through SimpleFinService's app-wide one-at-a-time gate, so the
/// daily run never overlaps a manual sync either.
///
/// Checks every 10 minutes. A user is due when it's past their hour today and
/// today's sync hasn't succeeded yet. After a temporary failure (timeout,
/// outage) it waits an hour before trying again, so a night gets at most a
/// handful of tries (four from 8 pm) before stopping at midnight; each one
/// counts toward the connection's daily request limit. A permanent failure
/// (access revoked, subscription inactive) isn't retried that night at all —
/// it needs the user to act, and is shown in Bank Sync. A missed night costs nothing for
/// transaction accounts (the next sync fetches everything since the last
/// approval), but a balance-only account's value for that day stays missing,
/// since SimpleFIN only reports today's balance. A server that was down at
/// 8 pm catches up as soon as it starts, any time before midnight.
/// </summary>
public class SimpleFinDailyUpdateService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<SimpleFinDailyUpdateService> logger) : BackgroundService
{
    // Overridable (SimpleFin:DailyUpdateCheckSeconds) only so tests needn't wait.
    private TimeSpan Interval => TimeSpan.FromSeconds(config.GetValue("SimpleFin:DailyUpdateCheckSeconds", 600));
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);
    public const string ScheduleTimeZone = "America/Los_Angeles";

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

        // Connections with the nightly sync on and at least one active account
        // linked; the time-of-day check is below.
        var candidates = await db.SimpleFinConnections
            .Where(c => c.DailyUpdateHour != null
                && c.Accounts.Any(a => a.LinkedAccount != null && a.LinkedAccount.IsActive))
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

            // The schedule runs on Pacific Time for everyone, whatever the
            // user's own time zone (values are still dated in theirs).
            var now = UserClock.Now(ScheduleTimeZone);
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
                var result = await service.SyncAsync(user, null, ct, SyncTrigger.Nightly);
                conn.LastAutoUpdateDate = today;
                await userDb.SaveChangesAsync(ct);
                logger.LogInformation("Nightly SimpleFIN sync: {Accounts} account(s), {New} new transaction(s) for a user",
                    result.Accounts.Count, result.Accounts.Sum(a => a.NewTransactions));
            }
            catch (SimpleFinException ex) when (ex.Permanent)
            {
                // No retries tonight: they can't succeed until the user
                // reconnects or renews. Tomorrow night tries once more.
                conn.LastAutoUpdateDate = today;
                await userDb.SaveChangesAsync(CancellationToken.None);
                logger.LogWarning("Nightly SimpleFIN sync stopped for a user until they act: {Message}", ex.Message);
            }
            catch (Exception ex) when (ex is SimpleFinException or SimpleFinConflictException)
            {
                // Already saved to the connection's errors for the user to see.
                logger.LogWarning("Daily SimpleFIN update failed for a user: {Message}", ex.Message);
            }
        }
    }
}
