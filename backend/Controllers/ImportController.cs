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

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/import")]
public class ImportController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
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

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is ".ofx" or ".qfx")
            return Ok(new { total = 0, duplicates = Array.Empty<object>(), newTransactions = 0, transferMatches = 0, error = "OFX import coming soon" });

        if (ext is not ".csv" and not ".xlsx" and not ".qif")
            return BadRequest(new { message = "Unsupported file format." });

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        var userAccounts = await db.Accounts
            .Where(a => a.UserId == userId && a.IsActive)
            .ToListAsync();

        if (accountId.HasValue && userAccounts.All(a => a.Id != accountId.Value))
            return BadRequest(new { message = "Selected account not found." });

        List<CsvRow> rows;
        List<string> parseWarnings;
        try
        {
            if (ext == ".qif")
            {
                (rows, parseWarnings) = ParseQif(file);
                if (rows.Count == 0)
                    return BadRequest(new { message = "No transactions found in QIF file." });
                if (!accountId.HasValue && rows.All(r => string.IsNullOrWhiteSpace(r.Account)))
                    return BadRequest(new { message = "This QIF file doesn't specify an account. Please select an account to import into." });
            }
            else
            {
                rows = ParseCsv(file);
                parseWarnings = [];
            }
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Failed to parse file: {ex.Message}" });
        }

        var duplicates = new List<object>();
        var newCount = 0;
        var transferMatchCount = 0;
        var claimedTransferIds = new HashSet<int>();

        foreach (var row in rows)
        {
            if (!TryParseRow(row, userAccounts, out var date, out var amount, out var rowAccountId, out _, accountId))
            {
                newCount++;
                continue;
            }

            var existing = await db.Transactions
                .Include(t => t.Payee)
                .Where(t => t.AccountId == rowAccountId && t.Date == date && t.Amount == amount)
                .FirstOrDefaultAsync();

            if (existing is not null)
            {
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
            }
        }

        return Ok(new
        {
            total = rows.Count,
            duplicates,
            newTransactions = newCount,
            transferMatches = transferMatchCount,
            warnings = parseWarnings.Count > 0 ? parseWarnings : null,
        });
    }

    // ── Import ──

    [HttpPost]
    public async Task<IActionResult> Import(
        [FromForm] IFormFile file,
        [FromForm] string? includeDuplicateIds,
        [FromForm] int? accountId)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is ".ofx" or ".qfx")
            return Ok(new { imported = 0, errors = new[] { "OFX import coming soon" } });

        if (ext is not ".csv" and not ".xlsx" and not ".qif")
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

        List<CsvRow> rows;
        List<string> parseWarnings;
        try
        {
            if (ext == ".qif")
            {
                (rows, parseWarnings) = ParseQif(file);
                if (rows.Count == 0)
                    return BadRequest(new { message = "No transactions found in QIF file." });
                if (!accountId.HasValue && rows.All(r => string.IsNullOrWhiteSpace(r.Account)))
                    return BadRequest(new { message = "This QIF file doesn't specify an account. Please select an account to import into." });
            }
            else
            {
                rows = ParseCsv(file);
                parseWarnings = [];
            }
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Failed to parse file: {ex.Message}" });
        }

        var imported = 0;
        var errors = new List<string>(parseWarnings);

        // Newly-created rows from this same import call that haven't been claimed as
        // a transfer match yet — lets both legs of a transfer land together when a
        // single file contains multiple accounts (via the CSV "Account" column).
        var openBatchTransactions = new List<Transaction>();
        var claimedExistingIds = new HashSet<int>();
        var pendingLinks = new List<(Transaction NewTx, Transaction Match)>();

        await using (var dbTx = await db.Database.BeginTransactionAsync())
        {
            foreach (var row in rows)
            {
                try
                {
                    if (!TryParseRow(row, userAccounts, out var date, out var amount, out var rowAccountId, out var status, accountId))
                    {
                        errors.Add($"Could not parse row: {row.Date} {row.Payee} {row.Amount}");
                        continue;
                    }

                    // Check for duplicate
                    var existing = await db.Transactions
                        .Where(t => t.AccountId == rowAccountId && t.Date == date && t.Amount == amount)
                        .FirstOrDefaultAsync();

                    if (existing is not null && !includedIds.Contains(existing.Id))
                        continue; // skip duplicate not selected for inclusion

                    // Resolve payee
                    int? payeeId = null;
                    if (!string.IsNullOrWhiteSpace(row.Payee))
                    {
                        var payeeName = row.Payee.Trim();
                        var allPayees = await db.Payees.Where(p => p.UserId == userId).ToListAsync();
                        var payee = allPayees.FirstOrDefault(p =>
                            string.Equals(encryption.Decrypt(p.NameEncrypted, dek), payeeName, StringComparison.OrdinalIgnoreCase));

                        if (payee is null)
                        {
                            payee = new Payee
                            {
                                UserId = userId,
                                NameEncrypted = encryption.Encrypt(payeeName, dek),
                            };
                            db.Payees.Add(payee);
                            await db.SaveChangesAsync();
                        }
                        payeeId = payee.Id;
                    }

                    // Resolve category (and subcategory, if present)
                    int? categoryId = null;
                    if (!string.IsNullOrWhiteSpace(row.Category))
                    {
                        var catName = row.Category.Trim();
                        var allCategories = await db.Categories.Where(c => c.UserId == userId).ToListAsync();

                        var parent = allCategories.FirstOrDefault(c => c.ParentId == null &&
                            string.Equals(encryption.Decrypt(c.NameEncrypted, dek), catName, StringComparison.OrdinalIgnoreCase));
                        if (parent is null)
                        {
                            parent = new Category { UserId = userId, NameEncrypted = encryption.Encrypt(catName, dek)! };
                            db.Categories.Add(parent);
                            await db.SaveChangesAsync();
                        }

                        if (!string.IsNullOrWhiteSpace(row.SubCategory))
                        {
                            var subName = row.SubCategory.Trim();
                            var sub = allCategories.FirstOrDefault(c => c.ParentId == parent.Id &&
                                string.Equals(encryption.Decrypt(c.NameEncrypted, dek), subName, StringComparison.OrdinalIgnoreCase));
                            if (sub is null)
                            {
                                sub = new Category { UserId = userId, ParentId = parent.Id, NameEncrypted = encryption.Encrypt(subName, dek)! };
                                db.Categories.Add(sub);
                                await db.SaveChangesAsync();
                            }
                            categoryId = sub.Id;
                        }
                        else
                        {
                            categoryId = parent.Id;
                        }
                    }

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

                    // Detect a matching transfer leg: same date, opposite-sign amount,
                    // matching memo, in a different account, not already linked.
                    var match = await FindTransferMatchAsync(userId, dek, rowAccountId, date, amount, row.Memo, openBatchTransactions, claimedExistingIds);
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
                    imported++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Error on row {row.Date} {row.Payee}: {ex.Message}");
                }
            }

            // First save assigns real IDs to every new row (including both legs of
            // in-file transfer pairs), then cross-link transfer pairs by ID.
            await db.SaveChangesAsync();

            foreach (var (newTx, match) in pendingLinks)
            {
                newTx.TransferTransactionId = match.Id;
                match.TransferTransactionId = newTx.Id;
            }

            if (pendingLinks.Count > 0)
                await db.SaveChangesAsync();

            await dbTx.CommitAsync();
        }

        return Ok(new { imported, transfersLinked = pendingLinks.Count, errors = errors.Count > 0 ? errors : null });
    }

    // ── Helpers ──

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

    private static List<CsvRow> ParseCsv(IFormFile file)
    {
        using var stream = file.OpenReadStream();
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

    // ── QIF (loose) ──
    //
    // Tolerant QIF parser: skips/ignores anything it doesn't understand rather
    // than failing the whole file. Supports the common Money-Sunset export
    // shapes — a single !Type:Bank/CCard/Cash/Oth A/Oth L section with no
    // embedded account (the caller supplies accountId), and multi-account
    // exports using !Account blocks. !Type:Invst (investment) sections are
    // skipped with a warning since this app has no security/quantity model.

    private static (List<CsvRow> Rows, List<string> Warnings) ParseQif(IFormFile file)
    {
        using var stream = file.OpenReadStream();
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

        void FlushRecord()
        {
            if (current is null) return;
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
                default:
                    // Loosely ignore unsupported field codes (A address, S/E/$ splits, etc.)
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
