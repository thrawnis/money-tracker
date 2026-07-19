using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// Background job that materializes scheduled transactions into real
/// transactions. Runs at startup and then every 6 hours, doing two
/// independent passes each time:
///
///  1. PostDueAsync — materializes every occurrence up to and including
///     today and advances NextDueDate past it (back-filling missed runs,
///     e.g. the app was down for a month).
///  2. CreateFutureAsync — for users who opted in
///     (ApplicationUser.AutoCreateFutureTransactions), pre-creates
///     occurrences within their configured "days ahead" window WITHOUT
///     advancing NextDueDate, so they show up as real, editable
///     transactions before they're actually due.
///
/// Both dedupe by (ScheduledTransactionId, Date) — a future occurrence
/// pre-created by pass 2 is never re-created by pass 1 once it becomes due,
/// and either pass is safe to re-run. Transfers post as a linked pair; the
/// memo ciphertext is copied directly since both records belong to the same
/// user (same DEK).
/// </summary>
public class ScheduledTransactionPostingService(
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduledTransactionPostingService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private const int DefaultAutoCreateFutureDays = 31;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PostDueAsync(stoppingToken);
                await CreateFutureAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled transaction posting failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    public async Task PostDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var due = await db.ScheduledTransactions
            .Where(s => s.IsActive && s.NextDueDate <= today)
            .ToListAsync(ct);

        foreach (var s in due)
        {
            // Post every missed occurrence (e.g., app was down for a month).
            // The guard caps runaway loops from bad data (interval <= 0 dates).
            int guard = 0;
            while (s.NextDueDate <= today && guard++ < 366)
            {
                await using var dbTx = await db.Database.BeginTransactionAsync(ct);

                // Already materialized ahead of time by CreateFutureAsync (or a
                // previous run that inserted the transaction but was
                // interrupted before advancing NextDueDate) — don't duplicate,
                // just move the cursor past it.
                var alreadyExists = await db.Transactions
                    .AnyAsync(t => t.ScheduledTransactionId == s.Id && t.Date == s.NextDueDate, ct);

                if (!alreadyExists)
                {
                    if (s.TransferAccountId.HasValue)
                        await CreateTransferPairAsync(db, s, s.NextDueDate, ct);
                    else
                        db.Transactions.Add(NewTransaction(s, s.AccountId, s.Amount, s.NextDueDate));
                }

                var next = Advance(s.NextDueDate, s);
                if (next <= s.NextDueDate) // defensive: never loop on a non-advancing date
                {
                    await dbTx.RollbackAsync(ct);
                    logger.LogWarning("Scheduled transaction {Id} has a non-advancing schedule; deactivating", s.Id);
                    s.IsActive = false;
                    await db.SaveChangesAsync(ct);
                    break;
                }

                var postedDate = s.NextDueDate;
                s.NextDueDate = next;
                await db.SaveChangesAsync(ct);
                await dbTx.CommitAsync(ct);

                if (!alreadyExists)
                {
                    await audit.LogSystemAsync("SCHEDULED_POST", "Transaction",
                        new { scheduledTransactionId = s.Id, date = postedDate, amount = s.Amount });
                }
            }
        }
    }

    /// <summary>
    /// For users who opted in, pre-creates real transactions for occurrences
    /// within their configured "days ahead" window that haven't happened yet
    /// — so they're visible and editable in the register before they're
    /// actually due. Never advances NextDueDate; PostDueAsync still owns that
    /// when the occurrence's date arrives (and will find it already exists
    /// via the dedupe check there, so it isn't posted a second time).
    /// </summary>
    public async Task CreateFutureAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var schedules = await db.ScheduledTransactions
            .Include(s => s.User)
            .Where(s => s.IsActive && s.User.AutoCreateFutureTransactions)
            .ToListAsync(ct);

        foreach (var s in schedules)
        {
            var cutoff = today.AddDays(s.User.AutoCreateFutureDays ?? DefaultAutoCreateFutureDays);
            var date = s.NextDueDate;
            var guard = 0;
            while (date <= cutoff && guard++ < 366)
            {
                // date <= today is PostDueAsync's job — only pre-create genuinely
                // future occurrences here.
                if (date > today)
                {
                    var exists = await db.Transactions
                        .AnyAsync(t => t.ScheduledTransactionId == s.Id && t.Date == date, ct);
                    if (!exists)
                    {
                        await using var dbTx = await db.Database.BeginTransactionAsync(ct);

                        if (s.TransferAccountId.HasValue)
                            await CreateTransferPairAsync(db, s, date, ct);
                        else
                            db.Transactions.Add(NewTransaction(s, s.AccountId, s.Amount, date));

                        await db.SaveChangesAsync(ct);
                        await dbTx.CommitAsync(ct);

                        await audit.LogSystemAsync("SCHEDULED_PRECREATE", "Transaction",
                            new { scheduledTransactionId = s.Id, date, amount = s.Amount });
                    }
                }
                date = Advance(date, s);
            }
        }
    }

    private static async Task CreateTransferPairAsync(AppDbContext db, ScheduledTransaction s, DateOnly date, CancellationToken ct)
    {
        var amount = Math.Abs(s.Amount);

        var debit  = NewTransaction(s, s.AccountId, -amount, date);
        var credit = NewTransaction(s, s.TransferAccountId!.Value, amount, date);
        debit.TransferAccountId  = s.TransferAccountId.Value;
        credit.TransferAccountId = s.AccountId;

        db.Transactions.Add(debit);
        db.Transactions.Add(credit);
        await db.SaveChangesAsync(ct); // get IDs, then cross-link (within caller's transaction)

        debit.TransferTransactionId  = credit.Id;
        credit.TransferTransactionId = debit.Id;
    }

    private static Transaction NewTransaction(ScheduledTransaction s, int accountId, decimal amount, DateOnly date) => new()
    {
        AccountId     = accountId,
        Date          = date,
        PayeeId       = s.PayeeId,
        CategoryId    = s.CategoryId,
        MemoEncrypted = s.MemoEncrypted, // same user → same DEK → ciphertext is reusable
        Amount        = amount,
        Status        = TransactionStatus.Uncleared,
        ScheduledTransactionId = s.Id,
        CreatedAt     = DateTime.UtcNow,
        UpdatedAt     = DateTime.UtcNow,
    };

    /// <summary>
    /// Computes the next occurrence date after <paramref name="date"/> for a
    /// schedule — shared with ScheduledTransactionsController.GetUpcoming,
    /// which projects every occurrence within the "days ahead" window rather
    /// than just the next one.
    /// </summary>
    public static DateOnly Advance(DateOnly date, ScheduledTransaction s)
    {
        // Specific-weekdays schedule (e.g. "every Mon/Wed/Fri"): find the next
        // date strictly after the current one whose weekday bit is set, rather
        // than a flat N-week jump. Bit N = (int)DayOfWeek N (Sunday=0 → bit 1).
        if (s.FrequencyUnit == FrequencyUnit.Weeks && s.DaysOfWeekMask is int mask && mask != 0)
        {
            for (var i = 1; i <= 7; i++)
            {
                var candidate = date.AddDays(i);
                if ((mask & (1 << (int)candidate.DayOfWeek)) != 0) return candidate;
            }
            return date; // unreachable when mask != 0, but keeps this total
        }

        var interval = Math.Max(1, s.FrequencyInterval);
        return s.FrequencyUnit switch
        {
            FrequencyUnit.Days   => date.AddDays(interval),
            FrequencyUnit.Weeks  => date.AddDays(7 * interval),
            FrequencyUnit.Months => date.AddMonths(interval),
            FrequencyUnit.Years  => date.AddYears(interval),
            _                    => date.AddMonths(interval),
        };
    }
}
