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
    public async Task<IActionResult> Preview(IFormFile file)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is ".qif" or ".ofx" or ".qfx")
            return Ok(new { total = 0, duplicates = Array.Empty<object>(), newTransactions = 0, transferMatches = 0, error = "QIF/OFX import coming soon" });

        if (ext is not ".csv" and not ".xlsx")
            return BadRequest(new { message = "Unsupported file format." });

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        var userAccounts = await db.Accounts
            .Where(a => a.UserId == userId && a.IsActive)
            .ToListAsync();

        List<CsvRow> rows;
        try
        {
            rows = ParseCsv(file);
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
            if (!TryParseRow(row, userAccounts, out var date, out var amount, out var accountId, out _))
            {
                newCount++;
                continue;
            }

            var existing = await db.Transactions
                .Include(t => t.Payee)
                .Where(t => t.AccountId == accountId && t.Date == date && t.Amount == amount)
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
                var match = await FindTransferMatchAsync(userId, dek, accountId, date, amount, row.Memo, [], claimedTransferIds);
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
        });
    }

    // ── Import ──

    [HttpPost]
    public async Task<IActionResult> Import(
        [FromForm] IFormFile file,
        [FromForm] string? includeDuplicateIds)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is ".qif" or ".ofx" or ".qfx")
            return Ok(new { imported = 0, errors = new[] { "QIF/OFX import coming soon" } });

        if (ext is not ".csv" and not ".xlsx")
            return BadRequest(new { message = "Unsupported file format." });

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        var userAccounts = await db.Accounts
            .Where(a => a.UserId == userId && a.IsActive)
            .ToListAsync();

        HashSet<int> skipIds = [];
        if (!string.IsNullOrWhiteSpace(includeDuplicateIds))
        {
            // includeDuplicateIds is the list of IDs to INCLUDE (not skip)
            // but we need to figure out which duplicates to skip
            // The IDs passed are the ones the user chose to INCLUDE
        }

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
        try
        {
            rows = ParseCsv(file);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Failed to parse file: {ex.Message}" });
        }

        var imported = 0;
        var errors = new List<string>();

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
                    if (!TryParseRow(row, userAccounts, out var date, out var amount, out var accountId, out var status))
                    {
                        errors.Add($"Could not parse row: {row.Date} {row.Payee} {row.Amount}");
                        continue;
                    }

                    // Check for duplicate
                    var existing = await db.Transactions
                        .Where(t => t.AccountId == accountId && t.Date == date && t.Amount == amount)
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

                    var tx = new Transaction
                    {
                        AccountId = accountId,
                        Date = date,
                        Amount = amount,
                        PayeeId = payeeId,
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
                    var match = await FindTransferMatchAsync(userId, dek, accountId, date, amount, row.Memo, openBatchTransactions, claimedExistingIds);
                    if (match is not null)
                    {
                        tx.TransferAccountId = match.AccountId;
                        match.TransferAccountId = accountId;
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
        out TransactionStatus status)
    {
        date = default;
        amount = 0;
        accountId = 0;
        status = TransactionStatus.Uncleared;

        if (!DateOnly.TryParse(row.Date, out date)) return false;
        if (!decimal.TryParse(row.Amount, NumberStyles.Any, CultureInfo.InvariantCulture, out amount)) return false;

        var acct = userAccounts.FirstOrDefault(a =>
            string.Equals(a.Name, row.Account?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (acct is null) return false;
        accountId = acct.Id;

        status = row.Status?.Trim().ToLowerInvariant() switch
        {
            "c" or "cleared" => TransactionStatus.Cleared,
            "r" or "reconciled" => TransactionStatus.Reconciled,
            _ => TransactionStatus.Uncleared,
        };

        return true;
    }
}
