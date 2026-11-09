using System.Net;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;

namespace MoneyTracker.Services;

/// <summary>
/// "New sign-in" emails. A sign-in queues an alert here and returns at once;
/// LoginAlertService sends it in the background, so a slow or failing mail
/// server never delays or breaks signing in.
/// </summary>
public class LoginAlertQueue
{
    public record Alert(string UserId, DateTime AtUtc, string Method, string? Ip, string? UserAgent);

    private readonly Channel<Alert> channel = Channel.CreateBounded<Alert>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Enqueue(Alert alert) => channel.Writer.TryWrite(alert);
    public IAsyncEnumerable<Alert> ReadAllAsync(CancellationToken ct) => channel.Reader.ReadAllAsync(ct);
}

public class LoginAlertService(LoginAlertQueue queue, IServiceScopeFactory scopes, EmailSender sender, ILogger<LoginAlertService> logger)
    : BackgroundService
{
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
                if (user?.Email is null || !user.LoginAlertsEnabled) continue;

                var local = UserClock.ToLocalDate(alert.AtUtc, user.TimeZoneId);
                var time = LocalTime(alert.AtUtc, user.TimeZoneId);
                var how = alert.Method switch
                {
                    "passkey-only" => "a passkey (without a password)",
                    "passkey" => "your password and a passkey",
                    "totp" => "your password and an authenticator-app code",
                    _ => "your password",
                };
                var device = DescribeBrowser(alert.UserAgent);
                var appUrl = (config["App:Url"] ?? config["Fido2:Origin"] ?? "").Split(',')[0].Trim().TrimEnd('/');
                var lines = new List<(string, string)>
                {
                    ("When", $"{local:dddd, MMMM d, yyyy} at {time}"),
                    ("Signed in with", how),
                    ("Device", device),
                    ("IP address", alert.Ip ?? "unknown"),
                };
                const string advice = "If this was you, there's nothing to do. If it wasn't, change your password and remove any passkeys "
                    + "you don't recognize in Settings → Security, then reset your authenticator app if you use one.";

                var text = "Someone just signed in to your Money Tracker account.\n\n"
                    + string.Join("\n", lines.Select(l => $"{l.Item1}: {l.Item2}"))
                    + $"\n\n{advice}\n" + (appUrl.Length > 0 ? $"\n{appUrl}/settings\n" : "")
                    + "\nYou can turn these emails off in Settings → Preferences.";
                var html = "<div style=\"font-family:-apple-system,Segoe UI,Arial,sans-serif;font-size:14px;color:#222;max-width:560px\">"
                    + "<h2 style=\"color:#003366;font-size:18px\">New sign-in to your Money Tracker account</h2><table style=\"border-collapse:collapse\">"
                    + string.Concat(lines.Select(l => $"<tr><td style=\"padding:3px 12px 3px 0;color:#666\">{WebUtility.HtmlEncode(l.Item1)}</td><td style=\"padding:3px 0\">{WebUtility.HtmlEncode(l.Item2)}</td></tr>"))
                    + $"</table><p>{WebUtility.HtmlEncode(advice)}</p>"
                    + (appUrl.Length > 0 ? $"<p><a href=\"{WebUtility.HtmlEncode(appUrl + "/settings")}\">Open Settings</a></p>" : "")
                    + "<p style=\"color:#777;font-size:12px;margin-top:24px\">You can turn these emails off in Settings → Preferences.</p></div>";

                await sender.SendAsync(user.Email, "New sign-in to Money Tracker", text, html, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't send the sign-in alert for user {UserId}", alert.UserId);
            }
        }
    }

    private static string LocalTime(DateTime utc, string? tzId)
    {
        try
        {
            var tz = string.IsNullOrWhiteSpace(tzId) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(tzId);
            var t = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            return $"{t:h:mm tt} ({(string.IsNullOrWhiteSpace(tzId) ? "UTC" : tzId)})";
        }
        catch { return $"{utc:HH:mm} UTC"; }
    }

    /// <summary>"Chrome on Windows" from a User-Agent string; good enough to recognize your own device.</summary>
    public static string DescribeBrowser(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return "unknown";
        var os = ua.Contains("iPhone") ? "iPhone" : ua.Contains("iPad") ? "iPad" : ua.Contains("Android") ? "Android"
            : ua.Contains("Mac OS X") ? "Mac" : ua.Contains("Windows") ? "Windows" : ua.Contains("Linux") ? "Linux" : "an unknown system";
        var browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("Firefox/") ? "Firefox" : ua.Contains("Chrome/") || ua.Contains("CriOS/") ? "Chrome"
            : ua.Contains("Safari/") ? "Safari" : "a browser";
        return $"{browser} on {os}";
    }
}
