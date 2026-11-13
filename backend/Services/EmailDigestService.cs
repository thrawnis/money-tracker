using System.Net;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// SMTP settings from configuration (Smtp:Host, Smtp:Port, Smtp:Username,
/// Smtp:Password, Smtp:From, Smtp:Security = starttls | ssl | none). Email is
/// "configured" once a host and a from-address are set.
/// Smtp:AllowInvalidCertificate=true accepts any server certificate (e.g. a
/// self-signed one on a home mail server). Off by default: without the check,
/// anyone between the app and the mail server could pose as it and read the
/// SMTP password and the emails.
/// </summary>
public class EmailSender(IConfiguration config, ILogger<EmailSender> logger)
{
    private string? Get(string key) => string.IsNullOrWhiteSpace(config[key]) ? null : config[key]!.Trim();

    public bool IsConfigured => Get("Smtp:Host") is not null && Get("Smtp:From") is not null;

    public async Task SendAsync(string to, string subject, string text, string html, CancellationToken ct = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("Email isn't set up on the server (SMTP_HOST / SMTP_FROM).");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(Get("Smtp:From")!));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { TextBody = text, HtmlBody = html }.ToMessageBody();

        var security = (Get("Smtp:Security") ?? "starttls").ToLowerInvariant() switch
        {
            "ssl" or "tls" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        var port = int.TryParse(Get("Smtp:Port"), out var p) ? p : security == SecureSocketOptions.SslOnConnect ? 465 : 587;

        using var client = new SmtpClient();
        if (config.GetValue<bool>("Smtp:AllowInvalidCertificate"))
            client.ServerCertificateValidationCallback = (_, _, _, errors) =>
            {
                if (errors != System.Net.Security.SslPolicyErrors.None)
                    logger.LogDebug("Accepting SMTP server certificate despite {Errors} (Smtp:AllowInvalidCertificate)", errors);
                return true;
            };
        await client.ConnectAsync(Get("Smtp:Host"), port, security, ct);
        if (Get("Smtp:Username") is string user)
            await client.AuthenticateAsync(user, Get("Smtp:Password") ?? "", ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
        logger.LogInformation("Sent \"{Subject}\" email", subject);
    }
}

/// <summary>
/// Builds a user's daily digest: bank-sync reviews waiting, sync problems,
/// bills due soon or overdue, low-balance forecast warnings and the count of
/// uncategorized transactions. Amounts only when the user opted in.
/// </summary>
public class EmailDigestBuilder(AppDbContext db, IEncryptionService encryption, CashFlowForecast forecast, TransferMatcher transfers, IConfiguration config)
{
    public record Digest(string Subject, string Text, string Html, bool HasNews);

    public async Task<Digest> BuildAsync(ApplicationUser user)
    {
        var today = UserClock.Today(user);
        var money = (decimal n) => user.DigestIncludeAmounts ? n.ToString("C", System.Globalization.CultureInfo.GetCultureInfo("en-US")) : null;
        var day = (DateOnly d) => d == today ? "today" : d == today.AddDays(1) ? "tomorrow" : d.ToString("ddd MMM d");
        var appUrl = (config["App:Url"] ?? config["Fido2:Origin"] ?? "").Split(',')[0].Trim().TrimEnd('/');

        var sections = new List<(string Title, List<string> Lines, string? Link)>();
        var news = false;

        // Bank sync: reviews waiting and the last sync's problems.
        var drafts = await db.ImportDrafts
            .Where(d => d.UserId == user.Id && d.SimpleFinAccountId != null && d.RowCount > 0)
            .Select(d => new { d.Account.Name, d.RowCount })
            .ToListAsync();
        var conn = await db.SimpleFinConnections.FirstOrDefaultAsync(c => c.UserId == user.Id);
        var syncErrors = new List<string>();
        if (conn?.LastErrorsEncrypted is string enc)
        {
            try { syncErrors = JsonSerializer.Deserialize<List<string>>(encryption.Decrypt(enc, user.EncryptedDataKey)!) ?? []; }
            catch { /* unreadable: leave out */ }
        }
        if (drafts.Count > 0 || syncErrors.Count > 0)
        {
            news = true;
            var lines = drafts.Select(d => $"{d.Name}: {d.RowCount} new transaction{(d.RowCount == 1 ? "" : "s")} to review").ToList();
            lines.AddRange(syncErrors.Select(e => $"Sync problem: {e}"));
            sections.Add(("Bank sync", lines, "/accounts#bank-sync"));
        }

        // Bills due in the next 3 days, or overdue.
        var bills = await db.ScheduledTransactions
            .Where(s => s.UserId == user.Id && s.IsActive && s.NextDueDate <= today.AddDays(3))
            .OrderBy(s => s.NextDueDate)
            .Select(s => new { s.Name, s.NextDueDate, s.Amount })
            .ToListAsync();
        if (bills.Count > 0)
        {
            news = true;
            sections.Add(("Bills due soon", bills.Select(b =>
                $"{b.Name} — {(b.NextDueDate < today ? "overdue since " + day(b.NextDueDate) : "due " + day(b.NextDueDate))}"
                + (money(Math.Abs(b.Amount)) is string m ? $" ({m})" : "")).ToList(), "/bills"));
        }

        // Low-balance warnings over the next 30 days.
        var warnings = (await forecast.ForUserAsync(user, 30, CashFlowForecast.WatchedTypes))
            .Where(f => f.FirstBelowThreshold is not null).ToList();
        if (warnings.Count > 0)
        {
            news = true;
            sections.Add(("Low balance ahead", warnings.Select(w =>
                $"{w.Name} may drop below {money(w.Threshold ?? 0) ?? "its warning level"} on {day(w.FirstBelowThreshold!.Value)}"
                + (money(w.LowestBalance) is string m ? $" (low of {m} on {day(w.LowestDate)})" : "")).ToList(), "/reports"));
        }

        // Uncategorized: shown, but on its own not a reason to send.
        var uncategorized = await db.Transactions.CountAsync(t => t.Account.UserId == user.Id && t.CategoryId == null
            && t.TransferTransactionId == null && !t.IsVoided && !t.Splits.Any());
        // Possible transfers (Utilities → Possible Transfers): likewise not news on its own.
        var possibleTransfers = await transfers.CountAsync(user);
        var tidyUp = new List<string>();
        if (uncategorized > 0) tidyUp.Add($"{uncategorized} uncategorized transaction{(uncategorized == 1 ? "" : "s")}");
        if (possibleTransfers > 0)
            tidyUp.Add($"{possibleTransfers} pair{(possibleTransfers == 1 ? "" : "s")} of transactions that look like transfers between your accounts (Utilities → Possible Transfers)");
        if (tidyUp.Count > 0)
            sections.Add(("To categorize", tidyUp, possibleTransfers > 0 && uncategorized == 0 ? "/utilities?tab=transfers" : "/"));

        var subject = news
            ? "Money Tracker: " + string.Join(", ", sections.Where(s => s.Title != "To categorize").Select(s => s.Title.ToLowerInvariant()))
            : "Money Tracker: nothing new today";

        var text = new StringBuilder($"Your Money Tracker summary for {today:dddd, MMMM d}\n\n");
        var html = new StringBuilder(
            "<div style=\"font-family:-apple-system,Segoe UI,Arial,sans-serif;font-size:14px;color:#222;max-width:560px\">"
            + $"<h2 style=\"color:#003366;font-size:18px\">Your Money Tracker summary for {WebUtility.HtmlEncode(today.ToString("dddd, MMMM d"))}</h2>");
        if (sections.Count == 0)
        {
            text.Append("Nothing needs your attention today.\n");
            html.Append("<p>Nothing needs your attention today.</p>");
        }
        foreach (var (title, lines, link) in sections)
        {
            text.Append(title.ToUpperInvariant()).Append('\n');
            foreach (var l in lines) text.Append("  • ").Append(l).Append('\n');
            if (appUrl.Length > 0 && link is not null) text.Append("  ").Append(appUrl).Append(link).Append('\n');
            text.Append('\n');

            html.Append($"<h3 style=\"color:#003366;font-size:15px;margin:18px 0 6px\">{WebUtility.HtmlEncode(title)}</h3><ul style=\"margin:0;padding-left:20px\">");
            foreach (var l in lines) html.Append($"<li>{WebUtility.HtmlEncode(l)}</li>");
            html.Append("</ul>");
            if (appUrl.Length > 0 && link is not null)
                html.Append($"<p style=\"margin:6px 0 0\"><a href=\"{WebUtility.HtmlEncode(appUrl + link)}\">Open in Money Tracker</a></p>");
        }
        const string footer = "You're getting this because the daily email digest is on. Turn it off in Money Tracker → Settings → Preferences.";
        text.Append(footer);
        html.Append($"<p style=\"color:#777;font-size:12px;margin-top:24px\">{footer}</p></div>");

        return new Digest(subject, text.ToString(), html.ToString(), news);
    }
}

/// <summary>
/// Sends each opted-in user's digest once a day, at their chosen hour in
/// their time zone (checked every 10 minutes, so it can be up to ~10 minutes
/// late). A quiet day is skipped when the user asked for news-only digests.
/// </summary>
public class EmailDigestService(IServiceScopeFactory scopes, ILogger<EmailDigestService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Email digest run failed"); }
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<EmailSender>();
        if (!sender.IsConfigured) return;
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var builder = scope.ServiceProvider.GetRequiredService<EmailDigestBuilder>();

        var users = await db.Users.Where(u => u.DigestEnabled && u.Email != null).ToListAsync(ct);
        foreach (var user in users)
        {
            var today = UserClock.Today(user);
            if (user.DigestLastSentOn == today) continue;
            if (UserClock.Now(user.TimeZoneId).Hour < user.DigestHour) continue;
            try
            {
                var digest = await builder.BuildAsync(user);
                if (digest.HasNews || !user.DigestOnlyWhenNews)
                    await sender.SendAsync(user.Email!, digest.Subject, digest.Text, digest.Html, ct);
                user.DigestLastSentOn = today;   // checked today either way
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Leave DigestLastSentOn alone so the next run retries.
                logger.LogWarning(ex, "Couldn't send the email digest for user {UserId}", user.Id);
            }
        }
    }
}
