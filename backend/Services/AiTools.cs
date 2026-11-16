using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// The read-only lookups the AI assistant can call (OpenAI "tools"). Every one
/// works only on the given user's data and only within what the user chose to
/// share (ApplicationUser.AiShare*). Nothing here writes to the database.
/// Account numbers and account notes are never included.
/// </summary>
public class AiTools(AppDbContext db, IEncryptionService encryption)
{
    private const int MaxRows = 100;

    /// <summary>Tool definitions for the chat request, limited to what the user shares.</summary>
    public static JsonArray Definitions(ApplicationUser u)
    {
        var tools = new JsonArray
        {
            Tool("list_categories", "List the user's categories (\"Parent: Child\" names). Use to find the exact category names to search by.", new JsonObject()),
        };
        if (u.AiShareBalances)
            tools.Add(Tool("get_accounts", "List the user's accounts with type, institution and current balance (as of today; future-dated transactions excluded).", new JsonObject
            {
                ["include_closed"] = Prop("boolean", "Also include inactive/closed accounts. Default false."),
            }));
        if (u.AiShareTransactions)
        {
            tools.Add(Tool("search_transactions",
                "Find individual transactions. All filters are optional and combined with AND. Text matching is case-insensitive and partial. "
                + "Amounts are negative for money out, positive for money in. Returns the newest first, plus the count and total of ALL matches.",
                new JsonObject
                {
                    ["text"] = Prop("string", "Matches payee, category" + (u.AiShareMemos ? " or memo" : "") + "."),
                    ["payee"] = Prop("string", "Payee name contains this."),
                    ["category"] = Prop("string", "Category name contains this (a parent category also matches its subcategories)."),
                    ["account"] = Prop("string", "Account name contains this."),
                    ["from"] = Prop("string", "Earliest date, YYYY-MM-DD."),
                    ["to"] = Prop("string", "Latest date, YYYY-MM-DD."),
                    ["min_amount"] = Prop("number", "Smallest absolute amount (e.g. 50 matches -50 and 50)."),
                    ["max_amount"] = Prop("number", "Largest absolute amount."),
                    ["direction"] = Prop("string", "\"out\" (spending), \"in\" (income/deposits) or omit for both."),
                    ["include_transfers"] = Prop("boolean", "Include transfers between the user's own accounts. Default false."),
                    ["limit"] = Prop("integer", $"How many to return (default 25, max {MaxRows})."),
                }));
            tools.Add(Tool("spending_summary",
                "Totals of money in and out over a period, grouped by category, top-level category, payee, month or account. "
                + "Transfers between the user's accounts and voided transactions are excluded; split transactions count per split.",
                new JsonObject
                {
                    ["from"] = Prop("string", "Start date YYYY-MM-DD (required)."),
                    ["to"] = Prop("string", "End date YYYY-MM-DD (required)."),
                    ["group_by"] = Prop("string", "\"category\", \"top_category\", \"payee\", \"month\" or \"account\". Default \"top_category\"."),
                    ["account"] = Prop("string", "Only accounts whose name contains this."),
                    ["category"] = Prop("string", "Only categories whose name contains this."),
                }, ["from", "to"]));
        }
        if (u.AiShareBills)
            tools.Add(Tool("get_bills", "Recurring bills, reminders and scheduled transfers: name, amount, next due date, how often, account, and whether overdue.", new JsonObject
            {
                ["days_ahead"] = Prop("integer", "Only those due within this many days from today (overdue ones always included). Omit for all."),
            }));
        if (u.AiShareIous)
            tools.Add(Tool("get_ious", "Money owed between the user and other people (IOUs): each person's balance (positive = they owe the user) and the open IOUs.", new JsonObject
            {
                ["person"] = Prop("string", "Only people whose name contains this."),
            }));
        return tools;
    }

    private static JsonObject Tool(string name, string description, JsonObject props, string[]? required = null) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = new JsonArray((required ?? []).Select(r => (JsonNode)r).ToArray()),
            },
        },
    };

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    /// <summary>A short "what it's doing" line for the Ask page while a tool runs.</summary>
    public static string Describe(string name, string argumentsJson)
    {
        JsonObject a;
        try { a = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject ?? []; }
        catch (JsonException) { a = []; }
        string Range()
        {
            string? f = Str(a, "from"), t = Str(a, "to");
            static string D(string s) => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US")) : s;
            return f is not null && t is not null ? $" from {D(f)} to {D(t)}" : f is not null ? $" since {D(f)}" : t is not null ? $" up to {D(t)}" : "";
        }
        switch (name)
        {
            case "list_categories": return "Checking your categories…";
            case "get_accounts": return "Looking up your account balances…";
            case "get_bills": return "Checking your bills and reminders…";
            case "get_ious": return Str(a, "person") is string p ? $"Checking IOUs with {p}…" : "Checking your IOUs…";
            case "spending_summary":
            {
                var by = (Str(a, "group_by") ?? "top_category").Replace("top_category", "category").Replace('_', ' ');
                var only = Str(a, "category") ?? Str(a, "account");
                return $"Adding up {(only is null ? "" : only + " ")}spending by {by}{Range()}…";
            }
            case "search_transactions":
            {
                var what = new[] { Str(a, "payee"), Str(a, "text"), Str(a, "category"), Str(a, "account") }.FirstOrDefault(x => x is not null);
                return $"Searching transactions{(what is null ? "" : $" for \"{what}\"")}{Range()}…";
            }
            default: return "Looking that up…";
        }
    }

    /// <summary>Runs one tool call and returns its result as JSON text for the model.</summary>
    public async Task<string> RunAsync(ApplicationUser user, string name, string argumentsJson)
    {
        JsonObject args;
        try { args = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject ?? []; }
        catch (JsonException) { return Error("The arguments weren't valid JSON."); }

        try
        {
            object result = name switch
            {
                "list_categories" => await CategoriesAsync(user),
                "get_accounts" when user.AiShareBalances => await AccountsAsync(user, Bool(args, "include_closed")),
                "search_transactions" when user.AiShareTransactions => await SearchAsync(user, args),
                "spending_summary" when user.AiShareTransactions => await SummaryAsync(user, args),
                "get_bills" when user.AiShareBills => await BillsAsync(user, Int(args, "days_ahead")),
                "get_ious" when user.AiShareIous => await IousAsync(user, Str(args, "person")),
                _ => new { error = $"\"{name}\" isn't available. The user hasn't shared that information with the assistant." },
            };
            return JsonSerializer.Serialize(result, Json);
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });

    private static string? Str(JsonObject a, string k) =>
        a[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    private static bool Bool(JsonObject a, string k) =>
        a[k] is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && s.Equals("true", StringComparison.OrdinalIgnoreCase));
    private static decimal? Num(JsonObject a, string k) =>
        a[k] is not JsonValue v ? null
        : v.TryGetValue<decimal>(out var d) ? d
        : v.TryGetValue<string>(out var s) && decimal.TryParse(s.Replace("$", "").Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out d) ? d : null;
    private static int? Int(JsonObject a, string k) => Num(a, k) is decimal d ? (int)Math.Round(d) : null;
    private static DateOnly? Date(JsonObject a, string k) =>
        Str(a, k) is not string s ? null
        : DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
        : throw new ArgumentException($"\"{k}\" must be a date like 2026-01-31.");

    private static bool Has(string? haystack, string? needle) =>
        needle is null || (haystack ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase);

    // ── Lookups ──────────────────────────────────────────────────────────────

    private async Task<Dictionary<int, string>> CategoryLabelsAsync(ApplicationUser user)
    {
        var cats = await db.Categories.AsNoTracking().Where(c => c.UserId == user.Id).ToListAsync();
        var names = cats.ToDictionary(c => c.Id, c => encryption.Decrypt(c.NameEncrypted, user.EncryptedDataKey) ?? "");
        return cats.ToDictionary(c => c.Id,
            c => c.ParentId is int p && names.TryGetValue(p, out var parent) ? $"{parent}: {names[c.Id]}" : names[c.Id]);
    }

    private async Task<object> CategoriesAsync(ApplicationUser user) =>
        new { categories = (await CategoryLabelsAsync(user)).Values.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() };

    private async Task<object> AccountsAsync(ApplicationUser user, bool includeClosed)
    {
        var today = UserClock.Today(user);
        var accounts = await db.Accounts.AsNoTracking().Include(a => a.Institution)
            .Where(a => a.UserId == user.Id && (includeClosed || a.IsActive)).ToListAsync();
        var ids = accounts.Select(a => a.Id).ToList();
        var sums = await db.Transactions
            .Where(t => ids.Contains(t.AccountId) && !t.IsVoided && (t.PostDate ?? t.Date) <= today)
            .GroupBy(t => t.AccountId).Select(g => new { g.Key, Sum = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum);
        var reported = await ReportedBalances.ForUserAsync(db, user.Id);
        return new
        {
            as_of = today,
            accounts = accounts.OrderBy(a => a.Type).ThenBy(a => a.Name).Select(a => new
            {
                name = a.Name,
                type = a.Type.ToString(),
                institution = a.Institution?.Name,
                balance = reported.TryGetValue(a.Id, out var r) ? r.Balance : a.OpeningBalance + sums.GetValueOrDefault(a.Id),
                balance_reported_by_bank_on = reported.TryGetValue(a.Id, out var r2) ? r2.Date : (DateOnly?)null,
                active = a.IsActive,
            }).ToList(),
        };
    }

    private record Row(DateOnly Date, string Account, string? Payee, string? Category, string? Memo, decimal Amount,
        bool IsTransfer, string? TransferAccount, string Status, List<(string? Category, string? Memo, decimal Amount)>? Splits);

    /// <summary>The user's non-voided transactions in the date range, decrypted.</summary>
    private async Task<List<Row>> LoadAsync(ApplicationUser user, DateOnly? from, DateOnly? to)
    {
        var dek = user.EncryptedDataKey;
        var q = db.Transactions.AsNoTracking().Where(t => t.Account.UserId == user.Id && !t.IsVoided);
        if (from is DateOnly f) q = q.Where(t => t.Date >= f);
        if (to is DateOnly e) q = q.Where(t => t.Date <= e);
        var raw = await q.Select(t => new
        {
            t.Date, t.Amount, t.CategoryId, t.Status, t.TransferAccountId, t.MemoEncrypted,
            AccountName = t.Account.Name,
            PayeeName = t.Payee == null ? null : t.Payee.NameEncrypted,
            Splits = t.Splits.Select(s => new { s.CategoryId, s.Amount, s.MemoEncrypted }).ToList(),
        }).ToListAsync();
        var cats = await CategoryLabelsAsync(user);
        var accountNames = await db.Accounts.Where(a => a.UserId == user.Id).ToDictionaryAsync(a => a.Id, a => a.Name);
        string? Cat(int? id) => id is int i ? cats.GetValueOrDefault(i) : null;
        string? Memo(string? enc) => user.AiShareMemos ? encryption.Decrypt(enc, dek) : null;
        return raw.Select(t => new Row(
            t.Date, t.AccountName,
            t.PayeeName is null ? null : encryption.Decrypt(t.PayeeName, dek),
            t.TransferAccountId != null ? "Transfer" : t.Splits.Count > 0 ? "Split" : Cat(t.CategoryId),
            Memo(t.MemoEncrypted), t.Amount,
            t.TransferAccountId != null,
            t.TransferAccountId is int ta ? accountNames.GetValueOrDefault(ta) : null,
            t.Status.ToString(),
            t.Splits.Count > 0 ? t.Splits.Select(s => (Cat(s.CategoryId), Memo(s.MemoEncrypted), s.Amount)).ToList() : null))
            .ToList();
    }

    private async Task<object> SearchAsync(ApplicationUser user, JsonObject a)
    {
        var from = Date(a, "from"); var to = Date(a, "to");
        var text = Str(a, "text"); var payee = Str(a, "payee"); var category = Str(a, "category"); var account = Str(a, "account");
        var min = Num(a, "min_amount") is decimal mn ? Math.Abs(mn) : (decimal?)null;
        var max = Num(a, "max_amount") is decimal mx ? Math.Abs(mx) : (decimal?)null;
        var direction = Str(a, "direction")?.ToLowerInvariant();
        var transfers = Bool(a, "include_transfers");
        var limit = Math.Clamp(Int(a, "limit") ?? 25, 1, MaxRows);

        bool CatMatch(Row r, string c) => Has(r.Category, c) || (r.Splits?.Any(s => Has(s.Category, c)) ?? false);
        var matches = (await LoadAsync(user, from, to))
            .Where(r => transfers || !r.IsTransfer)
            .Where(r => Has(r.Account, account) && (payee is null || Has(r.Payee, payee)))
            .Where(r => category is null || CatMatch(r, category))
            .Where(r => text is null || Has(r.Payee, text) || CatMatch(r, text) || Has(r.Memo, text)
                || (r.Splits?.Any(s => Has(s.Memo, text)) ?? false) || Has(r.TransferAccount, text))
            .Where(r => (min is null || Math.Abs(r.Amount) >= min) && (max is null || Math.Abs(r.Amount) <= max))
            .Where(r => direction switch { "out" => r.Amount < 0, "in" => r.Amount > 0, _ => true })
            .OrderByDescending(r => r.Date)
            .ToList();

        return new
        {
            match_count = matches.Count,
            total_amount = matches.Sum(r => r.Amount),
            returned = Math.Min(limit, matches.Count),
            transactions = matches.Take(limit).Select(r => new
            {
                date = r.Date, account = r.Account, payee = r.Payee,
                category = r.Category,
                transfer_with = r.TransferAccount,
                memo = r.Memo, amount = r.Amount, status = r.Status,
                splits = r.Splits?.Select(s => new { category = s.Category, memo = s.Memo, amount = s.Amount }),
            }),
        };
    }

    private async Task<object> SummaryAsync(ApplicationUser user, JsonObject a)
    {
        var from = Date(a, "from") ?? throw new ArgumentException("\"from\" is required.");
        var to = Date(a, "to") ?? throw new ArgumentException("\"to\" is required.");
        if (to < from) throw new ArgumentException("\"to\" is before \"from\".");
        var groupBy = (Str(a, "group_by") ?? "top_category").ToLowerInvariant();
        var account = Str(a, "account"); var category = Str(a, "category");

        // One line per transaction, or per split.
        var lines = (await LoadAsync(user, from, to))
            .Where(r => !r.IsTransfer && Has(r.Account, account))
            .SelectMany(r => r.Splits is { } sp
                ? sp.Select(s => (r.Date, r.Account, r.Payee, Category: s.Category ?? "Uncategorized", s.Amount))
                : [(r.Date, r.Account, r.Payee, Category: r.Category ?? "Uncategorized", r.Amount)])
            .Where(l => Has(l.Category, category))
            .ToList();

        string Key((DateOnly Date, string Account, string? Payee, string Category, decimal Amount) l) => groupBy switch
        {
            "category" => l.Category,
            "payee" => l.Payee ?? "(no payee)",
            "month" => l.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            "account" => l.Account,
            _ => l.Category.Split(':')[0].Trim(),
        };
        var groups = lines.GroupBy(Key).Select(g => new
        {
            name = g.Key,
            spent = -g.Where(l => l.Amount < 0).Sum(l => l.Amount),
            received = g.Where(l => l.Amount > 0).Sum(l => l.Amount),
            net = g.Sum(l => l.Amount),
            count = g.Count(),
        });
        groups = groupBy == "month" ? groups.OrderBy(g => g.name) : groups.OrderByDescending(g => g.spent).ThenByDescending(g => g.received);
        return new
        {
            from, to, group_by = groupBy,
            total_spent = -lines.Where(l => l.Amount < 0).Sum(l => l.Amount),
            total_received = lines.Where(l => l.Amount > 0).Sum(l => l.Amount),
            groups = groups.Take(60).ToList(),
        };
    }

    private async Task<object> BillsAsync(ApplicationUser user, int? daysAhead)
    {
        var dek = user.EncryptedDataKey;
        var today = UserClock.Today(user);
        var q = db.ScheduledTransactions.AsNoTracking().Where(s => s.UserId == user.Id && s.IsActive);
        if (daysAhead is int d) { var end = today.AddDays(Math.Max(0, d)); q = q.Where(s => s.NextDueDate <= end); }
        var bills = await q.OrderBy(s => s.NextDueDate).Select(s => new
        {
            s.Name, s.Amount, s.NextDueDate, s.FrequencyInterval, s.FrequencyUnit, s.CategoryId, s.TransferAccountId,
            Account = s.Account.Name, Payee = s.Payee == null ? null : s.Payee.NameEncrypted,
        }).ToListAsync();
        var cats = await CategoryLabelsAsync(user);
        var accounts = await db.Accounts.Where(x => x.UserId == user.Id).ToDictionaryAsync(x => x.Id, x => x.Name);
        return new
        {
            today,
            bills = bills.Select(b => new
            {
                name = b.Name,
                payee = b.Payee is null ? null : encryption.Decrypt(b.Payee, dek),
                amount = b.Amount,
                account = b.Account,
                transfer_to = b.TransferAccountId is int t ? accounts.GetValueOrDefault(t) : null,
                category = b.CategoryId is int c ? cats.GetValueOrDefault(c) : null,
                next_due = b.NextDueDate,
                overdue = b.NextDueDate < today,
                repeats = $"every {b.FrequencyInterval} {b.FrequencyUnit.ToString().ToLowerInvariant()}",
            }).ToList(),
        };
    }

    private async Task<object> IousAsync(ApplicationUser user, string? person)
    {
        var dek = user.EncryptedDataKey;
        var people = await db.IouPeople.AsNoTracking().Where(p => p.UserId == user.Id).ToListAsync();
        var entries = await db.IouEntries.AsNoTracking().Where(e => e.UserId == user.Id).ToListAsync();
        var payments = await db.IouPayments.AsNoTracking().Where(p => p.UserId == user.Id).ToListAsync();
        return new
        {
            note = "balance > 0: they owe the user; < 0: the user owes them.",
            people = people
                .Select(p => (p, name: encryption.Decrypt(p.NameEncrypted, dek) ?? ""))
                .Where(x => Has(x.name, person))
                .Select(x => new
                {
                    name = x.name,
                    balance = IouLedger.Balance(entries.Where(e => e.PersonId == x.p.Id), payments.Where(y => y.PersonId == x.p.Id)),
                    open_ious = entries.Where(e => e.PersonId == x.p.Id && e.PaidOffDate == null).OrderBy(e => e.Date)
                        .Select(e => new { date = e.Date, amount = e.Amount, description = encryption.Decrypt(e.DescriptionEncrypted, dek) }).ToList(),
                    recent_payments = payments.Where(y => y.PersonId == x.p.Id).OrderByDescending(y => y.Date).Take(5)
                        .Select(y => new { date = y.Date, amount = y.Amount, direction = y.Direction.ToString() }).ToList(),
                })
                .OrderBy(x => x.name).ToList(),
        };
    }
}
