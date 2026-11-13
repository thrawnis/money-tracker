using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;

namespace MoneyTracker.Services;

/// <summary>
/// Bank-sync emails: a sync queues what it found here and carries on;
/// BankSyncAlertService sends one email per sync, so a slow or failing mail
/// server never delays a sync.
/// </summary>
public class BankSyncAlertQueue
{
    public record Alert(string UserId, SyncTrigger Trigger, List<SimpleFinSyncAccountResult> Accounts);

    private readonly Channel<Alert> channel = Channel.CreateBounded<Alert>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Enqueue(Alert alert) => channel.Writer.TryWrite(alert);
    public IAsyncEnumerable<Alert> ReadAllAsync(CancellationToken ct) => channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// One email per sync, with up to two sections:
/// <list type="bullet">
/// <item>Large transactions (if on): any sync's newly found transactions whose
/// size, in or out, is at least the user's threshold.</item>
/// <item>New transactions (if on): nightly syncs only — a manual sync's results
/// are already on screen.</item>
/// </list>
/// Amounts and payees appear only when the user allows details in emails.
/// </summary>
public class BankSyncAlertService(BankSyncAlertQueue queue, IServiceScopeFactory scopes, EmailSender sender, ILogger<BankSyncAlertService> logger)
    : BackgroundService
{
    private const int MaxListed = 15;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var alert in queue.ReadAllAsync(stoppingToken))
        {
            if (!sender.IsConfigured) continue;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == alert.UserId, stoppingToken);
                if (user?.Email is null) continue;
                if (Build(user, alert, EmailLayout.AppUrl(config)) is { } email)
                    await sender.SendAsync(user.Email, email.Subject, email.Text, email.Html, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't send the bank sync alert for user {UserId}", alert.UserId);
            }
        }
    }

    public static (string Subject, string Text, string Html)? Build(Models.ApplicationUser user, BankSyncAlertQueue.Alert alert, string appUrl)
    {
        var today = UserClock.Today(user);
        var details = user.DigestIncludeAmounts;
        string Row(SyncArrival a) => $"{EmailLayout.Day(a.Date, today)} — {(string.IsNullOrWhiteSpace(a.Payee) ? "(no payee)" : a.Payee)} — {EmailLayout.Money(a.Amount)}";

        var layout = new EmailLayout("Bank sync update");
        var subjectParts = new List<string>();

        if (user.LargeTxAlertsEnabled && user.LargeTxThreshold > 0)
        {
            var large = alert.Accounts
                .SelectMany(acc => acc.Arrived.Where(a => Math.Abs(a.Amount) >= user.LargeTxThreshold).Select(a => (acc, a)))
                .OrderByDescending(x => Math.Abs(x.a.Amount))
                .ToList();
            if (large.Count > 0)
            {
                var limit = EmailLayout.Money(user.LargeTxThreshold);
                layout.Section($"Transactions of {limit} or more", large.Select(x =>
                    $"{x.acc.LinkedAccountName}: " + (details
                        ? Row(x.a)
                        : $"{(x.a.Amount < 0 ? "money out" : "money in")} of {limit} or more, dated {x.a.Date.ToString("ddd MMM d", EmailLayout.Us)}")),
                    "/accounts#bank-sync", "Review under Accounts → Bank Sync");
                subjectParts.Add(large.Count == 1 ? "a large transaction" : $"{large.Count} large transactions");
            }
        }

        if (user.SyncAlertsEnabled && alert.Trigger == SyncTrigger.Nightly)
        {
            // One section per account: the transactions when details are allowed,
            // otherwise just how many.
            foreach (var acc in alert.Accounts.OrderBy(a => a.LinkedAccountName))
            {
                var n = acc.Arrived.Count;
                var heading = $"New in {acc.LinkedAccountName} ({n})";
                var lines = details
                    ? acc.Arrived.Take(MaxListed).Select(Row).Concat(n > MaxListed ? [$"…and {n - MaxListed} more"] : []).ToList()
                    : [$"{n} new transaction{(n == 1 ? "" : "s")} from bank sync"];
                layout.Section(heading, lines, "/accounts#bank-sync", "Review under Accounts → Bank Sync");
            }
            var total = alert.Accounts.Sum(a => a.Arrived.Count);
            subjectParts.Add($"{total} new transaction{(total == 1 ? "" : "s")}");
        }

        if (layout.IsEmpty) return null;
        layout.Intro("New transactions wait under Accounts → Bank Sync for your review before they reach your registers.");
        var (text, html) = layout.Render(appUrl, EmailLayout.SettingsFooter);
        return ("Money Tracker: " + string.Join(", ", subjectParts), text, html);
    }
}
