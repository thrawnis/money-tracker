using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// IOU payment pages: a private link (/pay/{token}, 30 days) that shows one
/// person what they still owe the user — date, payee and remaining amount per
/// IOU, nothing else — with one-tap ways to pay (the user's payment details).
/// </summary>
public partial class IouPaymentPages(AppDbContext db, IEncryptionService encryption, IConfiguration config)
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(30);
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Payment details ─────────────────────────────────────────────────────

    public static readonly string[] Types = ["zelle", "paypal", "venmo", "cashapp", "applecash", "googlepay", "other"];
    public record Method(string Type, string Value, string? Label);
    public record Details(string? DisplayName, List<Method> Methods);

    public Details GetDetails(ApplicationUser user)
    {
        var json = encryption.Decrypt(user.PaymentMethodsEncrypted, user.EncryptedDataKey);
        if (string.IsNullOrEmpty(json)) return new Details(null, []);
        try { return JsonSerializer.Deserialize<Details>(json, Json) ?? new Details(null, []); }
        catch (JsonException) { return new Details(null, []); }
    }

    [GeneratedRegex(@"^[A-Za-z0-9]{1,20}$")] private static partial Regex PayPalRx();
    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,30}$")] private static partial Regex VenmoRx();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{0,19}$")] private static partial Regex CashTagRx();

    private static bool IsEmailOrPhone(string v)
    {
        if (MailAddress.TryCreate(v, out var m) && m.Address == v) return true;
        var digits = new string(v.Where(char.IsDigit).ToArray());
        return digits.Length is >= 10 and <= 15 && v.All(c => char.IsDigit(c) || " +-().".Contains(c));
    }

    /// <summary>Checks and tidies the details (strips @ and $ prefixes); throws ArgumentException with a plain message.</summary>
    public static Details Normalize(Details d)
    {
        var name = d.DisplayName?.Trim();
        if (name?.Length > 60) throw new ArgumentException("Your display name is too long (60 characters at most).");
        if (d.Methods.Count > 12) throw new ArgumentException("That's too many payment methods (12 at most).");
        var methods = new List<Method>();
        foreach (var m in d.Methods)
        {
            var type = m.Type?.Trim().ToLowerInvariant() ?? "";
            var value = m.Value?.Trim() ?? "";
            var label = m.Label?.Trim();
            if (!Types.Contains(type)) throw new ArgumentException($"Unknown payment method \"{m.Type}\".");
            if (value.Length == 0) continue;   // blank rows are dropped
            switch (type)
            {
                case "paypal":
                    value = value.Replace("https://", "").Replace("paypal.me/", "", StringComparison.OrdinalIgnoreCase).Trim('/', '@');
                    if (!PayPalRx().IsMatch(value)) throw new ArgumentException("Enter your PayPal.Me name (letters and numbers), e.g. JaneDoe.");
                    break;
                case "venmo":
                    value = value.TrimStart('@');
                    if (!VenmoRx().IsMatch(value)) throw new ArgumentException("Enter your Venmo username, e.g. @Jane-Doe.");
                    break;
                case "cashapp":
                    value = value.TrimStart('$');
                    if (!CashTagRx().IsMatch(value)) throw new ArgumentException("Enter your Cash App $Cashtag, e.g. $JaneDoe.");
                    break;
                case "zelle" or "applecash" or "googlepay":
                    if (!IsEmailOrPhone(value)) throw new ArgumentException($"Enter the email address or phone number you use for {TypeName(type)}.");
                    break;
                case "other":
                    if (string.IsNullOrEmpty(label) || label.Length > 40) throw new ArgumentException("Give each \"Other\" method a name (up to 40 characters), e.g. Chime.");
                    if (value.Length > 200) throw new ArgumentException($"The details for {label} are too long (200 characters at most).");
                    if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"Links for {label} must start with https://.");
                    break;
            }
            methods.Add(new Method(type, value, type == "other" ? label : null));
        }
        return new Details(string.IsNullOrEmpty(name) ? null : name, methods);
    }

    public async Task SaveDetailsAsync(ApplicationUser user, Details d)
    {
        var clean = Normalize(d);
        user.PaymentMethodsEncrypted = clean.Methods.Count == 0 && clean.DisplayName is null
            ? null : encryption.Encrypt(JsonSerializer.Serialize(clean, Json), user.EncryptedDataKey);
        await db.SaveChangesAsync();
    }

    public static string TypeName(string type) => type switch
    {
        "zelle" => "Zelle", "paypal" => "PayPal", "venmo" => "Venmo", "cashapp" => "Cash App",
        "applecash" => "Apple Cash", "googlepay" => "Google Pay", _ => "Other",
    };

    /// <summary>A button on the page: Url to open (with the amount filled in where the service allows), or Copy to copy.</summary>
    public record PayOption(string Type, string Name, string Display, string? Url, string? Copy, string Hint);

    public static List<PayOption> Options(Details d, decimal amount, string note)
    {
        var amt = amount.ToString("0.00", CultureInfo.InvariantCulture);
        var n = Uri.EscapeDataString(note);
        return d.Methods.Select(m => m.Type switch
        {
            "paypal" => new PayOption("paypal", "PayPal", $"paypal.me/{m.Value}", $"https://paypal.me/{m.Value}/{amt}USD", null, "Opens PayPal with the amount filled in."),
            "venmo" => new PayOption("venmo", "Venmo", $"@{m.Value}", $"https://venmo.com/?txn=pay&recipients={Uri.EscapeDataString(m.Value)}&amount={amt}&note={n}", null, "Opens Venmo with the amount filled in."),
            "cashapp" => new PayOption("cashapp", "Cash App", $"${m.Value}", $"https://cash.app/${m.Value}/{amt}", null, "Opens Cash App with the amount filled in."),
            "zelle" => new PayOption("zelle", "Zelle", m.Value, null, m.Value, "Send it in your bank's app using Zelle to this email or phone."),
            "applecash" => new PayOption("applecash", "Apple Cash", m.Value, null, m.Value, "Send it with Apple Cash in Messages or Wallet to this number or email."),
            "googlepay" => new PayOption("googlepay", "Google Pay", m.Value, null, m.Value, "Send it with Google Pay / Google Wallet to this number or email."),
            _ => m.Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(m.Value, UriKind.Absolute, out _)
                ? new PayOption("other", m.Label ?? "Other", m.Value, m.Value, null, $"Pay with {m.Label}.")
                : new PayOption("other", m.Label ?? "Other", m.Value, null, m.Value, $"Pay with {m.Label} using these details."),
        }).ToList();
    }

    // ── What a person owes ─────────────────────────────────────────────────

    public record Item(DateOnly Date, string Payee, decimal Amount);
    public record Owed(List<Item> Items, decimal ItemsTotal, decimal Total);

    /// <summary>
    /// Their open IOUs (they owe the user) with what's left on each, and the
    /// total to pay: the person's balance, which is lower than the items when
    /// the user owes them something too or they've paid ahead.
    /// </summary>
    public async Task<Owed> OwedAsync(ApplicationUser user, int personId)
    {
        var dek = user.EncryptedDataKey;
        var entries = await db.IouEntries.AsNoTracking().Where(e => e.UserId == user.Id && e.PersonId == personId).ToListAsync();
        var payments = await db.IouPayments.AsNoTracking().Where(p => p.UserId == user.Id && p.PersonId == personId).ToListAsync();
        var entryIds = entries.Select(e => e.Id).ToList();
        var covered = await db.IouAllocations.Where(a => entryIds.Contains(a.EntryId))
            .GroupBy(a => a.EntryId).Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum);
        var open = entries.Where(e => e.PaidOffDate == null && e.Amount > 0)
            .Select(e => (e, left: e.Amount - covered.GetValueOrDefault(e.Id))).Where(x => x.left > 0).ToList();
        var txIds = open.Where(x => x.e.TransactionId != null).Select(x => x.e.TransactionId!.Value).ToList();
        var payees = await db.Transactions.AsNoTracking()
            .Where(t => txIds.Contains(t.Id) && t.Account.UserId == user.Id && t.Payee != null)
            .Select(t => new { t.Id, t.Payee!.NameEncrypted }).ToDictionaryAsync(t => t.Id, t => t.NameEncrypted);

        var items = open.OrderBy(x => x.e.Date).ThenBy(x => x.e.Id).Select(x => new Item(
            x.e.Date,
            (x.e.TransactionId is int t && payees.TryGetValue(t, out var pe) ? encryption.Decrypt(pe, dek) : null)
                ?? encryption.Decrypt(x.e.DescriptionEncrypted, dek) ?? "IOU",
            x.left)).ToList();
        var sum = items.Sum(i => i.Amount);
        var balance = IouLedger.Balance(entries, payments);
        return new Owed(items, sum, Math.Max(0, Math.Min(sum, balance)));
    }

    // ── Links ───────────────────────────────────────────────────────────────

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>A new 30-day link for the person; returns the token (only its hash is stored).</summary>
    public async Task<(string Token, DateTime ExpiresAt)> CreateLinkAsync(ApplicationUser user, IouPerson person)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var link = new IouShareLink
        {
            UserId = user.Id, PersonId = person.Id, TokenHash = Hash(token),
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.Add(LinkLifetime),
        };
        db.IouShareLinks.Add(link);
        await db.SaveChangesAsync();
        return (token, link.ExpiresAt);
    }

    /// <summary>The live link for a token (not expired or revoked), with its user and person; null otherwise.</summary>
    public async Task<(IouShareLink Link, ApplicationUser User, IouPerson Person)?> ResolveAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return null;
        var hash = Hash(token);
        var link = await db.IouShareLinks.Include(l => l.Person).FirstOrDefaultAsync(l => l.TokenHash == hash);
        if (link is null || link.RevokedAt != null || link.ExpiresAt <= DateTime.UtcNow) return null;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == link.UserId);
        if (user is null || link.Person.UserId != user.Id) return null;
        return (link, user, link.Person);
    }

    public string FromName(ApplicationUser user) =>
        GetDetails(user).DisplayName ?? user.Email?.Split('@')[0] ?? "Money Tracker";

    public string Url(string token, HttpRequest request)
    {
        var app = EmailLayout.AppUrl(config);
        if (app.Length == 0) app = $"{request.Scheme}://{request.Host}";
        return $"{app}/pay/{token}";
    }

    public static string Money(decimal m) => m.ToString("C", Us);

    /// <summary>
    /// The plain-text request (copied to send by text, or put into an email in the user's own email app):
    /// each open item oldest to newest with its amount, any credit, then the total.
    /// </summary>
    public static string MessageText(string fromName, string personName, Owed owed, string url, DateTime expires)
    {
        var until = expires.ToString("MMMM d, yyyy", Us);
        var credit = owed.ItemsTotal - owed.Total;
        // Rows padded into columns (date, what, amount right-aligned) — lines up in
        // fixed-width fonts and still reads cleanly in proportional ones.
        var rows = owed.Items.Select(i => (Date: i.Date.ToString("MMM d, yyyy", Us), What: Clip(i.Payee, 28), Amount: Money(i.Amount))).ToList();
        if (credit > 0.004m) rows.Add(("", "Less paid / owed to you", "−" + Money(credit)));
        var dateW = Math.Max(4, rows.Max(r => (int?)r.Date.Length) ?? 0);
        var whatW = Math.Max(5, rows.Max(r => (int?)r.What.Length) ?? 0);
        var amtW = Math.Max(Money(owed.Total).Length, rows.Max(r => (int?)r.Amount.Length) ?? 0);

        var text = new StringBuilder($"Hi {personName}, here's what you owe {fromName}:\n\n");
        foreach (var r in rows) text.Append($"{r.Date.PadRight(dateW)}  {r.What.PadRight(whatW)}  {r.Amount.PadLeft(amtW)}\n");
        // A short divider and a plain total line read cleanly in any font (padding
        // only lines up in fixed-width ones, and most phones aren't).
        text.Append("──────────\n");
        text.Append($"Total: {Money(owed.Total)}\n\n");
        // The link alone on its own line, so mail and messaging apps turn it into a link.
        text.Append($"See the details and pay here (link works until {until}):\n\n{url}\n");
        return text.ToString();
    }

    /// <summary>
    /// The same request as formatted HTML — a table and a clickable link — for
    /// pasting into an email (the page puts it on the clipboard alongside the text).
    /// </summary>
    public static string MessageHtml(string fromName, string personName, Owed owed, string url, DateTime expires)
    {
        static string e(string s) => WebUtility.HtmlEncode(s);
        var until = expires.ToString("MMMM d, yyyy", Us);
        const string td = "padding:6px 14px 6px 0;border-bottom:1px solid #e3e8ee";
        var rows = new StringBuilder();
        foreach (var i in owed.Items)
            rows.Append($"<tr><td style=\"{td};color:#555;white-space:nowrap\">{e(i.Date.ToString("MMM d, yyyy", Us))}</td>"
                + $"<td style=\"{td}\">{e(i.Payee)}</td><td style=\"{td};padding-right:0;text-align:right;white-space:nowrap\">{e(Money(i.Amount))}</td></tr>");
        var credit = owed.ItemsTotal - owed.Total;
        if (credit > 0.004m)
            rows.Append($"<tr><td style=\"{td}\"></td><td style=\"{td};color:#555\">Less what {e(fromName)} owes you or you've already paid</td>"
                + $"<td style=\"{td};padding-right:0;text-align:right;white-space:nowrap;color:#555\">−{e(Money(credit))}</td></tr>");
        rows.Append("<tr><td style=\"padding:8px 14px 0 0;font-weight:700\" colspan=\"2\">Total</td>"
            + $"<td style=\"padding:8px 0 0;text-align:right;font-weight:700;white-space:nowrap\">{e(Money(owed.Total))}</td></tr>");
        return "<div style=\"font-family:-apple-system,Segoe UI,Arial,sans-serif;font-size:15px;color:#222\">"
            + $"<p>Hi {e(personName)}, here's what you owe {e(fromName)}:</p>"
            + "<table style=\"border-collapse:collapse;font-size:14px;margin:4px 0 14px\">"
            + "<thead><tr><th style=\"text-align:left;padding:0 14px 6px 0;border-bottom:2px solid #c5d4e0;color:#336699\">Date</th>"
            + "<th style=\"text-align:left;padding:0 14px 6px 0;border-bottom:2px solid #c5d4e0;color:#336699\">What</th>"
            + "<th style=\"text-align:right;padding:0 0 6px;border-bottom:2px solid #c5d4e0;color:#336699\">Amount</th></tr></thead>"
            + $"<tbody>{rows}</tbody></table>"
            + $"<p><a href=\"{e(url)}\" style=\"color:#003366;font-weight:600\">See the details and pay here</a> (link works until {e(until)}).</p></div>";
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
