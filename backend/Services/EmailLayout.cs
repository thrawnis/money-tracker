using System.Globalization;
using System.Net;
using System.Text;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// Plain-text and HTML bodies for the app's emails, built from a title and
/// sections of lines, so every email looks the same. Also the shared rules for
/// what an email may show (amounts and payees only when the user allows it).
/// </summary>
public class EmailLayout(string title)
{
    private readonly List<(string Heading, List<string> Lines, string? LinkPath, string? LinkText)> sections = [];
    private readonly List<string> intro = [];

    public static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string Money(decimal n) => n.ToString("C", Us);

    /// <summary>Signed money for changes: "+$1,234.00" / "−$56.00".</summary>
    public static string Change(decimal n) => (n >= 0 ? "+" : "−") + Math.Abs(n).ToString("C", Us);

    public static string Day(DateOnly d, DateOnly today) =>
        d == today ? "today" : d == today.AddDays(-1) ? "yesterday" : d == today.AddDays(1) ? "tomorrow" : d.ToString("ddd MMM d", Us);

    /// <summary>Where links in emails point (App:Url, else the first passkey origin).</summary>
    public static string AppUrl(IConfiguration config) =>
        (config["App:Url"] ?? config["Fido2:Origin"] ?? "").Split(',')[0].Trim().TrimEnd('/');

    public EmailLayout Intro(string line) { intro.Add(line); return this; }

    public EmailLayout Section(string heading, IEnumerable<string> lines, string? linkPath = null, string? linkText = null)
    {
        var list = lines.ToList();
        if (list.Count > 0) sections.Add((heading, list, linkPath, linkText));
        return this;
    }

    public bool IsEmpty => sections.Count == 0;

    public (string Text, string Html) Render(string appUrl, string footer)
    {
        var text = new StringBuilder(title).Append("\n\n");
        var html = new StringBuilder("<div style=\"font-family:-apple-system,Segoe UI,Arial,sans-serif;font-size:14px;color:#222;max-width:600px\">")
            .Append($"<h2 style=\"color:#003366;font-size:18px\">{WebUtility.HtmlEncode(title)}</h2>");
        foreach (var l in intro)
        {
            text.Append(l).Append('\n');
            html.Append($"<p style=\"margin:0 0 8px\">{WebUtility.HtmlEncode(l)}</p>");
        }
        if (intro.Count > 0) text.Append('\n');
        foreach (var (heading, lines, link, linkText) in sections)
        {
            text.Append(heading.ToUpperInvariant()).Append('\n');
            foreach (var l in lines) text.Append("  • ").Append(l).Append('\n');
            if (appUrl.Length > 0 && link is not null) text.Append("  ").Append(appUrl).Append(link).Append('\n');
            text.Append('\n');
            html.Append($"<h3 style=\"color:#003366;font-size:15px;margin:18px 0 6px\">{WebUtility.HtmlEncode(heading)}</h3><ul style=\"margin:0;padding-left:20px\">");
            foreach (var l in lines) html.Append($"<li style=\"margin:2px 0\">{WebUtility.HtmlEncode(l)}</li>");
            html.Append("</ul>");
            if (appUrl.Length > 0 && link is not null)
                html.Append($"<p style=\"margin:6px 0 0\"><a href=\"{WebUtility.HtmlEncode(appUrl + link)}\">{WebUtility.HtmlEncode(linkText ?? "Open in Money Tracker")}</a></p>");
        }
        text.Append(footer);
        html.Append($"<p style=\"color:#777;font-size:12px;margin-top:24px\">{WebUtility.HtmlEncode(footer)}</p></div>");
        return (text.ToString(), html.ToString());
    }

    public const string SettingsFooter = "You can change which emails you get in Money Tracker → Settings → Preferences → Email notifications.";
}
