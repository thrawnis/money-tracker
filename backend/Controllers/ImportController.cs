using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/import")]
public class ImportController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager,
    SimpleFinService simpleFin) : ControllerBase
{
    // A slow import (many DB round-trips per row) can outlast a client-side or
    // proxy timeout — the browser reports failure while the request keeps running
    // server-side. If the user retries, a second concurrent import for the same
    // user wouldn't see the first one's not-yet-committed rows and would insert
    // everything a second time. This per-user guard rejects that overlap outright
    // instead of relying on request duration to stay under any particular timeout.
    private static readonly ConcurrentDictionary<string, byte> ImportsInProgress = new();

    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    // ── CSV row DTO ──

    private class CsvRow
    {
        public string? Date { get; set; }
        public string? Account { get; set; }
        public string? CheckNumber { get; set; }
        public string? Payee { get; set; }
        public string? Category { get; set; }
        public string? SubCategory { get; set; }
        public string? Memo { get; set; }
        public string? Amount { get; set; }
        public string? Status { get; set; }

        // QIF-only: populated from S/E/$ split lines. Never set for CSV rows
        // (CsvHelper only maps columns present in the file's header).
        public List<QifSplit>? Splits { get; set; }
    }

    private class QifSplit
    {
        public string? Category { get; set; }
        public string? Memo { get; set; }
        public string? Amount { get; set; }
    }

    // ── Template download ──

    [HttpGet("template/{format}")]
    public IActionResult GetTemplate(string format)
    {
        var headers = "Date,Account,CheckNumber,Payee,Category,SubCategory,Memo,Amount,Status\n";
        if (format == "csv")
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(headers);
            return File(bytes, "text/csv", "template.csv");
        }
        // xlsx — return csv for now (simple fallback)
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(headers);
        return File(csvBytes, "text/csv", "template.csv");
    }

    // ── Preview ──

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(IFormFile file, [FromForm] int? accountId)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        byte[] fileBytes;
        using (var ms = new MemoryStream()) { await file.CopyToAsync(ms); fileBytes = ms.ToArray(); }

        var result = await BuildPreviewAsync(userId, file.FileName, fileBytes, accountId);
        if (result.Error is not null) return result.Error;

        // Stage this upload so the review isn't lost if the user is interrupted
        // before committing. Only for single-account imports — multi-account
        // QIF/JSON files don't have one AccountId to key a draft on.
        if (accountId.HasValue)
        {
            var draftId = await SaveDraftAsync(userId, accountId.Value, file.FileName, fileBytes, result.Rows!.Count);
            result.Response!.DraftId = draftId;
        }

        return Ok(result.Response);
    }

    // Shared by both a fresh upload and resuming a saved ImportDraft — parses
    // the file, computes duplicates/transfer-match preview, and flags raw payee
    // strings that don't already resolve to a known payee or mapping rule so
    // the caller can ask the user to confirm/redirect them before committing.
    // rowsAreDistinct: set for SimpleFIN sync drafts, whose rows each carry a
    // bank-assigned transaction id — two rows with the same account, date, and
    // amount there are two real transactions (two identical coffees), not an
    // export glitch, so the within-file repeat check that protects hand-made
    // files must not drop the second one.
    private async Task<(IActionResult? Error, PreviewResponse? Response, List<CsvRow>? Rows)> BuildPreviewAsync(
        string userId, string fileName, byte[] fileBytes, int? accountId, bool rowsAreDistinct = false,
        IReadOnlySet<int>? notDuplicates = null)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".ofx" or ".qfx")
            return (Ok(new { total = 0, duplicates = Array.Empty<object>(), newTransactions = 0, transferMatches = 0, error = "OFX import coming soon" }), null, null);

        if (ext is not ".csv" and not ".xlsx" and not ".qif" and not ".json")
            return (BadRequest(new { message = "Unsupported file format." }), null, null);

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return (Unauthorized(), null, null);
        var dek = user.EncryptedDataKey;

        var userAccounts = await db.Accounts
            .Where(a => a.UserId == userId && a.IsActive)
            .ToListAsync();

        if (accountId.HasValue && userAccounts.All(a => a.Id != accountId.Value))
            return (BadRequest(new { message = "Selected account not found." }), null, null);

        List<CsvRow> rows;
        List<string> parseWarnings;
        try
        {
            (rows, parseWarnings) = ParseByExtension(ext, fileBytes, accountId);
        }
        catch (ImportParseException ex)
        {
            return (BadRequest(new { message = ex.Message }), null, null);
        }
        catch (Exception ex)
        {
            return (BadRequest(new { message = $"Failed to parse file: {ex.Message}" }), null, null);
        }

        var duplicates = new List<object>();
        // Every row that would be imported, keyed by its position in the file,
        // so the review screen can list them and let the user untick some.
        var newRows = new List<object>();
        var newCount = 0;
        var transferMatchCount = 0;
        var claimedTransferIds = new HashSet<int>();
        var batchKeys = new HashSet<(int AccountId, DateOnly Date, decimal Amount)>();
        var duplicatesWithinFile = 0;
        // Each existing transaction can be "the duplicate of" at most one incoming
        // row — otherwise two same-day, same-amount incoming rows would both be
        // pinned to the one existing match, and a genuinely new second one would
        // be hidden as a duplicate. Must match RunImportAsync's claiming exactly.
        var claimedExistingDupIds = new HashSet<int>();

        var allPayees = await db.Payees.Where(p => p.UserId == userId).ToListAsync();
        var rules = await db.PayeeMappingRules.Where(r => r.UserId == userId).ToListAsync();
        var unmatchedPayees = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (!string.IsNullOrWhiteSpace(row.Payee))
                CollectUnmatchedPayee(row.Payee.Trim(), allPayees, rules, dek, unmatchedPayees);

            if (!TryParseRow(row, userAccounts, out var date, out var amount, out var rowAccountId, out _, accountId))
            {
                // Can't be imported as-is (the commit reports it as an error), but
                // it's still listed so the user can see it and untick it.
                newCount++;
                newRows.Add(new
                {
                    row = rowIndex,
                    date = row.Date ?? "",
                    payee = row.Payee?.Trim() ?? "",
                    amount = (double?)null,
                    memo = row.Memo?.Trim(),
                    account = row.Account?.Trim(),
                    transfer = false,
                    invalid = true,
                });
                continue;
            }

            // Two identical rows within the same file: the DB-backed check below
            // can't see the first one yet (nothing's been imported), so flag it
            // here too — matches the safeguard applied at actual import time.
            // These are always skipped (no "include" option, unlike DB duplicates).
            if (!rowsAreDistinct && !batchKeys.Add((rowAccountId, date, amount)))
            {
                duplicatesWithinFile++;
                continue;
            }

            var existing = (await db.Transactions
                .Include(t => t.Payee)
                .Where(t => t.AccountId == rowAccountId && t.Date == date && t.Amount == amount)
                .OrderBy(t => t.Id)
                .ToListAsync())
                .FirstOrDefault(t => !claimedExistingDupIds.Contains(t.Id) && notDuplicates?.Contains(t.Id) != true);

            if (existing is not null)
            {
                claimedExistingDupIds.Add(existing.Id);
                var payeeName = existing.Payee is not null
                    ? encryption.Decrypt(existing.Payee.NameEncrypted, dek)
                    : row.Payee ?? "";
                var memo = existing.MemoEncrypted is not null
                    ? encryption.Decrypt(existing.MemoEncrypted, dek)
                    : null;

                duplicates.Add(new
                {
                    date = date.ToString("yyyy-MM-dd"),
                    payee = payeeName ?? row.Payee ?? "",
                    amount = (double)amount,
                    memo,
                    matchedTransactionId = existing.Id,
                });
            }
            else
            {
                newCount++;

                // Best-effort preview of transfer auto-linking: only checks against
                // already-committed transactions in the user's other accounts (a full
                // simulation of intra-file matches happens at actual import time).
                var match = await FindTransferMatchAsync(userId, dek, rowAccountId, date, amount, row.Memo, [], claimedTransferIds);
                if (match is not null)
                {
                    claimedTransferIds.Add(match.Id);
                    transferMatchCount++;
                }

                newRows.Add(new
                {
                    row = rowIndex,
                    date = date.ToString("yyyy-MM-dd"),
                    payee = row.Payee?.Trim() ?? "",
                    amount = (double?)amount,
                    memo = row.Memo?.Trim(),
                    account = userAccounts.FirstOrDefault(a => a.Id == rowAccountId)?.Name,
                    transfer = match is not null,
                    invalid = false,
                });
            }
        }

        var warnings = new List<string>(parseWarnings);
        if (duplicatesWithinFile > 0)
            warnings.Add($"{duplicatesWithinFile} row{(duplicatesWithinFile == 1 ? "" : "s")} in this file exactly repeat an earlier row (same account, date, and amount) and will be skipped automatically.");

        var response = new PreviewResponse
        {
            Total = rows.Count,
            Duplicates = duplicates,
            NewTransactions = newCount,
            NewRows = newRows,
            TransferMatches = transferMatchCount,
            Warnings = warnings.Count > 0 ? warnings : null,
            UnmatchedPayees = unmatchedPayees.Values,
        };

        return (null, response, rows);
    }

    // A concrete (not anonymous) type so DraftId can be attached after the
    // draft is saved, once its id is known.
    private class PreviewResponse
    {
        public int Total { get; set; }
        public object Duplicates { get; set; } = null!;
        public int NewTransactions { get; set; }
        public List<object> NewRows { get; set; } = [];
        public int TransferMatches { get; set; }
        public List<string>? Warnings { get; set; }
        public object UnmatchedPayees { get; set; } = null!;
        public int? DraftId { get; set; }
    }

    // Raw payee strings with no exact existing-payee match and no mapping rule
    // match — these would create a brand-new payee at commit time unless the
    // caller resolves them first. Suggests existing payees that share a
    // substring or a significant word, so e.g. "AMZN F98797" can be pointed at
    // an existing "Amazon.com" payee instead of creating a near-duplicate.
    private void CollectUnmatchedPayee(
        string rawPayee, List<Payee> allPayees, List<PayeeMappingRule> rules, string dek,
        Dictionary<string, object> unmatchedPayees)
    {
        if (unmatchedPayees.ContainsKey(rawPayee)) return;

        var decryptedNames = allPayees.Select(p => (p.Id, Name: encryption.Decrypt(p.NameEncrypted, dek) ?? "")).ToList();

        var exact = decryptedNames.Any(p => string.Equals(p.Name, rawPayee, StringComparison.OrdinalIgnoreCase));
        if (exact) return;

        var ruleMatch = PayeePatternMatcher.FindMatch(rules, rawPayee,
            r => encryption.Decrypt(r.PatternEncrypted, dek) ?? "", r => r.IsRegex);
        if (ruleMatch is not null) return;

        var suggestions = decryptedNames
            .Where(p => p.Name.Length > 0 && SharesSubstringOrWord(rawPayee, p.Name))
            .Take(5)
            .Select(p => new { id = p.Id, name = p.Name })
            .ToList();

        unmatchedPayees[rawPayee] = new { rawText = rawPayee, suggestions };
    }

    private static bool SharesSubstringOrWord(string a, string b)
    {
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase))
            return true;

        var separators = new[] { ' ', '-', '_', '.', '*' };
        var wordsA = a.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        var wordsB = b.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        return wordsA.Any(wa => wa.Length >= 3 && wordsB.Any(wb => string.Equals(wa, wb, StringComparison.OrdinalIgnoreCase)));
    }

    // ── Import ──

    [HttpPost]
    public async Task<IActionResult> Import(
        [FromForm] IFormFile? file,
        [FromForm] string? includeDuplicateIds,
        [FromForm] int? accountId,
        [FromForm] int? draftId,
        [FromForm] string? payeeOverrides,
        [FromForm] string? rememberPayeeMappings,
        [FromForm] string? excludeRows,
        [FromForm] string? payeeNewNames)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        string fileName;
        byte[] fileBytes;
        ImportDraft? sourceDraft = null;

        if (draftId.HasValue)
        {
            var user = await userManager.FindByIdAsync(userId);
            if (user is null) return Unauthorized();

            var draft = await db.ImportDrafts.FirstOrDefaultAsync(d => d.Id == draftId.Value && d.UserId == userId);
            if (draft is null) return NotFound(new { message = "That import draft no longer exists." });

            fileName = draft.FileName;
            fileBytes = Convert.FromBase64String(encryption.Decrypt(draft.FileContentEncrypted, user.EncryptedDataKey)!);
            accountId ??= draft.AccountId;
            sourceDraft = draft;
        }
        else if (file is not null)
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            fileBytes = ms.ToArray();
            fileName = file.FileName;
        }
        else
        {
            return BadRequest(new { message = "No file or draft provided." });
        }

        if (!ImportsInProgress.TryAdd(userId, 0))
            return Conflict(new { message = "An import is already in progress for your account. Please wait for it to finish before starting another." });
        try
        {
            var fromSync = sourceDraft?.SimpleFinAccountId is not null;
            var excluded = ImportDraftJson.ReadInts(excludeRows).ToHashSet();
            var skippedRows = new List<(int Index, DateOnly Date, decimal Amount, string? Payee)>();
            var outcomes = new List<(int Index, Transaction Tx)>();
            var tied = fromSync ? await TiedTransactionIdsAsync(sourceDraft!.SimpleFinAccountId!.Value) : null;
            var result = await RunImportAsync(fileName, fileBytes, includeDuplicateIds, accountId, userId,
                payeeOverrides, rememberPayeeMappings, rowsAreDistinct: fromSync, excluded, skippedRows,
                payeeNewNamesJson: payeeNewNames,
                notDuplicates: tied, rowOutcomes: fromSync ? outcomes : null);

            // Each bank row is recorded by its SimpleFIN id against the register
            // transaction it became (or already was), so later syncs recognize it.
            if (fromSync && result is OkObjectResult && outcomes.Count > 0)
            {
                var txIds = ImportDraftJson.ReadStrings(sourceDraft!.SimpleFinTxIdsJson);
                var sfId = sourceDraft.SimpleFinAccountId!.Value;
                var already = (await db.SimpleFinImportedTransactions
                        .Where(i => i.SimpleFinAccountId == sfId)
                        .Select(i => i.ExternalId)
                        .ToListAsync())
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var (index, tx) in outcomes)
                {
                    if (index >= txIds.Count || !already.Add(txIds[index])) continue;
                    db.SimpleFinImportedTransactions.Add(new SimpleFinImportedTransaction
                    {
                        UserId = userId, SimpleFinAccountId = sfId, ExternalId = txIds[index], TransactionId = tx.Id,
                    });
                }
                await db.SaveChangesAsync();
            }

            // Bank rows the user unticked are remembered, so the next sync
            // doesn't offer them again (they can be restored from the Sync screen).
            if (fromSync && result is OkObjectResult && skippedRows.Count > 0)
            {
                var txIds = ImportDraftJson.ReadStrings(sourceDraft!.SimpleFinTxIdsJson);
                var user = await userManager.FindByIdAsync(userId);
                if (user is not null)
                    await simpleFin.RecordSkippedAsync(user, sourceDraft.SimpleFinAccountId!.Value,
                        skippedRows.Where(r => r.Index < txIds.Count)
                            .Select(r => (txIds[r.Index], r.Date, r.Amount, r.Payee)));
            }

            // A reviewed-and-committed SimpleFIN draft moves that bank account's
            // sync cursor forward, so the next sync starts from here (minus an
            // overlap). Only on success, and only from the draft itself —
            // discarding a draft never advances it, so nothing gets skipped.
            if (fromSync && result is OkObjectResult && sourceDraft!.SimpleFinSyncedThrough is DateOnly through)
            {
                var sfAccount = await db.SimpleFinAccounts
                    .FirstOrDefaultAsync(a => a.Id == sourceDraft.SimpleFinAccountId && a.UserId == userId);
                if (sfAccount is not null && (sfAccount.SyncedThrough is null || through > sfAccount.SyncedThrough))
                {
                    sfAccount.SyncedThrough = through;
                    await db.SaveChangesAsync();
                }
            }

            // A completed import (whichever path it came from) supersedes any
            // staged draft for this account — nothing left to resume.
            if (accountId.HasValue)
            {
                var staleDraft = await db.ImportDrafts.FirstOrDefaultAsync(d => d.UserId == userId && d.AccountId == accountId.Value);
                if (staleDraft is not null)
                {
                    db.ImportDrafts.Remove(staleDraft);
                    await db.SaveChangesAsync();
                }
            }

            return result;
        }
        finally
        {
            ImportsInProgress.TryRemove(userId, out _);
        }
    }

    private async Task<IActionResult> RunImportAsync(
        string fileName, byte[] fileBytes, string? includeDuplicateIds, int? accountId, string userId,
        string? payeeOverridesJson, string? rememberPayeeMappingsJson, bool rowsAreDistinct = false,
        IReadOnlySet<int>? excludedRows = null,
        List<(int Index, DateOnly Date, decimal Amount, string? Payee)>? skippedExcluded = null,
        IReadOnlySet<int>? notDuplicates = null,
        List<(int Index, Transaction Tx)>? rowOutcomes = null,
        string? payeeNewNamesJson = null)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".ofx" or ".qfx")
            return Ok(new { imported = 0, errors = new[] { "OFX import coming soon" } });

        if (ext is not ".csv" and not ".xlsx" and not ".qif" and not ".json")
            return BadRequest(new { message = "Unsupported file format." });

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        var userAccounts = await db.Accounts
            .Where(a => a.UserId == userId && a.IsActive)
            .ToListAsync();

        if (accountId.HasValue && userAccounts.All(a => a.Id != accountId.Value))
            return BadRequest(new { message = "Selected account not found." });

        var includedIds = new HashSet<int>();
        if (!string.IsNullOrWhiteSpace(includeDuplicateIds))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<int[]>(includeDuplicateIds);
                if (parsed is not null)
                    foreach (var id in parsed) includedIds.Add(id);
            }
            catch { /* ignore parse errors */ }
        }

        // Raw payee text -> the specific existing payee the user chose during
        // review, instead of creating a new payee for it (see BuildPreviewAsync's
        // unmatchedPayees). Keys are trimmed raw text, matched case-insensitively.
        var payeeOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(payeeOverridesJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(payeeOverridesJson);
                if (parsed is not null)
                    foreach (var kv in parsed) payeeOverrides[kv.Key] = kv.Value;
            }
            catch { /* ignore parse errors */ }
        }

        // Raw payee text -> a name for a new payee the user typed during review
        // ("create new payee"), used instead of the raw text. If a payee with
        // that name already exists it's used rather than duplicated.
        var payeeNewNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, name) in ParseStringMap(payeeNewNamesJson))
            if (!string.IsNullOrWhiteSpace(name)) payeeNewNames[raw.Trim()] = name.Trim();
        // Raw text -> the payee it resolved to through payeeNewNames, so a
        // "remember" mapping rule can point at it.
        var resolvedNewNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var rememberMappings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(rememberPayeeMappingsJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<string[]>(rememberPayeeMappingsJson);
                if (parsed is not null)
                    foreach (var raw in parsed) rememberMappings.Add(raw);
            }
            catch { /* ignore parse errors */ }
        }

        List<CsvRow> rows;
        List<string> parseWarnings;
        try
        {
            (rows, parseWarnings) = ParseByExtension(ext, fileBytes, accountId);
        }
        catch (ImportParseException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Failed to parse file: {ex.Message}" });
        }

        var imported = 0;
        var errors = new List<string>(parseWarnings);
        var newPayeesCreated = new List<object>();

        // Newly-created rows from this same import call that haven't been claimed as
        // a transfer match yet — lets both legs of a transfer land together when a
        // single file contains multiple accounts (via the CSV "Account" column).
        var openBatchTransactions = new List<Transaction>();
        var claimedExistingIds = new HashSet<int>();
        var pendingLinks = new List<(Transaction NewTx, Transaction Match)>();
        var pendingSplits = new List<(Transaction Tx, List<TransactionSplit> Splits)>();

        // Find-or-create a category (and optional subcategory), reusing the same
        // in-memory list across the whole import so categories created earlier in
        // this call are visible to later rows.
        List<Category>? allCategoriesCache = null;
        async Task<int?> ResolveCategoryIdAsync(string? name, string? sub)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            allCategoriesCache ??= await db.Categories.Where(c => c.UserId == userId).ToListAsync();

            var catName = name.Trim();
            var parent = allCategoriesCache.FirstOrDefault(c => c.ParentId == null &&
                string.Equals(encryption.Decrypt(c.NameEncrypted, dek), catName, StringComparison.OrdinalIgnoreCase));
            if (parent is null)
            {
                parent = new Category { UserId = userId, NameEncrypted = encryption.Encrypt(catName, dek)! };
                db.Categories.Add(parent);
                await db.SaveChangesAsync();
                allCategoriesCache.Add(parent);
            }

            if (string.IsNullOrWhiteSpace(sub)) return parent.Id;

            var subName = sub.Trim();
            var subCat = allCategoriesCache.FirstOrDefault(c => c.ParentId == parent.Id &&
                string.Equals(encryption.Decrypt(c.NameEncrypted, dek), subName, StringComparison.OrdinalIgnoreCase));
            if (subCat is null)
            {
                subCat = new Category { UserId = userId, ParentId = parent.Id, NameEncrypted = encryption.Encrypt(subName, dek)! };
                db.Categories.Add(subCat);
                await db.SaveChangesAsync();
                allCategoriesCache.Add(subCat);
            }
            return subCat.Id;
        }

        // Rows already added earlier in this same file (account/date/amount) — the
        // DB-backed duplicate check below only sees committed rows, so without this
        // two identical rows in one file (a common symptom of a bad export) would
        // both be inserted as "new" since neither exists in the DB yet when checked.
        var batchKeys = new HashSet<(int AccountId, DateOnly Date, decimal Amount)>();
        // Same claiming rule as BuildPreviewAsync (see there). addedThisRun
        // matters because payee/category creation below calls SaveChanges
        // mid-loop, flushing already-queued rows into the DB — without it a
        // second identical incoming row would find the first one just
        // inserted and be skipped as its "duplicate".
        var claimedExistingDupIds = new HashSet<int>();
        var addedThisRun = new HashSet<Transaction>(ReferenceEqualityComparer.Instance);

        var allPayeesCache = await db.Payees.Where(p => p.UserId == userId).ToListAsync();
        var mappingRulesCache = await db.PayeeMappingRules.Where(r => r.UserId == userId).ToListAsync();

        // Find-or-create a payee, in this priority order: exact existing match,
        // then the user's chosen override for this raw text (from reviewing
        // BuildPreviewAsync's unmatchedPayees), then a saved mapping rule,
        // finally creating a brand-new payee (logged so the caller can offer to
        // remember a mapping for it next time).
        async Task<int?> ResolvePayeeIdAsync(string rawPayee)
        {
            var trimmed = rawPayee.Trim();
            if (trimmed.Length == 0) return null;

            var exact = allPayeesCache.FirstOrDefault(p =>
                string.Equals(encryption.Decrypt(p.NameEncrypted, dek), trimmed, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact.Id;

            if (payeeOverrides.TryGetValue(trimmed, out var overrideId))
                return overrideId;

            if (payeeNewNames.TryGetValue(trimmed, out var newName))
            {
                var named = allPayeesCache.FirstOrDefault(p =>
                    string.Equals(encryption.Decrypt(p.NameEncrypted, dek), newName, StringComparison.OrdinalIgnoreCase));
                if (named is null)
                {
                    named = new Payee { UserId = userId, NameEncrypted = encryption.Encrypt(newName, dek)! };
                    db.Payees.Add(named);
                    await db.SaveChangesAsync();
                    allPayeesCache.Add(named);
                    newPayeesCreated.Add(new { id = named.Id, name = newName, rawText = trimmed });
                }
                resolvedNewNames[trimmed] = named.Id;
                return named.Id;
            }

            var rule = PayeePatternMatcher.FindMatch(mappingRulesCache, trimmed,
                r => encryption.Decrypt(r.PatternEncrypted, dek) ?? "", r => r.IsRegex);
            if (rule is not null) return rule.TargetPayeeId;

            var payee = new Payee { UserId = userId, NameEncrypted = encryption.Encrypt(trimmed, dek)! };
            db.Payees.Add(payee);
            await db.SaveChangesAsync();
            allPayeesCache.Add(payee);
            newPayeesCreated.Add(new { id = payee.Id, name = trimmed, rawText = trimmed });
            return payee.Id;
        }

        await using (var dbTx = await db.Database.BeginTransactionAsync())
        {
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                var isExcluded = excludedRows?.Contains(rowIndex) == true;
                try
                {
                    if (!TryParseRow(row, userAccounts, out var date, out var amount, out var rowAccountId, out var status, accountId))
                    {
                        if (!isExcluded)
                            errors.Add($"Could not parse row: {row.Date} {row.Payee} {row.Amount}");
                        continue;
                    }

                    var batchKey = (rowAccountId, date, amount);
                    if (!rowsAreDistinct && !batchKeys.Add(batchKey))
                    {
                        errors.Add($"Skipped duplicate row within this file: {row.Date} {row.Payee} {row.Amount}");
                        continue;
                    }

                    // Check for duplicate
                    var existing = (await db.Transactions
                        .Where(t => t.AccountId == rowAccountId && t.Date == date && t.Amount == amount)
                        .OrderBy(t => t.Id)
                        .ToListAsync())
                        .FirstOrDefault(t => !addedThisRun.Contains(t) && !claimedExistingDupIds.Contains(t.Id)
                            && notDuplicates?.Contains(t.Id) != true);

                    if (existing is not null)
                    {
                        claimedExistingDupIds.Add(existing.Id);
                        if (!includedIds.Contains(existing.Id))
                        {
                            // Already in the register: the row is accounted for by that transaction.
                            rowOutcomes?.Add((rowIndex, existing));
                            continue; // skip duplicate not selected for inclusion
                        }
                    }
                    else if (isExcluded)
                    {
                        // A new row the user unticked on the review screen. Checked
                        // here, after the repeat and duplicate checks, so every
                        // other row is classified exactly as the preview showed it.
                        skippedExcluded?.Add((rowIndex, date, amount, row.Payee));
                        continue;
                    }

                    // Resolve payee
                    int? payeeId = !string.IsNullOrWhiteSpace(row.Payee) ? await ResolvePayeeIdAsync(row.Payee) : null;

                    // Resolve split categories first (QIF only) — if the split amounts
                    // don't add up to the transaction total, fall back to a plain
                    // (non-split) row using the top-level category instead of failing it.
                    List<TransactionSplit>? splits = null;
                    if (row.Splits is { Count: > 0 })
                    {
                        if (row.Splits.Sum(s => decimal.TryParse(s.Amount, NumberStyles.Any, CultureInfo.InvariantCulture, out var a) ? a : 0) == amount)
                        {
                            splits = [];
                            foreach (var s in row.Splits)
                            {
                                var colonIdx = s.Category?.IndexOf(':') ?? -1;
                                var splitCategoryId = colonIdx >= 0
                                    ? await ResolveCategoryIdAsync(s.Category![..colonIdx], s.Category[(colonIdx + 1)..])
                                    : await ResolveCategoryIdAsync(s.Category, null);
                                decimal.TryParse(s.Amount, NumberStyles.Any, CultureInfo.InvariantCulture, out var splitAmount);
                                splits.Add(new TransactionSplit
                                {
                                    CategoryId    = splitCategoryId,
                                    Amount        = splitAmount,
                                    MemoEncrypted = string.IsNullOrWhiteSpace(s.Memo) ? null : encryption.Encrypt(s.Memo.Trim(), dek),
                                });
                            }
                        }
                        else
                        {
                            errors.Add($"Split amounts didn't add up to the total for {row.Date} {row.Payee} — imported without splits.");
                        }
                    }

                    // Resolve category (and subcategory, if present) — unused when split
                    var categoryId = splits is null ? await ResolveCategoryIdAsync(row.Category, row.SubCategory) : null;

                    var tx = new Transaction
                    {
                        AccountId = rowAccountId,
                        Date = date,
                        Amount = amount,
                        PayeeId = payeeId,
                        CategoryId = categoryId,
                        CheckNumberEncrypted = string.IsNullOrWhiteSpace(row.CheckNumber)
                            ? null
                            : encryption.Encrypt(row.CheckNumber.Trim(), dek),
                        MemoEncrypted = string.IsNullOrWhiteSpace(row.Memo)
                            ? null
                            : encryption.Encrypt(row.Memo.Trim(), dek),
                        Status = status,
                    };

                    if (splits is { Count: > 0 })
                        pendingSplits.Add((tx, splits));

                    // Detect a matching transfer leg: same date, opposite-sign amount,
                    // matching memo, in a different account, not already linked.
                    // Split transactions are never transfers (Money never mixes the two).
                    var match = splits is null
                        ? await FindTransferMatchAsync(userId, dek, rowAccountId, date, amount, row.Memo, openBatchTransactions, claimedExistingIds)
                        : null;
                    if (match is not null)
                    {
                        tx.TransferAccountId = match.AccountId;
                        match.TransferAccountId = rowAccountId;
                        pendingLinks.Add((tx, match));

                        if (match.Id != 0) claimedExistingIds.Add(match.Id);
                        else openBatchTransactions.Remove(match);
                    }
                    else
                    {
                        openBatchTransactions.Add(tx);
                    }

                    db.Transactions.Add(tx);
                    addedThisRun.Add(tx);
                    rowOutcomes?.Add((rowIndex, tx));
                    imported++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Error on row {row.Date} {row.Payee}: {ex.Message}");
                }
            }

            // First save assigns real IDs to every new row (including both legs of
            // in-file transfer pairs), then cross-link transfer pairs and attach splits by ID.
            await db.SaveChangesAsync();

            foreach (var (newTx, match) in pendingLinks)
            {
                newTx.TransferTransactionId = match.Id;
                match.TransferTransactionId = newTx.Id;
            }

            foreach (var (tx, txSplits) in pendingSplits)
            {
                foreach (var split in txSplits) split.TransactionId = tx.Id;
                db.TransactionSplits.AddRange(txSplits);
            }

            if (pendingLinks.Count > 0 || pendingSplits.Count > 0)
                await db.SaveChangesAsync();

            // Persist any mapping rules the user asked to remember while resolving
            // unmatched payees — an exact-match rule pointing the raw text at
            // whichever payee it was resolved to (existing pick or override).
            foreach (var raw in rememberMappings)
            {
                var trimmed = raw.Trim();
                if (trimmed.Length == 0) continue;
                if (!payeeOverrides.TryGetValue(trimmed, out var targetPayeeId)
                    && !resolvedNewNames.TryGetValue(trimmed, out targetPayeeId)) continue;

                var alreadyExists = mappingRulesCache.Any(r =>
                    !r.IsRegex && string.Equals(encryption.Decrypt(r.PatternEncrypted, dek), trimmed, StringComparison.OrdinalIgnoreCase));
                if (alreadyExists) continue;

                db.PayeeMappingRules.Add(new PayeeMappingRule
                {
                    UserId = userId,
                    PatternEncrypted = encryption.Encrypt(trimmed, dek)!,
                    IsRegex = false,
                    TargetPayeeId = targetPayeeId,
                });
            }
            if (rememberMappings.Count > 0)
                await db.SaveChangesAsync();

            await dbTx.CommitAsync();
        }

        return Ok(new
        {
            imported,
            transfersLinked = pendingLinks.Count,
            errors = errors.Count > 0 ? errors : null,
            newPayeesCreated = newPayeesCreated.Count > 0 ? newPayeesCreated : null,
        });
    }

    // ── Import drafts (staged, not-yet-committed imports) ──

    [HttpGet("drafts")]
    public async Task<IActionResult> GetDrafts()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var drafts = await db.ImportDrafts
            .Where(d => d.UserId == userId)
            .Include(d => d.Account)
            .OrderByDescending(d => d.UpdatedAt)
            .Select(d => new
            {
                id = d.Id,
                accountId = d.AccountId,
                accountName = d.Account.Name,
                fileName = d.FileName,
                rowCount = d.RowCount,
                fromBankSync = d.SimpleFinAccountId != null,
                createdAt = d.CreatedAt,
                updatedAt = d.UpdatedAt,
            })
            .ToListAsync();

        return Ok(drafts);
    }

    // Re-parses the draft's stored file (fresh — duplicate detection has to
    // reflect the DB as it is now, not as it was when staged) and returns the
    // same shape as /preview, plus the review decisions already made so the
    // frontend can restore exactly where the user left off.
    [HttpGet("drafts/{id}/resume")]
    public async Task<IActionResult> ResumeDraft(int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var draft = await db.ImportDrafts.FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId);
        if (draft is null) return NotFound();

        var fileBytes = Convert.FromBase64String(encryption.Decrypt(draft.FileContentEncrypted, user.EncryptedDataKey)!);
        var result = await BuildPreviewAsync(userId, draft.FileName, fileBytes, draft.AccountId,
            rowsAreDistinct: draft.SimpleFinAccountId is not null,
            notDuplicates: draft.SimpleFinAccountId is int sfId ? await TiedTransactionIdsAsync(sfId) : null);
        if (result.Error is not null) return result.Error;

        return Ok(new
        {
            draftId = draft.Id,
            accountId = draft.AccountId,
            fileName = draft.FileName,
            includeDuplicateIds = string.IsNullOrWhiteSpace(draft.IncludeDuplicateIdsJson)
                ? Array.Empty<int>()
                : JsonSerializer.Deserialize<int[]>(draft.IncludeDuplicateIdsJson),
            payeeOverrides = string.IsNullOrWhiteSpace(draft.PayeeOverridesJson)
                ? new Dictionary<string, int>()
                : JsonSerializer.Deserialize<Dictionary<string, int>>(draft.PayeeOverridesJson),
            payeeNewNames = ParseStringMap(encryption.Decrypt(draft.PayeeNewNamesJson, user.EncryptedDataKey)),
            excludedRows = ImportDraftJson.ReadInts(draft.ExcludedRowsJson),
            fromBankSync = draft.SimpleFinAccountId is not null,
            preview = result.Response,
        });
    }

    public record DraftReviewDto(
        int[]? IncludeDuplicateIds, Dictionary<string, int>? PayeeOverrides, int[]? ExcludedRows,
        Dictionary<string, string>? PayeeNewNames);

    // Saves in-progress review choices (which duplicates to include, payee
    // resolutions picked so far) without committing — called as the user works
    // through the review screen, so an interruption loses at most the last
    // unsaved tweak, not the whole review.
    [HttpPut("drafts/{id}")]
    public async Task<IActionResult> UpdateDraft(int id, DraftReviewDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var draft = await db.ImportDrafts.FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId);
        if (draft is null) return NotFound();

        if (dto.IncludeDuplicateIds is not null)
            draft.IncludeDuplicateIdsJson = JsonSerializer.Serialize(dto.IncludeDuplicateIds);
        if (dto.PayeeOverrides is not null)
            draft.PayeeOverridesJson = JsonSerializer.Serialize(dto.PayeeOverrides);
        if (dto.ExcludedRows is not null)
            draft.ExcludedRowsJson = JsonSerializer.Serialize(dto.ExcludedRows);
        if (dto.PayeeNewNames is not null)
            draft.PayeeNewNamesJson = encryption.Encrypt(JsonSerializer.Serialize(dto.PayeeNewNames),
                (await userManager.FindByIdAsync(userId))!.EncryptedDataKey);
        draft.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("drafts/{id}")]
    public async Task<IActionResult> DeleteDraft(int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var draft = await db.ImportDrafts.FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId);
        if (draft is null) return NotFound();

        db.ImportDrafts.Remove(draft);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // Register transactions already tied to a bank id for this SimpleFIN
    // account. A row in a sync draft has a different bank id, so it's a
    // different bank transaction and must never be taken as their duplicate.
    private static Dictionary<string, string> ParseStringMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private async Task<HashSet<int>> TiedTransactionIdsAsync(int simpleFinAccountId) =>
        (await db.SimpleFinImportedTransactions
            .Where(i => i.SimpleFinAccountId == simpleFinAccountId && i.TransactionId != null)
            .Select(i => i.TransactionId!.Value)
            .ToListAsync())
        .ToHashSet();

    private async Task<int?> SaveDraftAsync(string userId, int accountId, string fileName, byte[] fileBytes, int rowCount)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return null;

        var draft = await db.ImportDrafts.FirstOrDefaultAsync(d => d.UserId == userId && d.AccountId == accountId);
        var encryptedContent = encryption.Encrypt(Convert.ToBase64String(fileBytes), user.EncryptedDataKey)!;

        if (draft is null)
        {
            draft = new ImportDraft
            {
                UserId = userId,
                AccountId = accountId,
                FileName = fileName,
                FileContentEncrypted = encryptedContent,
                RowCount = rowCount,
            };
            db.ImportDrafts.Add(draft);
        }
        else
        {
            // A fresh upload for this account supersedes any prior review —
            // reset the saved choices rather than reapplying them to different rows.
            draft.FileName = fileName;
            draft.FileContentEncrypted = encryptedContent;
            draft.RowCount = rowCount;
            draft.IncludeDuplicateIdsJson = null;
            draft.PayeeOverridesJson = null;
            draft.ExcludedRowsJson = null;
            draft.PayeeNewNamesJson = null;
            // A manual upload replacing a sync-generated draft is no longer a
            // bank-feed draft — it must not inherit the distinct-rows handling
            // or move the SimpleFIN sync cursor when committed.
            draft.SimpleFinAccountId = null;
            draft.SimpleFinSyncedThrough = null;
            draft.SimpleFinTxIdsJson = null;
            draft.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        return draft.Id;
    }

    // ── Helpers ──

    private class ImportParseException(string message) : Exception(message);

    private static (List<CsvRow> Rows, List<string> Warnings) ParseByExtension(string ext, byte[] fileBytes, int? accountId)
    {
        using var stream = new MemoryStream(fileBytes);

        if (ext == ".qif")
        {
            var (rows, warnings) = ParseQif(stream);
            if (rows.Count == 0)
                throw new ImportParseException("No transactions found in QIF file.");
            if (!accountId.HasValue && rows.All(r => string.IsNullOrWhiteSpace(r.Account)))
                throw new ImportParseException("This QIF file doesn't specify an account. Please select an account to import into.");
            return (rows, warnings);
        }

        if (ext == ".json")
        {
            var rows = ParseJson(stream);
            if (rows.Count == 0)
                throw new ImportParseException("No transactions found in JSON file.");
            return (rows, []);
        }

        return (ParseCsv(stream), []);
    }

    private static string? NormalizeMemo(string? memo) =>
        string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();

    // Looks for exactly one unlinked transaction — in a different account belonging
    // to the same user — with the same date, the opposite-sign amount, and a matching
    // memo. Checks both already-committed transactions (from prior imports/entries)
    // and rows created earlier in this same import call (for multi-account files).
    // Returns null if there is no match or more than one candidate (ambiguous).
    private async Task<Transaction?> FindTransferMatchAsync(
        string userId,
        string dek,
        int excludeAccountId,
        DateOnly date,
        decimal amount,
        string? memo,
        List<Transaction> openBatchTransactions,
        HashSet<int> claimedExistingIds)
    {
        var normMemo = NormalizeMemo(memo);
        var targetAmount = -amount;

        var dbCandidates = await db.Transactions
            .Where(t => t.Account.UserId == userId
                     && t.AccountId != excludeAccountId
                     && t.TransferTransactionId == null
                     && t.Date == date
                     && t.Amount == targetAmount)
            .ToListAsync();

        var candidates = dbCandidates
            .Where(t => !claimedExistingIds.Contains(t.Id))
            .Concat(openBatchTransactions.Where(t =>
                t.AccountId != excludeAccountId && t.Date == date && t.Amount == targetAmount))
            .Where(t => NormalizeMemo(encryption.Decrypt(t.MemoEncrypted, dek)) == normMemo)
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static List<CsvRow> ParseCsv(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null,
        };
        using var csv = new CsvReader(reader, config);
        return csv.GetRecords<CsvRow>().ToList();
    }

    private static bool TryParseRow(
        CsvRow row,
        List<Account> userAccounts,
        out DateOnly date,
        out decimal amount,
        out int accountId,
        out TransactionStatus status,
        int? fallbackAccountId = null)
    {
        date = default;
        amount = 0;
        accountId = 0;
        status = TransactionStatus.Uncleared;

        if (!DateOnly.TryParse(row.Date, out date)) return false;
        if (!decimal.TryParse(row.Amount, NumberStyles.Any, CultureInfo.InvariantCulture, out amount)) return false;

        if (string.IsNullOrWhiteSpace(row.Account) && fallbackAccountId.HasValue)
        {
            accountId = fallbackAccountId.Value;
        }
        else
        {
            var acct = userAccounts.FirstOrDefault(a =>
                string.Equals(a.Name, row.Account?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (acct is null) return false;
            accountId = acct.Id;
        }

        status = row.Status?.Trim().ToLowerInvariant() switch
        {
            "c" or "cleared" => TransactionStatus.Cleared,
            "r" or "reconciled" => TransactionStatus.Reconciled,
            _ => TransactionStatus.Uncleared,
        };

        return true;
    }

    // ── JSON ──
    //
    // Accepts this app's own JSON export shape: an array of
    // { account: { name }, transactions: [...] } groups (see
    // ExportController.DownloadJson). This is a plaintext format — the
    // caller decrypted every field to produce it — so importing it goes
    // through the exact same encrypt-on-write path as CSV/QIF: nothing
    // plaintext ever reaches the database or disk. Each account's name is
    // embedded per group, so (like multi-account QIF) no accountId fallback
    // is needed.

    private static List<CsvRow> ParseJson(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Expected a JSON array of account/transaction groups.");

        var rows = new List<CsvRow>();

        foreach (var group in doc.RootElement.EnumerateArray())
        {
            if (!group.TryGetProperty("account", out var accountEl)) continue;
            var accountName = JsonString(accountEl, "name");

            if (!group.TryGetProperty("transactions", out var txsEl) || txsEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var txEl in txsEl.EnumerateArray())
            {
                var row = new CsvRow
                {
                    Account     = accountName,
                    Date        = JsonString(txEl, "date"),
                    Amount      = JsonNumberAsString(txEl, "amount"),
                    Payee       = JsonString(txEl, "payee"),
                    Category    = JsonString(txEl, "category"),
                    SubCategory = JsonString(txEl, "subCategory"),
                    Memo        = JsonString(txEl, "memo"),
                    CheckNumber = JsonString(txEl, "checkNumber"),
                    Status      = JsonString(txEl, "status"),
                };

                if (txEl.TryGetProperty("splits", out var splitsEl) && splitsEl.ValueKind == JsonValueKind.Array && splitsEl.GetArrayLength() > 0)
                {
                    row.Splits = [];
                    foreach (var splitEl in splitsEl.EnumerateArray())
                    {
                        var cat = JsonString(splitEl, "category");
                        var subCat = JsonString(splitEl, "subCategory");
                        row.Splits.Add(new QifSplit
                        {
                            Category = subCat is not null ? $"{cat}:{subCat}" : cat,
                            Memo     = JsonString(splitEl, "memo"),
                            Amount   = JsonNumberAsString(splitEl, "amount"),
                        });
                    }
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    private static string? JsonString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static string? JsonNumberAsString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDecimal().ToString(CultureInfo.InvariantCulture)
            : null;

    // ── QIF (loose) ──
    //
    // Tolerant QIF parser: skips/ignores anything it doesn't understand rather
    // than failing the whole file. Supports the common Money-Sunset export
    // shapes — a single !Type:Bank/CCard/Cash/Oth A/Oth L section with no
    // embedded account (the caller supplies accountId), and multi-account
    // exports using !Account blocks. !Type:Invst (investment) sections are
    // skipped with a warning since this app has no security/quantity model.

    private static (List<CsvRow> Rows, List<string> Warnings) ParseQif(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var rows = new List<CsvRow>();
        var warnings = new List<string>();

        string? currentAccountName = null;
        bool inAccountHeader = false;
        bool skipSection = false;
        string? pendingAccountName = null;
        CsvRow? current = null;
        QifSplit? pendingSplit = null;

        void FlushRecord()
        {
            if (current is null) { pendingSplit = null; return; }
            if (pendingSplit is not null)
            {
                current.Splits ??= [];
                current.Splits.Add(pendingSplit);
                pendingSplit = null;
            }
            if (!string.IsNullOrWhiteSpace(current.Date) || !string.IsNullOrWhiteSpace(current.Amount))
            {
                current.Account = currentAccountName;
                rows.Add(current);
            }
            current = null;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0) continue;

            if (line[0] == '!')
            {
                FlushRecord();
                var header = line.Trim();
                if (header.Equals("!Account", StringComparison.OrdinalIgnoreCase))
                {
                    inAccountHeader = true;
                    pendingAccountName = null;
                    continue;
                }
                if (header.StartsWith("!Type:", StringComparison.OrdinalIgnoreCase))
                {
                    var type = header["!Type:".Length..].Trim();
                    skipSection = type.Equals("Invst", StringComparison.OrdinalIgnoreCase);
                    if (skipSection)
                        warnings.Add("Skipped an investment (!Type:Invst) section — not supported.");
                    inAccountHeader = false;
                    continue;
                }
                // Unrecognized header (!Option:, !Clear, !Category, memorized txns, etc.) — ignore loosely
                inAccountHeader = false;
                continue;
            }

            if (inAccountHeader)
            {
                if (line[0] == 'N') pendingAccountName = line[1..].Trim();
                else if (line == "^")
                {
                    currentAccountName = string.IsNullOrWhiteSpace(pendingAccountName) ? currentAccountName : pendingAccountName;
                    inAccountHeader = false;
                }
                continue; // T (account type), D (description), etc. inside the header — ignore
            }

            if (line == "^")
            {
                FlushRecord();
                continue;
            }

            if (skipSection) continue;

            current ??= new CsvRow();
            var code = line[0];
            var value = line.Length > 1 ? line[1..].Trim() : "";

            switch (code)
            {
                case 'D':
                    current.Date = NormalizeQifDate(value) ?? value;
                    break;
                case 'T':
                case 'U':
                    current.Amount = (value.StartsWith('(') && value.EndsWith(')'))
                        ? "-" + value[1..^1]
                        : value;
                    break;
                case 'P':
                    current.Payee = value;
                    break;
                case 'M':
                    current.Memo = value;
                    break;
                case 'N':
                    current.CheckNumber = value;
                    break;
                case 'C':
                    current.Status = value switch
                    {
                        "" => "",
                        "X" or "R" or "x" or "r" => "reconciled",
                        _ => "cleared",
                    };
                    break;
                case 'L':
                    // "[Account Name]" marks a transfer — the existing date/amount/memo
                    // matching already links transfer pairs, so just drop it as a category
                    // rather than treating an account name as a category.
                    if (!value.StartsWith('['))
                    {
                        var colonIdx = value.IndexOf(':');
                        if (colonIdx >= 0)
                        {
                            current.Category = value[..colonIdx].Trim();
                            current.SubCategory = value[(colonIdx + 1)..].Trim();
                        }
                        else
                        {
                            current.Category = value;
                        }
                    }
                    break;
                case 'S':
                    // Start of a new split line: flush the previous one (if any) first.
                    if (pendingSplit is not null)
                    {
                        current.Splits ??= [];
                        current.Splits.Add(pendingSplit);
                    }
                    pendingSplit = new QifSplit { Category = value.StartsWith('[') ? null : value };
                    break;
                case 'E':
                    pendingSplit ??= new QifSplit();
                    pendingSplit.Memo = value;
                    break;
                case '$':
                    pendingSplit ??= new QifSplit();
                    pendingSplit.Amount = (value.StartsWith('(') && value.EndsWith(')'))
                        ? "-" + value[1..^1]
                        : value;
                    break;
                default:
                    // Loosely ignore unsupported field codes (A address, etc.)
                    break;
            }
        }

        FlushRecord(); // tolerate a missing trailing ^

        return (rows, warnings);
    }

    // Loosely normalizes common QIF date shapes (M/D/YYYY, M/D/YY, M/ D'YY,
    // YYYY-MM-DD, etc.) to an ISO "yyyy-MM-dd" string. Returns null if it can't
    // make sense of the input, so the caller can fall back to reporting a
    // per-row parse error rather than throwing.
    private static string? NormalizeQifDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.Replace(" ", "").Replace("'", "/").Replace('-', '/').Replace('.', '/');
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], out var p1)) return null;
        if (!int.TryParse(parts[1], out var p2)) return null;
        if (!int.TryParse(parts[2], out var p3)) return null;

        try
        {
            // ISO: yyyy/MM/dd
            if (parts[0].Length == 4)
                return new DateOnly(p1, p2, p3).ToString("yyyy-MM-dd");

            // Otherwise assume US M/D/Y (Money's default export locale)
            var year = p3;
            if (year < 100) year += year < 50 ? 2000 : 1900;
            return new DateOnly(year, p1, p2).ToString("yyyy-MM-dd");
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
