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
    DateOnly? From, int NewTransactions, int? DraftId, string? Note, bool Skipped = false,
    decimal? ValueRecorded = null, DateOnly? ValueDate = null);

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
    // days after they happen, so without an overlap a late-posting charge dated
    // before the cursor would never be picked up. Two weeks, because a sync with
    // nothing new moves the cursor to today, so the overlap is all that covers
    // a charge that happened before then and posts after. Rows already in the
    // register are filtered out, so the overlap never duplicates anything.
    public const int OverlapDays = 14;
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
            .Include(c => c.Accounts).ThenInclude(a => a.SkippedTransactions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.UserId == user.Id);
        if (conn is null) return new { connected = false };

        var dek = user.EncryptedDataKey;
        var linkedIds = conn.Accounts.Where(a => a.LinkedAccountId != null).Select(a => a.LinkedAccountId!.Value).ToList();
        var latestValues = await LatestValuesAsync(linkedIds);
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
            dailyUpdateHour = conn.DailyUpdateHour,
            lastAutoUpdateDate = conn.LastAutoUpdateDate,
            accounts = conn.Accounts
                .Select(a =>
                {
                    var draft = syncDrafts.FirstOrDefault(d => d.SimpleFinAccountId == a.Id);
                    var value = a.LinkedAccountId is int lid && latestValues.TryGetValue(lid, out var v) ? v : null;
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
                        balanceOnly = a.BalanceOnly,
                        valueRecorded = value?.Balance,
                        valueDate = value?.Date,
                        pendingDraftId = draft?.Id,
                        pendingDraftRows = draft?.RowCount,
                        skipped = a.SkippedTransactions
                            .OrderByDescending(s => s.Date).ThenByDescending(s => s.Id)
                            .Select(s => new
                            {
                                id = s.Id,
                                date = s.Date,
                                amount = s.Amount,
                                payee = encryption.Decrypt(s.PayeeEncrypted, dek),
                            })
                            .ToList(),
                    };
                })
                .OrderBy(a => a.orgName).ThenBy(a => a.name)
                .ToList(),
        };
    }

    // One SimpleFIN request at a time across the whole app — every user's
    // manual syncs, the daily update, and connecting all queue here and run in
    // turn, so a busy evening can't pile parallel bank fetches (and their
    // database work) onto the server or trip SimpleFIN's rate limits.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static async Task<T> OneAtATimeAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try { return await work(); }
        finally { Gate.Release(); }
    }

    public Task ConnectAsync(ApplicationUser user, string setupToken, CancellationToken ct) =>
        OneAtATimeAsync(async () => { await ConnectCoreAsync(user, setupToken, ct); return 0; }, ct);

    private async Task ConnectCoreAsync(ApplicationUser user, string setupToken, CancellationToken ct)
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

        // Investment accounts default to balance-only (their value moves with
        // the market, not through transactions); everything else to transactions.
        if (localAccountId.HasValue)
            sfAccount.BalanceOnly = await db.Accounts
                .Where(a => a.Id == localAccountId.Value)
                .Select(a => a.Type == AccountType.Investment)
                .FirstAsync();

        // A different target account has different history: the cursor no
        // longer applies, and any staged draft was built for the old target.
        sfAccount.LinkedAccountId = localAccountId;
        sfAccount.SyncedThrough = null;
        await db.ImportDrafts
            .Where(d => d.UserId == user.Id && d.SimpleFinAccountId == sfAccount.Id)
            .ExecuteDeleteAsync();
        await db.SaveChangesAsync();
    }

    /// <param name="onlyAccountIds">
    /// SimpleFIN account ids to sync this time; null syncs every linked
    /// account. Leaving an account out only skips it for this run — its link
    /// and cursor are untouched, so a later sync picks up where it left off.
    /// </param>
    public Task<SimpleFinSyncResult> SyncAsync(
        ApplicationUser user, IReadOnlyCollection<int>? onlyAccountIds, CancellationToken ct) =>
        OneAtATimeAsync(() => SyncCoreAsync(user, onlyAccountIds, ct), ct);

    private async Task<SimpleFinSyncResult> SyncCoreAsync(
        ApplicationUser user, IReadOnlyCollection<int>? onlyAccountIds, CancellationToken ct)
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
        if (onlyAccountIds is not null)
        {
            linked = linked.Where(a => onlyAccountIds.Contains(a.Id)).ToList();
            if (linked.Count == 0)
                throw new SimpleFinConflictException("Select at least one linked bank account to sync.");
        }

        // Transactions the user chose not to import on an earlier review. Old
        // ones that no sync can reach any more are pruned as they age out.
        var linkedIds = linked.Select(a => a.Id).ToList();
        await db.SimpleFinSkippedTransactions
            .Where(s => s.UserId == user.Id && s.Date < today.AddDays(-MaxWindowDays - OverlapDays))
            .ExecuteDeleteAsync(ct);
        var skippedIds = (await db.SimpleFinSkippedTransactions
                .Where(s => s.UserId == user.Id && linkedIds.Contains(s.SimpleFinAccountId))
                .Select(s => new { s.SimpleFinAccountId, s.ExternalId })
                .ToListAsync(ct))
            .ToLookup(s => s.SimpleFinAccountId, s => s.ExternalId);

        // Balance-only accounts import nothing; their reported value is recorded
        // below. Everything after this works on the transaction-syncing ones.
        var valueAccounts = linked.Where(a => a.BalanceOnly).ToList();
        linked = linked.Where(a => !a.BalanceOnly).ToList();

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
        // With only balance-only accounts selected, ask for (almost) no history:
        // just the current balances and holdings.
        var fetchFrom = starts.Count > 0 ? starts.Values.Min(s => s.Start).AddDays(-1) : today.AddDays(-1);
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
        foreach (var a in valueAccounts)
        {
            var name = encryption.Decrypt(a.NameEncrypted, dek) ?? a.ExternalId;
            var sf = set.Accounts.FirstOrDefault(x => x.Id == a.ExternalId);
            var recorded = sf is null ? null : await RecordValueAsync(user, a.LinkedAccountId!.Value, sf, ct);
            results.Add(new SimpleFinSyncAccountResult(
                a.Id, name, a.LinkedAccountId, a.LinkedAccount!.Name, null, 0, null,
                recorded is null
                    ? sf is null
                        ? "SimpleFIN didn't return this account this time — its bank connection may need attention on SimpleFIN Bridge."
                        : "SimpleFIN didn't report a balance for this account."
                    : null,
                Skipped: recorded is null,
                ValueRecorded: recorded?.Balance, ValueDate: recorded?.Date));
        }

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

            // Windowed by the date each row happened — the same date it's matched
            // against the register by — so every row that could pair with an
            // existing transaction is in view together. A charge that posts late
            // is still caught as long as it happened within OverlapDays of the cursor.
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
            // Rows the user skipped before are neither offered again nor allowed
            // to claim an existing transaction, but still count toward the cursor.
            var skipped = skippedIds[a.Id].ToHashSet(StringComparer.Ordinal);
            var claimed = new HashSet<int>();
            var fresh = new List<(SfTransaction Tx, DateOnly Date)>();
            foreach (var r in rows)
            {
                if (skipped.Contains(r.Tx.Id)) continue;
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
                // Nothing new: a successful sync, so the account is up to date
                // through today. The next sync still re-checks the previous week
                // (OverlapDays) for late-posting charges. While an unreviewed sync
                // draft is waiting, the cursor only moves when it's committed, so
                // its rows are refetched until then.
                var upTo = existingDraft is null ? today : rows.Count > 0 ? rows[^1].Date : (DateOnly?)null;
                if (upTo is DateOnly u && (a.SyncedThrough is null || u > a.SyncedThrough))
                    a.SyncedThrough = u;
                results.Add(Result(0, existingDraft?.Id, capped ? CappedNote() : null));
                continue;
            }

            var csv = BuildCsv(fresh);
            var encrypted = encryption.Encrypt(Convert.ToBase64String(csv), dek)!;
            var fileName = $"Bank sync {today:yyyy-MM-dd}.csv";

            // A newer sync supersedes an unreviewed older one for the same account:
            // its window starts at the same (unadvanced) cursor, so it's a superset.
            // Rows already unticked in that older review stay unticked: row
            // positions change in the rebuilt file, so carry them over by bank id.
            var txIds = fresh.Select(r => r.Tx.Id).ToList();
            string? excludedJson = null;
            if (existingDraft is null)
            {
                existingDraft = new ImportDraft { UserId = user.Id, AccountId = localId };
                db.ImportDrafts.Add(existingDraft);
            }
            else
            {
                var oldIds = ImportDraftJson.ReadStrings(existingDraft.SimpleFinTxIdsJson);
                var unticked = ImportDraftJson.ReadInts(existingDraft.ExcludedRowsJson)
                    .Where(i => i >= 0 && i < oldIds.Count)
                    .Select(i => oldIds[i])
                    .ToHashSet(StringComparer.Ordinal);
                var carried = txIds.Select((id, i) => (id, i)).Where(x => unticked.Contains(x.id)).Select(x => x.i).ToArray();
                if (carried.Length > 0) excludedJson = JsonSerializer.Serialize(carried);
            }
            existingDraft.FileName = fileName;
            existingDraft.FileContentEncrypted = encrypted;
            existingDraft.RowCount = fresh.Count;
            existingDraft.IncludeDuplicateIdsJson = null;
            existingDraft.PayeeOverridesJson = null;
            existingDraft.ExcludedRowsJson = excludedJson;
            existingDraft.SimpleFinAccountId = a.Id;
            existingDraft.SimpleFinSyncedThrough = fresh[^1].Date;
            existingDraft.SimpleFinTxIdsJson = JsonSerializer.Serialize(txIds);
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

    /// <summary>
    /// Records today's value (and holdings, when reported) for every linked
    /// balance-only account. Used by the daily automatic update.
    /// </summary>
    public Task<int> UpdateBalancesAsync(ApplicationUser user, CancellationToken ct) =>
        OneAtATimeAsync(() => UpdateBalancesCoreAsync(user, ct), ct);

    private async Task<int> UpdateBalancesCoreAsync(ApplicationUser user, CancellationToken ct)
    {
        var conn = await db.SimpleFinConnections
            .Include(c => c.Accounts).ThenInclude(a => a.LinkedAccount)
            .FirstOrDefaultAsync(c => c.UserId == user.Id, ct)
            ?? throw new KeyNotFoundException();
        var targets = conn.Accounts.Where(a => a.BalanceOnly && a.LinkedAccount is { IsActive: true }).ToList();
        if (targets.Count == 0) return 0;

        var dek = user.EncryptedDataKey;
        SfAccountSet set;
        try
        {
            // Not balances-only: holdings may only come with the full account
            // payload. A start date of yesterday keeps the transaction list tiny.
            set = await client.GetAccountsAsync(
                encryption.Decrypt(conn.AccessUrlEncrypted, dek)!,
                DateTimeOffset.UtcNow.AddDays(-1), balancesOnly: false, ct);
        }
        catch (SimpleFinException ex)
        {
            conn.LastErrorsEncrypted = EncryptErrors([ex.Message], dek);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        UpsertAccounts(user, conn, set);
        var recorded = 0;
        foreach (var a in targets)
        {
            var sf = set.Accounts.FirstOrDefault(x => x.Id == a.ExternalId);
            if (sf is not null && await RecordValueAsync(user, a.LinkedAccountId!.Value, sf, ct) is not null)
                recorded++;
        }
        conn.LastErrorsEncrypted = EncryptErrors(set.Errors, dek);
        await db.SaveChangesAsync(ct);
        return recorded;
    }

    /// <summary>
    /// Saves one day's reported value for a local account, replacing any
    /// earlier value for the same day, and that day's holdings as a set.
    /// The day is SimpleFIN's balance date in the user's time zone.
    /// </summary>
    private async Task<AccountValueSnapshot?> RecordValueAsync(ApplicationUser user, int accountId, SfAccount sf, CancellationToken ct)
    {
        if (sf.Balance is not decimal balance) return null;
        var when = sf.BalanceDate is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(sf.BalanceDate.Value)
            : DateTimeOffset.UtcNow;
        var date = UserClock.ToLocalDate(when, user.TimeZoneId);
        var today = UserClock.Today(user);
        if (date > today) date = today;

        var snap = await db.AccountValueSnapshots.FirstOrDefaultAsync(v => v.AccountId == accountId && v.Date == date, ct);
        if (snap is null)
        {
            snap = new AccountValueSnapshot { UserId = user.Id, AccountId = accountId, Date = date };
            db.AccountValueSnapshots.Add(snap);
        }
        snap.Balance = balance;
        snap.UpdatedAt = DateTime.UtcNow;

        if (sf.Holdings is { Count: > 0 })
        {
            var dek = user.EncryptedDataKey;
            await db.HoldingSnapshots.Where(h => h.AccountId == accountId && h.Date == date).ExecuteDeleteAsync(ct);
            foreach (var h in sf.Holdings)
            {
                db.HoldingSnapshots.Add(new HoldingSnapshot
                {
                    UserId = user.Id,
                    AccountId = accountId,
                    Date = date,
                    HoldingKeyEncrypted = encryption.Encrypt(h.Id, dek)!,
                    SymbolEncrypted = encryption.Encrypt(h.Symbol, dek),
                    DescriptionEncrypted = encryption.Encrypt(h.Description, dek),
                    Shares = h.Shares,
                    MarketValue = h.MarketValue,
                    CostBasis = h.CostBasis,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return snap;
    }

    /// <summary>
    /// Latest recorded value for each of the given local accounts that has one.
    /// </summary>
    private async Task<Dictionary<int, AccountValueSnapshot>> LatestValuesAsync(List<int> accountIds) =>
        (await db.AccountValueSnapshots
            .Where(v => accountIds.Contains(v.AccountId))
            .GroupBy(v => v.AccountId)
            .Select(g => g.OrderByDescending(v => v.Date).First())
            .ToListAsync())
        .ToDictionary(v => v.AccountId);

    public async Task SetBalanceOnlyAsync(ApplicationUser user, int simpleFinAccountId, bool balanceOnly)
    {
        var sfAccount = await db.SimpleFinAccounts
            .FirstOrDefaultAsync(a => a.Id == simpleFinAccountId && a.UserId == user.Id)
            ?? throw new KeyNotFoundException();
        if (sfAccount.BalanceOnly == balanceOnly) return;

        sfAccount.BalanceOnly = balanceOnly;
        // Unreviewed transactions belong to the old mode; switching back to
        // transactions starts from the newest transaction in the register.
        sfAccount.SyncedThrough = null;
        await db.ImportDrafts
            .Where(d => d.UserId == user.Id && d.SimpleFinAccountId == sfAccount.Id)
            .ExecuteDeleteAsync();
        await db.SaveChangesAsync();
    }

    public async Task SetDailyUpdateHourAsync(ApplicationUser user, int? hour)
    {
        if (hour is < 0 or > 23) throw new ArgumentOutOfRangeException(nameof(hour));
        var conn = await db.SimpleFinConnections.FirstOrDefaultAsync(c => c.UserId == user.Id)
            ?? throw new KeyNotFoundException();
        conn.DailyUpdateHour = hour;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Offers a skipped transaction again: forgets the skip and rewinds the
    /// account's cursor to the transaction's date, so the next sync of that
    /// account fetches it (anything already imported is filtered out as usual).
    /// </summary>
    public async Task RestoreSkippedAsync(ApplicationUser user, int skippedId)
    {
        var skip = await db.SimpleFinSkippedTransactions
            .Include(s => s.SimpleFinAccount)
            .FirstOrDefaultAsync(s => s.Id == skippedId && s.UserId == user.Id)
            ?? throw new KeyNotFoundException();

        var account = skip.SimpleFinAccount;
        if (account.SyncedThrough is null || skip.Date < account.SyncedThrough)
            account.SyncedThrough = skip.Date;
        db.SimpleFinSkippedTransactions.Remove(skip);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Called when a sync draft is committed: remembers the rows the user
    /// unticked so later syncs don't offer them again.
    /// </summary>
    public async Task RecordSkippedAsync(
        ApplicationUser user, int simpleFinAccountId, IEnumerable<(string ExternalId, DateOnly Date, decimal Amount, string? Payee)> rows)
    {
        var existing = (await db.SimpleFinSkippedTransactions
                .Where(s => s.SimpleFinAccountId == simpleFinAccountId && s.UserId == user.Id)
                .Select(s => s.ExternalId)
                .ToListAsync())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!existing.Add(r.ExternalId)) continue;
            db.SimpleFinSkippedTransactions.Add(new SimpleFinSkippedTransaction
            {
                UserId = user.Id,
                SimpleFinAccountId = simpleFinAccountId,
                ExternalId = r.ExternalId,
                Date = r.Date,
                Amount = r.Amount,
                PayeeEncrypted = string.IsNullOrWhiteSpace(r.Payee) ? null : encryption.Encrypt(r.Payee.Trim(), user.EncryptedDataKey),
            });
        }
        await db.SaveChangesAsync();
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
