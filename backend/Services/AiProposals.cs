using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// Changes the AI assistant can propose (with ApplicationUser.AiAllowProposals):
/// set a category on transactions, link transfers, rename / merge payees, add a
/// payee mapping rule, create a bill. The propose_* tools only look things up
/// and describe the change — nothing is written until the user clicks Apply,
/// and ApplyAsync re-checks everything (ownership, state) at that moment.
/// Proposals made while answering are collected in Pending and saved with the
/// answer (AiController).
/// </summary>
public class AiProposals(
    AppDbContext db,
    IEncryptionService encryption,
    TransferMatcher transfers,
    DuplicateNameMergeService merger,
    ScheduledTransactionPostingService posting,
    IAuditService audit)
{
    private const int MaxItems = 500;

    // ── Payloads (stored encrypted as JSON) ─────────────────────────────────
    public record PayeeRef(int Id, string Name);
    public record TxItem(int Id, DateOnly Date, string Account, string? Payee, decimal Amount, string? Current);
    public record SetCategoryPayload(int CategoryId, string CategoryLabel, List<TxItem> Items, List<PayeeRef> MakeDefault);
    public record LinkItem(int OutId, int InId, DateOnly OutDate, DateOnly InDate, string FromAccount, string ToAccount, decimal Amount);
    public record LinkPayload(List<LinkItem> Items);
    public record RenamePayload(int PayeeId, string From, string To);
    public record MergePayload(int KeepId, string KeepName, List<PayeeRef> Merge);
    public record RulePayload(string Pattern, int PayeeId, string PayeeName);
    public record BillPayload(string Name, int AccountId, string AccountName, int? PayeeId, string? PayeeName, bool NewPayee,
        int? CategoryId, string? CategoryLabel, decimal Amount, int Interval, FrequencyUnit Unit, DateOnly NextDue);

    public record Pending(string Kind, string PayloadJson);

    /// <summary>Proposals made while answering the current question.</summary>
    public List<Pending> Proposed { get; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Tool definitions ────────────────────────────────────────────────────

    public static readonly string[] ToolNames =
        ["propose_set_category", "propose_link_transfers", "propose_rename_payee", "propose_merge_payees", "propose_mapping_rule", "propose_bill"];

    public static IEnumerable<JsonObject> Definitions(ApplicationUser u)
    {
        if (!u.AiAllowProposals) yield break;
        const string note = " Creates a proposal the user reviews and applies; nothing changes until they do.";
        if (u.AiShareTransactions)
        {
            yield return Tool("propose_set_category",
                "Propose setting one category on the transactions matching the filters (same filters as search_transactions). "
                + "Transfers, split and voided transactions are never included." + note,
                new JsonObject
                {
                    ["category"] = P("string", "The category to set, exactly as list_categories shows it (\"Parent: Child\")."),
                    ["payee"] = P("string", "Payee name contains this."),
                    ["text"] = P("string", "Payee, category or memo contains this."),
                    ["account"] = P("string", "Account name contains this."),
                    ["from"] = P("string", "Earliest date YYYY-MM-DD."),
                    ["to"] = P("string", "Latest date YYYY-MM-DD."),
                    ["min_amount"] = P("number", "Smallest absolute amount."),
                    ["max_amount"] = P("number", "Largest absolute amount."),
                    ["only_uncategorized"] = P("boolean", "Only transactions with no category (default true)."),
                    ["make_payee_default"] = P("boolean", "Also make it the default category of those transactions' payees (default false)."),
                }, ["category"]);
            yield return Tool("propose_link_transfers",
                "Propose linking pairs of separate transactions that look like the two sides of a transfer between the user's accounts "
                + "(money out of one, the same amount into another within a few days). Only unambiguous pairs are included." + note,
                new JsonObject
                {
                    ["account"] = P("string", "Only pairs where either account name contains this."),
                    ["from"] = P("string", "Earliest date YYYY-MM-DD."),
                    ["to"] = P("string", "Latest date YYYY-MM-DD."),
                    ["min_amount"] = P("number", "Smallest amount."),
                    ["max_amount"] = P("number", "Largest amount."),
                });
        }
        yield return Tool("propose_rename_payee", "Propose renaming a payee." + note, new JsonObject
        {
            ["payee"] = P("string", "The payee's current name, exactly."),
            ["new_name"] = P("string", "The new name."),
        }, ["payee", "new_name"]);
        yield return Tool("propose_merge_payees",
            "Propose merging payees into one: their transactions, bills and rules move to the kept payee, the others are removed, "
            + "and their names become mapping rules for the kept payee." + note, new JsonObject
        {
            ["keep"] = P("string", "The payee to keep, exactly as named."),
            ["merge"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Payees to merge into it, exactly as named." },
        }, ["keep", "merge"]);
        yield return Tool("propose_mapping_rule",
            "Propose a payee mapping rule: imported/synced transactions whose payee text matches the pattern get this payee. "
            + "Pattern is case-insensitive; * matches anything (e.g. \"AMZN*\")." + note, new JsonObject
        {
            ["pattern"] = P("string", "Text to match, e.g. \"AMZN MKTP*\"."),
            ["payee"] = P("string", "The existing payee to assign, exactly as named."),
        }, ["pattern", "payee"]);
        yield return Tool("propose_bill", "Propose a recurring bill or reminder (Bills & Reminders)." + note, new JsonObject
        {
            ["name"] = P("string", "Bill name, e.g. \"Netflix\"."),
            ["account"] = P("string", "The account it's paid from (or paid into, for income), exactly as named."),
            ["payee"] = P("string", "Payee name (an existing payee, or a new one)."),
            ["category"] = P("string", "Category, exactly as list_categories shows it."),
            ["amount"] = P("number", "Negative for money out (a bill), positive for income."),
            ["every"] = P("integer", "Repeats every N units (default 1)."),
            ["unit"] = P("string", "\"days\", \"weeks\", \"months\" (default) or \"years\"."),
            ["next_due"] = P("string", "Next due date YYYY-MM-DD."),
        }, ["name", "account", "amount", "next_due"]);
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
    private static JsonObject P(string type, string description) => new() { ["type"] = type, ["description"] = description };

    // ── Argument helpers ────────────────────────────────────────────────────

    private static string? Str(JsonObject a, string k) =>
        a[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    private static bool? Bool(JsonObject a, string k) =>
        a[k] is JsonValue v ? (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) ? s.Equals("true", StringComparison.OrdinalIgnoreCase) : null) : null;
    private static decimal? Num(JsonObject a, string k) =>
        a[k] is not JsonValue v ? null
        : v.TryGetValue<decimal>(out var d) ? d
        : v.TryGetValue<string>(out var s) && decimal.TryParse(s.Replace("$", "").Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out d) ? d : null;
    private static DateOnly? Date(JsonObject a, string k) =>
        Str(a, k) is not string s ? null
        : DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
        : throw new ArgumentException($"\"{k}\" must be a date like 2026-01-31.");
    private static bool Has(string? hay, string? needle) => needle is null || (hay ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase);
    private static string Money(decimal m) => m.ToString("C", CultureInfo.GetCultureInfo("en-US"));
    private static string Day(DateOnly d) => d.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));

    private async Task<Dictionary<int, string>> CategoryLabelsAsync(ApplicationUser user)
    {
        var cats = await db.Categories.AsNoTracking().Where(c => c.UserId == user.Id).ToListAsync();
        var names = cats.ToDictionary(c => c.Id, c => encryption.Decrypt(c.NameEncrypted, user.EncryptedDataKey) ?? "");
        return cats.ToDictionary(c => c.Id,
            c => c.ParentId is int p && names.TryGetValue(p, out var parent) ? $"{parent}: {names[c.Id]}" : names[c.Id]);
    }

    private async Task<(int Id, string Label)> FindCategoryAsync(ApplicationUser user, string name)
    {
        var labels = await CategoryLabelsAsync(user);
        string Norm(string s) => string.Join(":", s.Split(':').Select(x => x.Trim())).ToLowerInvariant();
        var exact = labels.Where(l => Norm(l.Value) == Norm(name)).ToList();
        if (exact.Count == 0)   // a subcategory's own name, if that's unique
            exact = labels.Where(l => Norm(l.Value).Split(':').Last() == Norm(name)).ToList();
        if (exact.Count == 1) return (exact[0].Key, exact[0].Value);
        var close = labels.Values.Where(v => Has(v, name.Split(':').Last().Trim())).Take(8).ToList();
        throw new ArgumentException(exact.Count > 1
            ? $"\"{name}\" matches several categories: {string.Join(", ", exact.Select(e => e.Value))}. Use the full \"Parent: Child\" name."
            : $"There's no category \"{name}\"." + (close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : " Use list_categories."));
    }

    private async Task<List<PayeeRef>> PayeesAsync(ApplicationUser user) =>
        (await db.Payees.AsNoTracking().Where(p => p.UserId == user.Id).Select(p => new { p.Id, p.NameEncrypted }).ToListAsync())
        .Select(p => new PayeeRef(p.Id, encryption.Decrypt(p.NameEncrypted, user.EncryptedDataKey) ?? "")).ToList();

    private static PayeeRef FindPayee(List<PayeeRef> payees, string name)
    {
        var hit = payees.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return hit;
        var close = payees.Where(p => Has(p.Name, name.Trim()) || Has(name, p.Name)).Take(8).Select(p => p.Name).ToList();
        throw new ArgumentException($"There's no payee \"{name}\"." + (close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : ""));
    }

    private object Propose(string kind, object payload, string summary)
    {
        Proposed.Add(new Pending(kind, JsonSerializer.Serialize(payload, payload.GetType(), Json)));
        return new
        {
            proposal_created = true,
            summary,
            note = "Nothing has changed yet: the user will see a card under your answer and can apply or dismiss it. "
                + "Tell them what you proposed; don't say it's done.",
        };
    }

    // ── Propose (called by AiTools for propose_* tool calls) ───────────────

    public async Task<object> ProposeAsync(ApplicationUser user, string name, JsonObject a)
    {
        if (!user.AiAllowProposals) return new { error = "The user hasn't allowed the assistant to suggest changes (Settings → AI Assistant)." };
        switch (name)
        {
            case "propose_set_category" when user.AiShareTransactions: return await SetCategoryAsync(user, a);
            case "propose_link_transfers" when user.AiShareTransactions: return await LinkAsync(user, a);
            case "propose_rename_payee":
            {
                var payees = await PayeesAsync(user);
                var from = FindPayee(payees, Str(a, "payee") ?? throw new ArgumentException("\"payee\" is required."));
                var to = Str(a, "new_name") ?? throw new ArgumentException("\"new_name\" is required.");
                if (to.Length > 200) throw new ArgumentException("That name is too long.");
                if (payees.Any(p => p.Id != from.Id && p.Name.Equals(to, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"A payee named \"{to}\" already exists — propose merging instead.");
                return Propose("rename_payee", new RenamePayload(from.Id, from.Name, to), $"Rename payee \"{from.Name}\" to \"{to}\".");
            }
            case "propose_merge_payees":
            {
                var payees = await PayeesAsync(user);
                var keep = FindPayee(payees, Str(a, "keep") ?? throw new ArgumentException("\"keep\" is required."));
                var names = (a["merge"] as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() ?? [];
                var merge = names.Select(n => FindPayee(payees, n)).Where(p => p.Id != keep.Id).DistinctBy(p => p.Id).ToList();
                if (merge.Count == 0) throw new ArgumentException("Name at least one other payee to merge.");
                return Propose("merge_payees", new MergePayload(keep.Id, keep.Name, merge),
                    $"Merge {string.Join(", ", merge.Select(m => $"\"{m.Name}\""))} into \"{keep.Name}\".");
            }
            case "propose_mapping_rule":
            {
                var payee = FindPayee(await PayeesAsync(user), Str(a, "payee") ?? throw new ArgumentException("\"payee\" is required."));
                var pattern = Str(a, "pattern") ?? throw new ArgumentException("\"pattern\" is required.");
                if (pattern.Length > 200) throw new ArgumentException("That pattern is too long.");
                return Propose("mapping_rule", new RulePayload(pattern, payee.Id, payee.Name), $"Map payee text \"{pattern}\" to \"{payee.Name}\".");
            }
            case "propose_bill": return await BillAsync(user, a);
            default: return new { error = $"\"{name}\" isn't available (it needs Transactions to be shared)." };
        }
    }

    private async Task<object> SetCategoryAsync(ApplicationUser user, JsonObject a)
    {
        var dek = user.EncryptedDataKey;
        var (catId, catLabel) = await FindCategoryAsync(user, Str(a, "category") ?? throw new ArgumentException("\"category\" is required."));
        var from = Date(a, "from"); var to = Date(a, "to");
        var payee = Str(a, "payee"); var text = Str(a, "text"); var account = Str(a, "account");
        var min = Num(a, "min_amount") is decimal mn ? Math.Abs(mn) : (decimal?)null;
        var max = Num(a, "max_amount") is decimal mx ? Math.Abs(mx) : (decimal?)null;
        var onlyUncategorized = Bool(a, "only_uncategorized") ?? true;
        if (payee is null && text is null && account is null && from is null && to is null && min is null && max is null)
            throw new ArgumentException("Give at least one filter (payee, text, account, dates or amounts).");

        var q = db.Transactions.AsNoTracking()
            .Where(t => t.Account.UserId == user.Id && !t.IsVoided && t.TransferTransactionId == null && !t.Splits.Any());
        if (from is DateOnly f) q = q.Where(t => t.Date >= f);
        if (to is DateOnly e) q = q.Where(t => t.Date <= e);
        if (onlyUncategorized) q = q.Where(t => t.CategoryId == null);
        var raw = await q.Select(t => new { t.Id, t.Date, t.Amount, t.CategoryId, t.PayeeId, t.MemoEncrypted, Account = t.Account.Name, Payee = t.Payee == null ? null : t.Payee.NameEncrypted })
            .ToListAsync();
        var labels = await CategoryLabelsAsync(user);
        var rows = raw.Select(t => new
        {
            t.Id, t.Date, t.Amount, t.CategoryId, t.PayeeId, t.Account,
            Payee = t.Payee is null ? null : encryption.Decrypt(t.Payee, dek),
            Memo = user.AiShareMemos ? encryption.Decrypt(t.MemoEncrypted, dek) : null,
            Category = t.CategoryId is int c ? labels.GetValueOrDefault(c) : null,
        })
        .Where(t => t.CategoryId != catId)
        .Where(t => Has(t.Account, account) && (payee is null || Has(t.Payee, payee)))
        .Where(t => text is null || Has(t.Payee, text) || Has(t.Memo, text) || Has(t.Category, text))
        .Where(t => (min is null || Math.Abs(t.Amount) >= min) && (max is null || Math.Abs(t.Amount) <= max))
        .OrderByDescending(t => t.Date).ToList();
        if (rows.Count == 0) return new { proposal_created = false, summary = "No transactions match those filters (that need this category)." };
        if (rows.Count > MaxItems) throw new ArgumentException($"{rows.Count} transactions match — narrow it down to {MaxItems} or fewer.");

        var items = rows.Select(t => new TxItem(t.Id, t.Date, t.Account, t.Payee, t.Amount, t.Category)).ToList();
        var defaults = (Bool(a, "make_payee_default") ?? false)
            ? rows.Where(t => t.PayeeId != null).GroupBy(t => t.PayeeId!.Value).Select(g => new PayeeRef(g.Key, g.First().Payee ?? "")).ToList()
            : [];
        return Propose("set_category", new SetCategoryPayload(catId, catLabel, items, defaults),
            $"Set category \"{catLabel}\" on {items.Count} transaction{(items.Count == 1 ? "" : "s")}"
            + (defaults.Count > 0 ? $" and make it the default for {string.Join(", ", defaults.Select(d => d.Name))}" : "") + ".");
    }

    private async Task<object> LinkAsync(ApplicationUser user, JsonObject a)
    {
        var from = Date(a, "from"); var to = Date(a, "to"); var account = Str(a, "account");
        var min = Num(a, "min_amount"); var max = Num(a, "max_amount");
        var groups = await transfers.FindAsync(user);
        bool Fits(TransferMatcher.Match m) =>
            (account is null || Has(m.From.AccountName, account) || Has(m.To.AccountName, account))
            && (from is null || m.From.Date >= from || m.To.Date >= from) && (to is null || m.From.Date <= to || m.To.Date <= to)
            && (min is null || Math.Abs(m.From.Amount) >= Math.Abs(min.Value)) && (max is null || Math.Abs(m.From.Amount) <= Math.Abs(max.Value));
        var clear = groups.Where(g => g.Options.Count == 1 && Fits(g.Options[0])).Select(g => g.Options[0]).Take(MaxItems).ToList();
        var ambiguous = groups.Count(g => g.Options.Count > 1 && g.Options.Any(Fits));
        if (clear.Count == 0)
            return new { proposal_created = false, summary = "No clear transfer pairs found." + (ambiguous > 0 ? $" {ambiguous} have several possible matches — Utilities → Possible Transfers lets the user pick." : "") };
        var items = clear.Select(m => new LinkItem(m.From.Id, m.To.Id, m.From.Date, m.To.Date, m.From.AccountName, m.To.AccountName, Math.Abs(m.From.Amount))).ToList();
        return Propose("link_transfers", new LinkPayload(items),
            $"Link {items.Count} pair{(items.Count == 1 ? "" : "s")} as transfers."
            + (ambiguous > 0 ? $" ({ambiguous} more have several possible matches; the user can sort those out in Utilities → Possible Transfers.)" : ""));
    }

    private async Task<object> BillAsync(ApplicationUser user, JsonObject a)
    {
        var name = Str(a, "name") ?? throw new ArgumentException("\"name\" is required.");
        var accountName = Str(a, "account") ?? throw new ArgumentException("\"account\" is required.");
        var accounts = await db.Accounts.AsNoTracking().Where(x => x.UserId == user.Id && x.IsActive).ToListAsync();
        var account = accounts.FirstOrDefault(x => x.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"There's no active account \"{accountName}\". Accounts: {string.Join(", ", accounts.Select(x => x.Name))}.");
        var amount = Num(a, "amount") ?? throw new ArgumentException("\"amount\" is required.");
        if (amount == 0) throw new ArgumentException("The amount can't be zero.");
        var next = Date(a, "next_due") ?? throw new ArgumentException("\"next_due\" is required.");
        var every = Math.Clamp((int)(Num(a, "every") ?? 1), 1, 365);
        var unit = (Str(a, "unit") ?? "months").ToLowerInvariant().TrimEnd('s') switch
        {
            "day" => FrequencyUnit.Days, "week" => FrequencyUnit.Weeks, "year" => FrequencyUnit.Years, "month" => FrequencyUnit.Months,
            var u => throw new ArgumentException($"Unknown unit \"{u}\" — use days, weeks, months or years."),
        };
        PayeeRef? payee = null; var newPayee = false; string? payeeName = Str(a, "payee");
        if (payeeName is not null)
        {
            payee = (await PayeesAsync(user)).FirstOrDefault(p => p.Name.Equals(payeeName, StringComparison.OrdinalIgnoreCase));
            newPayee = payee is null;
            payeeName = payee?.Name ?? payeeName;
        }
        (int Id, string Label)? cat = Str(a, "category") is string c ? await FindCategoryAsync(user, c) : null;
        var payload = new BillPayload(name.Length > 100 ? name[..100] : name, account.Id, account.Name, payee?.Id, payeeName, newPayee,
            cat?.Id, cat?.Label, amount, every, unit, next);
        return Propose("create_bill", payload, $"Create the bill \"{payload.Name}\": {Money(amount)} every {Every(every, unit)} from {account.Name}, next due {Day(next)}.");
    }

    private static string Every(int n, FrequencyUnit u)
    {
        var unit = u.ToString().ToLowerInvariant().TrimEnd('s');
        return n == 1 ? unit : $"{n} {unit}s";
    }

    // ── Saving, showing ────────────────────────────────────────────────────

    /// <summary>Adds the proposals made while answering to the saved answer.</summary>
    public void AttachTo(AiMessage answer, ApplicationUser user)
    {
        foreach (var p in Proposed)
            answer.Proposals.Add(new AiProposal
            {
                UserId = user.Id, Kind = p.Kind,
                PayloadEncrypted = encryption.Encrypt(p.PayloadJson, user.EncryptedDataKey)!,
            });
        Proposed.Clear();
    }

    public record Item(string Key, string Label);
    public record View(int Id, string Kind, string Title, List<string> Lines, List<Item>? Items, string Status, string? Result);

    /// <summary>What the Ask page shows for a proposal.</summary>
    public View Describe(AiProposal p, ApplicationUser user)
    {
        var dek = user.EncryptedDataKey;
        var json = encryption.Decrypt(p.PayloadEncrypted, dek) ?? "{}";
        var result = encryption.Decrypt(p.ResultEncrypted, dek);
        View V(string title, List<string> lines, List<Item>? items = null) => new(p.Id, p.Kind, title, lines, items, p.Status, result);
        switch (p.Kind)
        {
            case "set_category":
            {
                var x = JsonSerializer.Deserialize<SetCategoryPayload>(json, Json)!;
                var lines = x.MakeDefault.Count > 0 ? new List<string> { $"Also make it the default category for {string.Join(", ", x.MakeDefault.Select(d => d.Name))}." } : [];
                return V($"Set category to {x.CategoryLabel} on {x.Items.Count} transaction{(x.Items.Count == 1 ? "" : "s")}", lines,
                    x.Items.Select(i => new Item(i.Id.ToString(CultureInfo.InvariantCulture),
                        $"{Day(i.Date)} · {i.Payee ?? "(no payee)"} · {Money(i.Amount)} · {i.Account}{(i.Current is null ? "" : $" (now {i.Current})")}")).ToList());
            }
            case "link_transfers":
            {
                var x = JsonSerializer.Deserialize<LinkPayload>(json, Json)!;
                return V($"Link {x.Items.Count} pair{(x.Items.Count == 1 ? "" : "s")} as transfers",
                    ["Each pair is joined like a transfer you entered yourself; their categories are cleared, dates and everything else are kept."],
                    x.Items.Select(i => new Item($"{i.OutId}-{i.InId}",
                        $"{Money(i.Amount)} · {i.FromAccount} → {i.ToAccount} · {Day(i.OutDate)}{(i.InDate != i.OutDate ? $" / {Day(i.InDate)}" : "")}")).ToList());
            }
            case "rename_payee":
            {
                var x = JsonSerializer.Deserialize<RenamePayload>(json, Json)!;
                return V($"Rename payee \"{x.From}\" to \"{x.To}\"", []);
            }
            case "merge_payees":
            {
                var x = JsonSerializer.Deserialize<MergePayload>(json, Json)!;
                return V($"Merge {x.Merge.Count + 1} payees into \"{x.KeepName}\"",
                    [$"{string.Join(", ", x.Merge.Select(m => $"\"{m.Name}\""))} will be merged into \"{x.KeepName}\": their transactions, bills and rules move over, "
                     + "they're removed, and their names become mapping rules for future imports. This can't be undone."]);
            }
            case "mapping_rule":
            {
                var x = JsonSerializer.Deserialize<RulePayload>(json, Json)!;
                return V($"Add a payee mapping rule: \"{x.Pattern}\" → {x.PayeeName}",
                    ["Imported and synced transactions whose payee matches will get this payee (and its default category)."]);
            }
            case "create_bill":
            {
                var x = JsonSerializer.Deserialize<BillPayload>(json, Json)!;
                var lines = new List<string>
                {
                    $"{Money(x.Amount)} every {Every(x.Interval, x.Unit)}, next due {Day(x.NextDue)}",
                    $"Account: {x.AccountName}" + (x.PayeeName is null ? "" : $" · Payee: {x.PayeeName}{(x.NewPayee ? " (new)" : "")}")
                        + (x.CategoryLabel is null ? "" : $" · Category: {x.CategoryLabel}"),
                };
                return V($"Create bill \"{x.Name}\"", lines);
            }
            default: return V("Unknown change", []);
        }
    }

    // ── Apply / dismiss ────────────────────────────────────────────────────

    public class ApplyException(string message) : Exception(message);

    /// <summary>Makes the change, re-checking everything; returns a short result. Items in <paramref name="exclude"/> are skipped.</summary>
    public async Task<string> ApplyAsync(ApplicationUser user, AiProposal p, IReadOnlyCollection<string> exclude)
    {
        if (p.Status != "pending") throw new ApplyException($"This change was already {p.Status}.");
        var json = encryption.Decrypt(p.PayloadEncrypted, user.EncryptedDataKey) ?? "{}";
        var result = p.Kind switch
        {
            "set_category" => await ApplySetCategoryAsync(user, JsonSerializer.Deserialize<SetCategoryPayload>(json, Json)!, exclude),
            "link_transfers" => await ApplyLinksAsync(user, JsonSerializer.Deserialize<LinkPayload>(json, Json)!, exclude),
            "rename_payee" => await ApplyRenameAsync(user, JsonSerializer.Deserialize<RenamePayload>(json, Json)!),
            "merge_payees" => await ApplyMergeAsync(user, JsonSerializer.Deserialize<MergePayload>(json, Json)!),
            "mapping_rule" => await ApplyRuleAsync(user, JsonSerializer.Deserialize<RulePayload>(json, Json)!),
            "create_bill" => await ApplyBillAsync(user, JsonSerializer.Deserialize<BillPayload>(json, Json)!),
            _ => throw new ApplyException("Unknown kind of change."),
        };
        p.Status = "applied";
        p.ResolvedAt = DateTime.UtcNow;
        p.ResultEncrypted = encryption.Encrypt(result, user.EncryptedDataKey);
        await db.SaveChangesAsync();
        await audit.LogAsync("AI_PROPOSAL_APPLIED", "AiProposal", p.Id, new { kind = p.Kind });
        return result;
    }

    private async Task<string> ApplySetCategoryAsync(ApplicationUser user, SetCategoryPayload x, IReadOnlyCollection<string> exclude)
    {
        if (!await db.Categories.AnyAsync(c => c.Id == x.CategoryId && c.UserId == user.Id))
            throw new ApplyException("That category no longer exists.");
        var ids = x.Items.Where(i => !exclude.Contains(i.Id.ToString(CultureInfo.InvariantCulture))).Select(i => i.Id).ToList();
        if (ids.Count == 0) throw new ApplyException("No transactions are selected.");
        var txs = await db.Transactions
            .Where(t => ids.Contains(t.Id) && t.Account.UserId == user.Id && !t.IsVoided && t.TransferTransactionId == null && !t.Splits.Any())
            .ToListAsync();
        foreach (var t in txs) { t.CategoryId = x.CategoryId; t.UpdatedAt = DateTime.UtcNow; }
        var defaults = 0;
        if (x.MakeDefault.Count > 0)
        {
            var payeeIds = x.MakeDefault.Select(d => d.Id).ToList();
            foreach (var payee in await db.Payees.Where(pp => payeeIds.Contains(pp.Id) && pp.UserId == user.Id).ToListAsync())
            {
                payee.DefaultCategoryId = x.CategoryId;
                payee.BlockAutoDefaultCategory = false;
                defaults++;
            }
        }
        await db.SaveChangesAsync();
        var skipped = ids.Count - txs.Count;
        return $"Set {x.CategoryLabel} on {txs.Count} transaction{(txs.Count == 1 ? "" : "s")}."
            + (defaults > 0 ? $" Default category updated for {defaults} payee{(defaults == 1 ? "" : "s")}." : "")
            + (skipped > 0 ? $" {skipped} skipped (changed since: now a transfer, split, voided or gone)." : "");
    }

    private async Task<string> ApplyLinksAsync(ApplicationUser user, LinkPayload x, IReadOnlyCollection<string> exclude)
    {
        var pairs = x.Items.Where(i => !exclude.Contains($"{i.OutId}-{i.InId}")).ToList();
        if (pairs.Count == 0) throw new ApplyException("No pairs are selected.");
        var linked = 0; var problems = new List<string>();
        foreach (var i in pairs)
        {
            if (await transfers.LinkAsync(user, i.OutId, i.InId) is string error) problems.Add(error);
            else linked++;
        }
        if (linked == 0) throw new ApplyException(problems.FirstOrDefault() ?? "Nothing was linked.");
        return $"Linked {linked} transfer{(linked == 1 ? "" : "s")}." + (problems.Count > 0 ? $" {problems.Count} skipped ({problems[0]})" : "");
    }

    private async Task<string> ApplyRenameAsync(ApplicationUser user, RenamePayload x)
    {
        var payee = await db.Payees.FirstOrDefaultAsync(pp => pp.Id == x.PayeeId && pp.UserId == user.Id)
            ?? throw new ApplyException($"The payee \"{x.From}\" no longer exists.");
        if ((await PayeesAsync(user)).Any(pp => pp.Id != x.PayeeId && pp.Name.Equals(x.To, StringComparison.OrdinalIgnoreCase)))
            throw new ApplyException($"A payee named \"{x.To}\" already exists.");
        payee.NameEncrypted = encryption.Encrypt(x.To, user.EncryptedDataKey)!;
        await db.SaveChangesAsync();
        return $"Renamed \"{x.From}\" to \"{x.To}\".";
    }

    private async Task<string> ApplyMergeAsync(ApplicationUser user, MergePayload x)
    {
        try
        {
            var n = await merger.MergePayeesIntoAsync(user, x.KeepId, x.Merge.Select(m => m.Id));
            return $"Merged {n} payee{(n == 1 ? "" : "s")} into \"{x.KeepName}\".";
        }
        catch (KeyNotFoundException) { throw new ApplyException("One of those payees no longer exists."); }
    }

    private async Task<string> ApplyRuleAsync(ApplicationUser user, RulePayload x)
    {
        if (!await db.Payees.AnyAsync(pp => pp.Id == x.PayeeId && pp.UserId == user.Id))
            throw new ApplyException($"The payee \"{x.PayeeName}\" no longer exists.");
        var existing = (await db.PayeeMappingRules.AsNoTracking().Where(r => r.UserId == user.Id && !r.IsRegex).ToListAsync())
            .Any(r => string.Equals(encryption.Decrypt(r.PatternEncrypted, user.EncryptedDataKey), x.Pattern, StringComparison.OrdinalIgnoreCase));
        if (existing) throw new ApplyException($"There's already a rule for \"{x.Pattern}\".");
        db.PayeeMappingRules.Add(new PayeeMappingRule
        {
            UserId = user.Id, IsRegex = false, TargetPayeeId = x.PayeeId,
            PatternEncrypted = encryption.Encrypt(x.Pattern, user.EncryptedDataKey)!,
        });
        await db.SaveChangesAsync();
        return $"Added the rule \"{x.Pattern}\" → {x.PayeeName}.";
    }

    private async Task<string> ApplyBillAsync(ApplicationUser user, BillPayload x)
    {
        var dek = user.EncryptedDataKey;
        if (!await db.Accounts.AnyAsync(acc => acc.Id == x.AccountId && acc.UserId == user.Id && acc.IsActive))
            throw new ApplyException($"The account \"{x.AccountName}\" is no longer available.");
        if (x.CategoryId is int c && !await db.Categories.AnyAsync(cc => cc.Id == c && cc.UserId == user.Id))
            throw new ApplyException("That category no longer exists.");
        int? payeeId = x.PayeeId;
        if (payeeId is int pid && !await db.Payees.AnyAsync(pp => pp.Id == pid && pp.UserId == user.Id))
            throw new ApplyException($"The payee \"{x.PayeeName}\" no longer exists.");
        if (x.NewPayee && x.PayeeName is not null)
        {
            // Created now — unless one by that name appeared in the meantime.
            var match = (await PayeesAsync(user)).FirstOrDefault(pp => pp.Name.Equals(x.PayeeName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) payeeId = match.Id;
            else
            {
                var payee = new Payee { UserId = user.Id, NameEncrypted = encryption.Encrypt(x.PayeeName.Trim(), dek)!, DefaultCategoryId = x.CategoryId };
                db.Payees.Add(payee);
                await db.SaveChangesAsync();
                payeeId = payee.Id;
            }
        }
        db.ScheduledTransactions.Add(new ScheduledTransaction
        {
            UserId = user.Id, Name = x.Name, AccountId = x.AccountId, PayeeId = payeeId, CategoryId = x.CategoryId,
            Amount = x.Amount, FrequencyInterval = x.Interval, FrequencyUnit = x.Unit, NextDueDate = x.NextDue,
            ReminderDays = 3, AutoCreate = false, AutoCreateDaysAhead = 31, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        await posting.PostDueAsync(CancellationToken.None);
        return $"Created the bill \"{x.Name}\".";
    }

    public async Task DismissAsync(ApplicationUser user, AiProposal p)
    {
        if (p.Status != "pending") throw new ApplyException($"This change was already {p.Status}.");
        p.Status = "dismissed";
        p.ResolvedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
