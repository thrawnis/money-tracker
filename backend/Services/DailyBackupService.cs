using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// The daily backup: every user, every day, just before the nightly bank sync
/// (at <see cref="Hour"/>:<see cref="Minute"/> Pacific Time — the sync runs at
/// 8 pm), whether or not they use bank sync. The nightly sync also calls
/// <see cref="EnsureTodayAsync"/> before syncing a user, so a backup is always
/// taken before that day's sync. A failed backup is retried on the next check
/// (every 5 minutes) until it succeeds or the day ends, and its reason is kept
/// (BackupLastError) for Settings → Backups.
/// </summary>
public class DailyBackupService(IServiceScopeFactory scopes, ILogger<DailyBackupService> logger) : BackgroundService
{
    public const int Hour = 19, Minute = 45;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public static DateOnly PacificToday() => DateOnly.FromDateTime(UserClock.Now(SimpleFinDailyUpdateService.ScheduleTimeZone));

    public static bool IsDue(ApplicationUser u)
    {
        var now = UserClock.Now(SimpleFinDailyUpdateService.ScheduleTimeZone);
        return (now.Hour > Hour || (now.Hour == Hour && now.Minute >= Minute)) && u.LastDailyBackupOn != DateOnly.FromDateTime(now);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Daily backup run failed"); }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!SevenZip.Available) { logger.LogWarning("Daily backups skipped: 7-Zip isn't installed"); return; }
        List<string> ids;
        using (var scope = scopes.CreateScope())
            ids = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Select(u => u.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            using var scope = scopes.CreateScope();
            await EnsureTodayAsync(scope.ServiceProvider, id);
        }
    }

    /// <summary>Takes today's daily backup for the user if it's due and not done yet. Never throws.</summary>
    public static async Task EnsureTodayAsync(IServiceProvider services, string userId)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var log = services.GetRequiredService<ILogger<DailyBackupService>>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null || !IsDue(user) || !SevenZip.Available) return;
        try
        {
            await UserBackupService.ExclusiveAsync(userId, async () =>
            {
                await db.Entry(user).ReloadAsync();
                if (db.Entry(user).State == EntityState.Detached || !IsDue(user)) return 0;   // gone, or done meanwhile
                await services.GetRequiredService<UserBackupService>().CreateAsync(user, "daily");
                // Direct update: never lost to a concurrent change elsewhere on the user record.
                var today = PacificToday();
                await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.LastDailyBackupOn, today).SetProperty(u => u.BackupLastError, (string?)null));
                return 0;
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Daily backup failed for user {UserId}", userId);
            try
            {
                db.ChangeTracker.Clear();
                await db.Users.Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.BackupLastError, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC: {ex.Message}"));
            }
            catch (Exception inner) { log.LogError(inner, "Couldn't record the backup failure"); }
        }
    }
}
