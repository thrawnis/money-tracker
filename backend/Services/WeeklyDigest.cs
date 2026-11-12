using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// The weekly email: where your money stands and what changed. Net worth and
/// its change over the week, the week's spending and income against a usual
/// week (the average of the 12 weeks before), the categories that moved most,
/// the month's pace, the biggest transactions, what's coming up (bills, low
/// balances) and anything waiting for you (bank-sync reviews, uncategorized).
/// Amounts and payees only when the user allows details in emails; otherwise
/// changes are given as percentages.
/// </summary>
public class WeeklyDigestBuilder(
    AppDbContext db, ReportData reports, CashFlowForecast forecast, TransferMatcher transfers, IEncryptionService encryption, IConfiguration config)
{
    private const int BaselineWeeks = 12;

    public async Task<(string Subject, string Text, string Html)> BuildAsync(ApplicationUser user)
    {
        var today = UserClock.Today(user);
        var weekStart = today.AddDays(-6);
        var details = user.DigestIncludeAmounts;
        string Pct(decimal now, decimal before) =>
            before == 0 ? "" : $"{(now >= before ? "+" : "−")}{Math.Abs(Math.Round((now - before) / Math.Abs(before) * 100))}%";

        var layout = new EmailLayout($"Your week in money: {weekStart.ToString("MMM d", EmailLayout.Us)} – {today.ToString("MMM d", EmailLayout.Us)}");

        // ── Net worth ──
        var inputs = await reports.BalanceInputsAsync(user);
        if (inputs.Count > 0)
        {
            var hist = BalanceHistory.Build(inputs, today.AddDays(-7), today);
            var now = hist.NetWorth[^1];
            var then = hist.NetWorth[0];
            var change = now - then;
            var lines = new List<string>();
            if (details)
            {
                lines.Add($"Net worth {EmailLayout.Money(now)} ({EmailLayout.Change(change)} this week)");
                lines.Add($"You own {EmailLayout.Money(hist.Assets[^1])} and owe {EmailLayout.Money(Math.Abs(hist.Debts[^1]))}");
            }
            else if (change == 0) lines.Add("Net worth unchanged this week");
            else
            {
                // Net worth moves by small fractions, so one decimal place.
                var pct = then != 0 ? Math.Abs(change / then * 100) : 0;
                lines.Add($"Net worth {(change > 0 ? "up" : "down")} "
                    + (then == 0 ? "" : pct < 0.1m ? "slightly " : $"{Math.Round(pct, 1)}% ") + "this week");
            }
            layout.Section("Where you stand", lines, "/reports?report=balances", "See balances over time");
        }

        // ── This week's spending vs a usual week ──
        var (week, names) = await reports.SpendingByCategoryAsync(user, weekStart, today);
        var (baseline, _) = await reports.SpendingByCategoryAsync(user, weekStart.AddDays(-7 * BaselineWeeks), weekStart.AddDays(-1));
        decimal Spent(Dictionary<int, decimal> d) => d.Values.Where(v => v > 0).Sum();
        decimal Income(Dictionary<int, decimal> d) => -d.Values.Where(v => v < 0).Sum();
        var spentWeek = Spent(week);
        var usualWeek = Math.Round(Spent(baseline) / BaselineWeeks, 2);
        var incomeWeek = Income(week);
        var spendingLines = new List<string>
        {
            (details ? $"Spent {EmailLayout.Money(spentWeek)} this week" : "Spending this week") + (usualWeek > 0
                ? $": {Pct(spentWeek, usualWeek)} vs your usual week" + (details ? $" ({EmailLayout.Money(usualWeek)})" : "")
                : ""),
        };
        if (incomeWeek > 0 && details) spendingLines.Add($"Money in: {EmailLayout.Money(incomeWeek)}");
        // Where this week's money went: the top categories, each against its usual
        // week. (Comparing categories that weren't spent this week would mostly
        // flag monthly bills, like rent, for not being due.)
        var top = week.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Take(4).ToList();
        foreach (var (k, now) in top)
        {
            var usual = Math.Max(0, baseline.GetValueOrDefault(k) / BaselineWeeks);
            spendingLines.Add($"{names.GetValueOrDefault(k, "?")}: " + (details ? $"{EmailLayout.Money(now)} " : "") + (usual > 0
                ? $"{Pct(now, usual)} vs usual" + (details ? $" ({EmailLayout.Money(Math.Round(usual, 2))})" : "")
                : "not usually spent in a week"));
        }
        layout.Section("This week's spending", spendingLines, "/reports?report=trends", "See spending trends");

        // ── Month so far ──
        var trends = await reports.SpendingTrendsAsync(user, 6);
        if (trends.TotalExpectedSoFar > 0)
        {
            var lines = new List<string>
            {
                $"{today.ToString("MMMM", EmailLayout.Us)} so far: " + (details ? $"{EmailLayout.Money(trends.TotalThisMonth)} spent, " : "spending ")
                + $"{Pct(trends.TotalThisMonth, trends.TotalExpectedSoFar)} vs your usual pace by this day",
            };
            lines.AddRange(trends.Categories
                .Where(c => c.AheadOfPace > 25 && c.ExpectedSoFar > 0)
                .Take(3)
                .Select(c => $"{c.Name} is running ahead: " + (details
                    ? $"{EmailLayout.Money(c.ThisMonth)} vs {EmailLayout.Money(c.ExpectedSoFar)} usually by now ({Pct(c.ThisMonth, c.ExpectedSoFar)})"
                    : $"{Pct(c.ThisMonth, c.ExpectedSoFar)} vs usual by now")));
            layout.Section("Month to date", lines);
        }

        // ── Biggest transactions this week (details only: they're amounts and payees) ──
        if (details)
        {
            var biggest = await db.Transactions
                .Where(t => t.Account.UserId == user.Id && !t.IsVoided && t.TransferTransactionId == null
                    && t.Date >= weekStart && t.Date <= today && t.Amount < 0)
                .OrderBy(t => t.Amount).Take(3)
                .Select(t => new { t.Date, t.Amount, t.Payee, Account = t.Account.Name })
                .ToListAsync();
            layout.Section("Biggest purchases", biggest.Select(b =>
                $"{EmailLayout.Day(b.Date, today)} — {(b.Payee is null ? "(no payee)" : encryption.Decrypt(b.Payee.NameEncrypted, user.EncryptedDataKey))}"
                + $" — {EmailLayout.Money(b.Amount)} ({b.Account})"));
        }

        // ── Coming up ──
        var bills = await db.ScheduledTransactions
            .Where(s => s.UserId == user.Id && s.IsActive && s.NextDueDate <= today.AddDays(7))
            .OrderBy(s => s.NextDueDate)
            .Select(s => new { s.Name, s.NextDueDate, s.Amount })
            .ToListAsync();
        var coming = bills.Select(b => $"{b.Name} — {(b.NextDueDate < today ? "overdue" : "due " + EmailLayout.Day(b.NextDueDate, today))}"
            + (details ? $" ({EmailLayout.Money(Math.Abs(b.Amount))})" : "")).ToList();
        var warnings = (await forecast.ForUserAsync(user, 30, CashFlowForecast.WatchedTypes)).Where(f => f.FirstBelowThreshold is not null);
        coming.AddRange(warnings.Select(w => $"{w.Name} may drop below its warning level on {EmailLayout.Day(w.FirstBelowThreshold!.Value, today)}"));
        layout.Section("Coming up", coming, "/bills", "See bills & reminders");

        // ── Waiting for you ──
        var reviews = await db.ImportDrafts.Where(d => d.UserId == user.Id && d.SimpleFinAccountId != null && d.RowCount > 0).SumAsync(d => d.RowCount);
        var uncategorized = await db.Transactions.CountAsync(t => t.Account.UserId == user.Id && t.CategoryId == null
            && t.TransferTransactionId == null && !t.IsVoided && !t.Splits.Any());
        var todo = new List<string>();
        if (reviews > 0) todo.Add($"{reviews} bank-sync transaction{(reviews == 1 ? "" : "s")} to review");
        if (uncategorized > 0) todo.Add($"{uncategorized} uncategorized transaction{(uncategorized == 1 ? "" : "s")}");
        var possibleTransfers = await transfers.CountAsync(user);
        if (possibleTransfers > 0) todo.Add($"{possibleTransfers} possible transfer{(possibleTransfers == 1 ? "" : "s")} to link (Utilities → Possible Transfers)");
        layout.Section("Waiting for you", todo, "/", "Open Money Tracker");

        if (layout.IsEmpty) layout.Intro("No activity to report this week.");
        var (text, html) = layout.Render(EmailLayout.AppUrl(config), EmailLayout.SettingsFooter);
        var vsUsual = usualWeek > 0 ? $"{Pct(spentWeek, usualWeek)} vs a usual week" : null;
        var subject = "Money Tracker weekly: " + (details
            ? $"{EmailLayout.Money(spentWeek)} spent" + (vsUsual is null ? "" : $" ({vsUsual})")
            : vsUsual is null ? "your week in money" : $"spending {vsUsual}");
        return (subject, text, html);
    }
}

/// <summary>
/// Sends the weekly digest to opted-in users on their chosen day, at their
/// chosen hour in their time zone (checked every 10 minutes).
/// </summary>
public class WeeklyDigestService(IServiceScopeFactory scopes, ILogger<WeeklyDigestService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(40), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Weekly digest run failed"); }
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<EmailSender>();
        if (!sender.IsConfigured) return;
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var builder = scope.ServiceProvider.GetRequiredService<WeeklyDigestBuilder>();

        foreach (var user in await db.Users.Where(u => u.WeeklyDigestEnabled && u.Email != null).ToListAsync(ct))
        {
            var now = UserClock.Now(user.TimeZoneId);
            var today = DateOnly.FromDateTime(now);
            if ((int)now.DayOfWeek != user.WeeklyDigestDay || now.Hour < user.WeeklyDigestHour || user.WeeklyDigestLastSentOn == today) continue;
            try
            {
                var (subject, text, html) = await builder.BuildAsync(user);
                await sender.SendAsync(user.Email!, subject, text, html, ct);
                user.WeeklyDigestLastSentOn = today;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not marked sent, so the next check retries (until the day ends).
                logger.LogWarning(ex, "Couldn't send the weekly digest for user {UserId}", user.Id);
            }
        }
    }
}
