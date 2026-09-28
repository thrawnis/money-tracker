using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

public class SimpleFinConflictException(string message) : Exception(message);

public record SimpleFinSyncAccountResult(
    int SimpleFinAccountId, string Name, int? LinkedAccountId, string? LinkedAccountName,
    DateOnly? From, int NewTransactions, int? DraftId, string? Note, bool Skipped = false);

public record SimpleFinSyncResult(List<SimpleFinSyncAccountResult> Accounts, List<string> Errors);

/// <summary>
/// Bank sync via SimpleFIN. A sync never writes transactions directly: for each
/// linked account it fetches everything since that account's cursor (minus an
/// overlap, since banks post late), drops rows that already exist in the
/// register, and stages the rest as an ordinary ImportDraft. The user then
/// reviews and commits it through the existing import flow — same duplicate
/// detection, payee mapping rules, and transfer linking as a file upload.
/// </summary>
public class SimpleFinService(
    AppDbContext db,
    IEncryptionService encryption,
    SimpleFinClient client,
    IAuditService audit)
{
    // Re-fetch this far behind the cursor on every sync: transactions often post
    // a few days after they happen, so without an overlap a late-posting charge
    // dated before the cursor would never be picked up. Rows already in the
    // register are filtered out, so the overlap never duplicates anything.
    public const int OverlapDays = 7;
    // First sync of an account with no transactions at all.
    public const int FirstSyncDays = 30;
    // Hard cap on how far back one sync reaches, to stay within what SimpleFIN
    // Bridge serves per request. Older history is a one-time file import.
    public const int MaxWindowDays = 90;

    private static readonly string[] CsvHeader =
        ["Date", "Account", "CheckNumber", "Payee", "Category", "SubCategory", "Memo", "Amount", "Status"];

    public async Task<object> GetStatusAsync(ApplicationUser user)
    {
        var conn = await db.SimpleFinConnections
            .Include(c => c.Accounts).ThenInclude(a => a.LinkedAccount)
            .FirstOrDefaultAsync(c => c.UserId == user.Id);
        if (conn is null) return new { connected = false };

        var dek = user.EncryptedDataKey;
        var syncDrafts = await db.ImportDrafts
            .Where(d => d.UserId == user.Id && d.SimpleFinAccountId != null)
            .Select(d => new { d.Id, d.SimpleFinAccountId, d.RowCount })
            .ToListAsync();

        return new
        {
            connected = true,
            createdAt = conn.CreatedAt,
            lastSyncAt = conn.LastSyncAt,
            lastErrors = DecryptErrors(conn.LastErrorsEncrypted, dek),
            accounts = conn.Accounts
                .Select(a =>
                {
                    var draft = syncDrafts.FirstOrDefault(d => d.SimpleFinAccountId == a.Id);
                    return new
                    {
                        id = a.Id,
                        name = encryption.Decrypt(a.NameEncrypted, dek),
                        orgName = encryption.Decrypt(a.OrgNameEncrypted, dek),
                        balance = a.Balance,
                        currency = a.Currency,
                        linkedAccountId = a.LinkedAccountId,
                        linkedAccountName = a.LinkedAccount?.Name,
                        syncedThrough = a.SyncedThrough,
                        pendingDraftId = draft?.Id,
                        pendingDraftRows = draft?.RowCount,
                    };
                })
                .OrderBy(a => a.orgName).ThenBy(a => a.name)
                .ToList(),
        };
    }

    public async Task ConnectAsync(ApplicationUser user, string setupToken, CancellationToken ct)
    {
        if (await db.SimpleFinConnections.AnyAsync(c => c.UserId == user.Id, ct))
            throw new SimpleFinConflictException("SimpleFIN is already connected. Disconnect first to use a different setup token.");

        var accessUrl = await client.ClaimAsync(setupToken, ct);
        // Balances-only: lists the accounts so they can be linked, without
        // pulling any transaction history until the user actually syncs.
        var set = await client.GetAccountsAsync(accessUrl, null, balancesOnly: true, ct);

        var conn = new SimpleFinConnection
        {
            UserId = user.Id,
            AccessUrlEncrypted = encryption.Encrypt(accessUrl, user.EncryptedDataKey)!,
            LastErrorsEncrypted = EncryptErrors(set.Errors, user.EncryptedDataKey),
        };
        db.SimpleFinConnections.Add(conn);
        await db.SaveChangesAsync(ct);

        UpsertAccounts(user, conn, set);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("SIMPLEFIN_CONNECT", "User", null, new { accounts = set.Accounts.Count });
    }

    public async Task DisconnectAsync(ApplicationUser user)
    {
        var conn = await db.SimpleFinConnections.FirstOrDefaultAsync(c => c.UserId == user.Id);
        if (conn is null) return;

        // Unreviewed sync drafts go too: once their SimpleFIN link is gone they'd
        // fall back to ordinary-file handling, whose within-file repeat check
        // would silently drop legitimate same-day, same-amount bank rows.
        var accountIds = await db.SimpleFinAccounts.Where(a => a.ConnectionId == conn.Id).Select(a => a.Id).ToListAsync();
        await db.ImportDrafts
            .Where(d => d.UserId == user.Id && d.SimpleFinAccountId != null && accountIds.Contains(d.SimpleFinAccountId.Value))
            .ExecuteDeleteAsync();

        db.SimpleFinConnections.Remove(conn); // cascades to SimpleFinAccounts
        await db.SaveChangesAsync();

        await audit.LogAsync("SIMPLEFIN_DISCONNECT");
    }

    public async Task LinkAsync(ApplicationUser user, int simpleFinAccountId, int? localAccountId)
    {
        var sfAccount = await db.SimpleFinAccounts
            .FirstOrDefaultAsync(a => a.Id == simpleFinAccountId && a.UserId == user.Id)
            ?? throw new KeyNotFoundException();

        if (localAccountId.HasValue)
        {
            var local = await db.Accounts.FirstOrDefaultAsync(a => a.Id == localAccountId.Value && a.UserId == user.Id)
                ?? throw new KeyNotFoundException();
            if (!local.IsActive)
                throw new SimpleFinConflictException("Inactive accounts can't be linked for syncing.");

            var other = await db.SimpleFinAccounts
                .FirstOrDefaultAsync(a => a.LinkedAccountId == localAccountId.Value && a.Id != simpleFinAccountId);
            if (other is not null)
                throw new SimpleFinConflictException(
                    $"\"{local.Name}\" is already linked to another bank account. Unlink it there first — two feeds into one account would import every transaction twice.");
        }

        if (sfAccount.LinkedAccountId == localAccountId) return;

        // A different target account has different history: the cursor no
        // longer applies, and any staged draft was built for the old target.
        sfAccount.LinkedAccountId = localAccountId;
        sfAccount.SyncedThrough = null;
        await db.ImportDrafts
            .Where(d => d.UserId == user.Id && d.SimpleFinAccountId == sfAccount.Id)
            .ExecuteDeleteAsync();
        await db.SaveChangesAsync();
    }

    public async Task<SimpleFinSyncResult> SyncAsync(ApplicationUser user, CancellationToken ct)
    {
        var conn = await db.SimpleFinConnections
            .Include(c => c.Accounts).ThenInclude(a => a.LinkedAccount)
            .FirstOrDefaultAsync(c => c.UserId == user.Id, ct)
            ?? throw new KeyNotFoundException();

        var dek = user.EncryptedDataKey;
        var today = UserClock.Today(user);
        var linked = conn.Accounts.Where(a => a.LinkedAccount is { IsActive: true }).ToList();
        if (linked.Count == 0)
            throw new SimpleFinConflictException("Link at least one bank account to a Money Tracker account before syncing.");

        // Per-account start date: the cursor minus the overlap; for an account
        // never synced, the newest transaction already in its register (so a
        // switch from manual CSV imports picks up where they left off); for an
        // empty account, the last FirstSyncDays days. Never beyond MaxWindowDays.
        var earliestAllowed = today.AddDays(-MaxWindowDays);
        var starts = new Dictionary<int, (DateOnly Start, bool Capped)>();
        foreach (var a in linked)
        {
            DateOnly start;
            if (a.SyncedThrough is DateOnly through)
            {
                start = through.AddDays(-OverlapDays);
            }
            else
            {
                var newest = await db.Transactions
                    .Where(t => t.AccountId == a.LinkedAccountId && t.Date <= today && !t.IsVoided)
                    .MaxAsync(t => (DateOnly?)t.Date, ct);
                start = newest ?? today.AddDays(-FirstSyncDays);
            }
            var capped = start < earliestAllowed;
            starts[a.Id] = (capped ? earliestAllowed : start, capped);
        }

        // One request for all accounts (SimpleFIN Bridge rate-limits by request).
        // A day of slack on the lower bound absorbs time-zone differences
        // between SimpleFIN's timestamps and the user's local calendar; rows are
        // filtered to each account's own start date below.
        var fetchFrom = starts.Values.Min(s => s.Start).AddDays(-1);
        SfAccountSet set;
        try
        {
            set = await client.GetAccountsAsync(
                encryption.Decrypt(conn.AccessUrlEncrypted, dek)!,
                new DateTimeOffset(fetchFrom.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                balancesOnly: false, ct);
        }
        catch (SimpleFinException ex)
        {
            conn.LastErrorsEncrypted = EncryptErrors([ex.Message], dek);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        UpsertAccounts(user, conn, set);

        var results = new List<SimpleFinSyncAccountResult>();
        foreach (var a in linked)
        {
            var name = encryption.Decrypt(a.NameEncrypted, dek) ?? a.ExternalId;
            var (start, capped) = starts[a.Id];
            var localId = a.LinkedAccountId!.Value;
            SimpleFinSyncAccountResult Result(int newTx, int? draftId, string? note, bool skipped = false) =>
                new(a.Id, name, localId, a.LinkedAccount!.Name, start, newTx, draftId, note, skipped);

            var sf = set.Accounts.FirstOrDefault(x => x.Id == a.ExternalId);
            if (sf is null)
            {
                results.Add(Result(0, null, "SimpleFIN didn't return this account this time — its bank connection may need attention on SimpleFIN Bridge.", skipped: true));
                continue;
            }

            var existingDraft = await db.ImportDrafts.FirstOrDefaultAsync(d => d.UserId == user.Id && d.AccountId == localId, ct);
            if (existingDraft is not null && existingDraft.SimpleFinAccountId is null)
            {
                results.Add(Result(0, null, "Skipped: there's an unfinished file import for this account. Finish or discard it first.", skipped: true));
                continue;
            }

            var rows = sf.Transactions
                .Where(t => !t.Pending && t.Posted > 0)
                .Select(t => (Tx: t, Date: UserClock.ToLocalDate(
                    DateTimeOffset.FromUnixTimeSeconds(t.TransactedAt is > 0 ? t.TransactedAt.Value : t.Posted), user.TimeZoneId)))
                .Where(r => r.Date >= start && r.Date <= today)
                .OrderBy(r => r.Date).ThenBy(r => r.Tx.Posted).ThenBy(r => r.Tx.Id, StringComparer.Ordinal)
                .ToList();

            // Drop rows already in the register (the overlap window, or entered by
            // hand), matching each existing transaction at most once so two real
            // same-day, same-amount charges aren't collapsed into one.
            var claimed = new HashSet<int>();
            var fresh = new List<(SfTransaction Tx, DateOnly Date)>();
            foreach (var r in rows)
            {
                var match = await db.Transactions
                    .Where(t => t.AccountId == localId && t.Date == r.Date && t.Amount == r.Tx.Amount)
                    .OrderBy(t => t.Id)
                    .Select(t => t.Id)
                    .ToListAsync(ct);
                var hit = match.FirstOrDefault(id => !claimed.Contains(id));
                if (hit != 0) claimed.Add(hit);
                else fresh.Add(r);
            }

            if (fresh.Count == 0)
            {
                // Everything fetched is already in the register — safe to move the
                // cursor forward now, since there's nothing left to review.
                if (rows.Count > 0 && (a.SyncedThrough is null || rows[^1].Date > a.SyncedThrough))
                    a.SyncedThrough = rows[^1].Date;
                results.Add(Result(0, existingDraft?.Id, capped ? CappedNote() : null));
                continue;
            }

            var csv = BuildCsv(fresh);
            var encrypted = encryption.Encrypt(Convert.ToBase64String(csv), dek)!;
            var fileName = $"Bank sync {today:yyyy-MM-dd}.csv";

            // A newer sync supersedes an unreviewed older one for the same account:
            // its window starts at the same (unadvanced) cursor, so it's a superset.
            if (existingDraft is null)
            {
                existingDraft = new ImportDraft { UserId = user.Id, AccountId = localId };
                db.ImportDrafts.Add(existingDraft);
            }
            existingDraft.FileName = fileName;
            existingDraft.FileContentEncrypted = encrypted;
            existingDraft.RowCount = fresh.Count;
            existingDraft.IncludeDuplicateIdsJson = null;
            existingDraft.PayeeOverridesJson = null;
            existingDraft.SimpleFinAccountId = a.Id;
            existingDraft.SimpleFinSyncedThrough = fresh[^1].Date;
            existingDraft.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            results.Add(Result(fresh.Count, existingDraft.Id, capped ? CappedNote() : null));
        }

        conn.LastSyncAt = DateTime.UtcNow;
        conn.LastErrorsEncrypted = EncryptErrors(set.Errors, dek);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("SIMPLEFIN_SYNC", "User", null, new
        {
            accounts = results.Count,
            newTransactions = results.Sum(r => r.NewTransactions),
        });

        return new SimpleFinSyncResult(results, set.Errors);
    }

    private static string CappedNote() =>
        $"Only the last {MaxWindowDays} days were fetched. Import anything older from a bank file export.";

    private void UpsertAccounts(ApplicationUser user, SimpleFinConnection conn, SfAccountSet set)
    {
        var dek = user.EncryptedDataKey;
        foreach (var sf in set.Accounts)
        {
            var row = conn.Accounts.FirstOrDefault(a => a.ExternalId == sf.Id);
            if (row is null)
            {
                row = new SimpleFinAccount { UserId = user.Id, ConnectionId = conn.Id, ExternalId = sf.Id };
                conn.Accounts.Add(row);
            }
            row.NameEncrypted = encryption.Encrypt(sf.Name, dek)!;
            row.OrgNameEncrypted = encryption.Encrypt(sf.OrgName, dek);
            row.Balance = sf.Balance;
            row.Currency = sf.Currency;
            row.LastSeenAt = DateTime.UtcNow;
        }
    }

    // Written in this app's own CSV template format so the existing importer
    // parses it unchanged. The Account column is left blank — the draft is
    // already bound to the linked account. Bank-posted rows import as Cleared.
    private static byte[] BuildCsv(List<(SfTransaction Tx, DateOnly Date)> rows)
    {
        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            foreach (var h in CsvHeader) csv.WriteField(h);
            csv.NextRecord();
            foreach (var (tx, date) in rows)
            {
                var payee = (tx.Payee ?? tx.Description ?? "").Trim();
                csv.WriteField(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                csv.WriteField("");
                csv.WriteField("");
                csv.WriteField(payee);
                csv.WriteField("");
                csv.WriteField("");
                csv.WriteField(tx.Memo?.Trim() ?? "");
                csv.WriteField(tx.Amount.ToString("0.00", CultureInfo.InvariantCulture));
                csv.WriteField("Cleared");
                csv.NextRecord();
            }
        }
        return ms.ToArray();
    }

    private string? EncryptErrors(List<string> errors, string dek) =>
        errors.Count == 0 ? null : encryption.Encrypt(JsonSerializer.Serialize(errors), dek);

    private List<string> DecryptErrors(string? encrypted, string dek)
    {
        if (encrypted is null) return [];
        try { return JsonSerializer.Deserialize<List<string>>(encryption.Decrypt(encrypted, dek)!) ?? []; }
        catch { return []; }
    }
}
