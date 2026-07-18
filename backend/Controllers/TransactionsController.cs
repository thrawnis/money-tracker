using System.Security.Claims;
using System.Text.RegularExpressions;
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
[Route("api/accounts/{accountId}/transactions")]
public class TransactionsController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager,
    IAuditService audit) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private async Task<bool> AccountBelongsToUser(int accountId, string userId) =>
        await db.Accounts.AnyAsync(a => a.Id == accountId && a.UserId == userId);

    /// <summary>
    /// Verifies that all FK references in the DTO belong to the calling user,
    /// and that Splits (if present) are internally consistent.
    /// Returns an error message, or null when everything checks out.
    /// </summary>
    private async Task<string?> ValidateReferences(TransactionDto dto, string userId)
    {
        if (dto.PayeeId.HasValue &&
            !await db.Payees.AnyAsync(p => p.Id == dto.PayeeId.Value && p.UserId == userId))
            return "Payee not found.";

        if (dto.CategoryId.HasValue &&
            !await db.Categories.AnyAsync(c => c.Id == dto.CategoryId.Value && c.UserId == userId))
            return "Category not found.";

        if (dto.TransferTransactionId.HasValue &&
            !await db.Transactions.AnyAsync(t => t.Id == dto.TransferTransactionId.Value && t.Account.UserId == userId))
            return "Linked transfer transaction not found.";

        if (dto.Splits is { Count: > 0 })
        {
            if (dto.TransferTransactionId.HasValue)
                return "Splits are not supported on transfer transactions.";

            foreach (var split in dto.Splits)
            {
                if (split.CategoryId.HasValue &&
                    !await db.Categories.AnyAsync(c => c.Id == split.CategoryId.Value && c.UserId == userId))
                    return "Split category not found.";
            }

            if (dto.Splits.Sum(s => s.Amount) != dto.Amount)
                return "Split amounts must add up to the transaction total.";
        }

        return null;
    }

    private object MapTransaction(Transaction tx, string dek, decimal? runningBalance = null) => new
    {
        id                    = tx.Id,
        accountId             = tx.AccountId,
        date                  = tx.Date,
        postDate              = tx.PostDate,
        checkNumber           = encryption.Decrypt(tx.CheckNumberEncrypted, dek),
        payeeId               = tx.PayeeId,
        payee                 = tx.Payee is null ? null : new
        {
            id   = tx.Payee.Id,
            name = encryption.Decrypt(tx.Payee.NameEncrypted, dek),
        },
        categoryId            = tx.CategoryId,
        category              = tx.Category is null ? null : new
        {
            id       = tx.Category.Id,
            name     = encryption.Decrypt(tx.Category.NameEncrypted, dek),
            parentId = tx.Category.ParentId,
        },
        memo                  = encryption.Decrypt(tx.MemoEncrypted, dek),
        amount                = tx.Amount,
        status                = tx.Status,
        transferTransactionId = tx.TransferTransactionId,
        transferAccountId     = tx.TransferAccountId,
        splits                = tx.Splits.Count == 0 ? null : tx.Splits.Select(s => new
        {
            id         = s.Id,
            categoryId = s.CategoryId,
            category   = s.Category is null ? null : new
            {
                id       = s.Category.Id,
                name     = encryption.Decrypt(s.Category.NameEncrypted, dek),
                parentId = s.Category.ParentId,
            },
            amount = s.Amount,
            memo   = encryption.Decrypt(s.MemoEncrypted, dek),
        }),
        runningBalance,
        createdAt             = tx.CreatedAt,
        updatedAt             = tx.UpdatedAt,
    };

    [HttpGet]
    public async Task<IActionResult> GetByAccount(
        int accountId,
        [FromQuery] DateOnly?  from,
        [FromQuery] DateOnly?  to,
        [FromQuery] decimal?   amountMin,
        [FromQuery] decimal?   amountMax,
        [FromQuery] int?       payeeId,
        [FromQuery] string?    payeeName,     // wildcard: * and ? supported
        [FromQuery] int?       categoryId,    // matches category or any child subcategory
        [FromQuery] string?    memo,          // wildcard
        [FromQuery] string?    checkNumber,   // wildcard
        [FromQuery] bool?      uncategorized, // true = no category assigned
        [FromQuery] string     sortBy  = "date",
        [FromQuery] string     sortDir = "desc",
        [FromQuery] int        page     = 1,
        [FromQuery] int        pageSize = 100)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        if (!await AccountBelongsToUser(accountId, userId)) return NotFound();

        // page=0 would make Skip() throw (500); clamp instead of erroring.
        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        // ── SQL filters (plaintext columns) ───────────────────────────────────
        var query = db.Transactions
            .Where(t => t.AccountId == accountId)
            .Include(t => t.Payee)
            .Include(t => t.Category)
            .Include(t => t.Splits).ThenInclude(s => s.Category)
            .AsQueryable();

        if (from.HasValue)        query = query.Where(t => t.Date >= from.Value);
        if (to.HasValue)          query = query.Where(t => t.Date <= to.Value);
        if (amountMin.HasValue)   query = query.Where(t => t.Amount >= amountMin.Value);
        if (amountMax.HasValue)   query = query.Where(t => t.Amount <= amountMax.Value);
        if (payeeId.HasValue)     query = query.Where(t => t.PayeeId == payeeId.Value);
        if (categoryId.HasValue)  query = query.Where(t =>
            t.CategoryId == categoryId.Value || t.Category!.ParentId == categoryId.Value ||
            t.Splits.Any(s => s.CategoryId == categoryId.Value || s.Category!.ParentId == categoryId.Value));
        if (uncategorized == true) query = query.Where(t => t.CategoryId == null && !t.Splits.Any());

        // Fetch into memory — needed for encrypted-field filtering and flexible sort
        var loaded = await query.ToListAsync();

        // ── In-memory filters (encrypted columns) ─────────────────────────────
        if (!string.IsNullOrWhiteSpace(payeeName))
        {
            var rx = BuildPattern(payeeName);
            loaded = loaded.Where(t =>
                t.Payee is not null &&
                rx.IsMatch(encryption.Decrypt(t.Payee.NameEncrypted, dek) ?? "")).ToList();
        }

        if (!string.IsNullOrWhiteSpace(memo))
        {
            var rx = BuildPattern(memo);
            loaded = loaded.Where(t =>
                rx.IsMatch(encryption.Decrypt(t.MemoEncrypted, dek) ?? "")).ToList();
        }

        if (!string.IsNullOrWhiteSpace(checkNumber))
        {
            var rx = BuildPattern(checkNumber);
            loaded = loaded.Where(t =>
                rx.IsMatch(encryption.Decrypt(t.CheckNumberEncrypted, dek) ?? "")).ToList();
        }

        // ── Sort ──────────────────────────────────────────────────────────────
        bool asc = sortDir.Equals("asc", StringComparison.OrdinalIgnoreCase);
        loaded = sortBy.ToLowerInvariant() switch
        {
            "payee"    => asc
                ? loaded.OrderBy(t => encryption.Decrypt(t.Payee?.NameEncrypted, dek)).ThenBy(t => t.CreatedAt).ToList()
                : loaded.OrderByDescending(t => encryption.Decrypt(t.Payee?.NameEncrypted, dek)).ThenByDescending(t => t.CreatedAt).ToList(),
            "category" => asc
                ? loaded.OrderBy(t => encryption.Decrypt(t.Category?.NameEncrypted, dek)).ThenBy(t => t.CreatedAt).ToList()
                : loaded.OrderByDescending(t => encryption.Decrypt(t.Category?.NameEncrypted, dek)).ThenByDescending(t => t.CreatedAt).ToList(),
            "memo"     => asc
                ? loaded.OrderBy(t => encryption.Decrypt(t.MemoEncrypted, dek)).ThenBy(t => t.CreatedAt).ToList()
                : loaded.OrderByDescending(t => encryption.Decrypt(t.MemoEncrypted, dek)).ThenByDescending(t => t.CreatedAt).ToList(),
            "amount"   => asc
                ? loaded.OrderBy(t => t.Amount).ThenBy(t => t.CreatedAt).ToList()
                : loaded.OrderByDescending(t => t.Amount).ThenByDescending(t => t.CreatedAt).ToList(),
            "status"   => asc
                ? loaded.OrderBy(t => t.Status).ThenBy(t => t.CreatedAt).ToList()
                : loaded.OrderByDescending(t => t.Status).ThenByDescending(t => t.CreatedAt).ToList(),
            _          => asc  // "date" (default)
                // Id tie-break matches the running-balance window function's
                // ORDER BY exactly, so the Balance column reads monotonically
                // even when rows share both effective date and CreatedAt.
                ? loaded.OrderBy(t => t.PostDate ?? t.Date).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id).ToList()
                : loaded.OrderByDescending(t => t.PostDate ?? t.Date).ThenByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).ToList(),
        };

        // ── Running balances ──────────────────────────────────────────────────
        // Always computed over the FULL account history in effective-date order,
        // independent of active filters, sort, or pagination — so each row shows
        // its true historical balance and the header total is always correct.
        //
        // Computed as a cumulative SUM window function in Postgres rather than a
        // foreach loop in .NET: the database can execute this far more
        // efficiently than pulling every row into app memory and summing there,
        // which matters once an account's history grows into the thousands+.
        // {accountId} is interpolated into a FormattableString, so EF parameterizes
        // it (not string-concatenated) — safe from SQL injection.
        var openingBalance = await db.Accounts
            .Where(a => a.Id == accountId)
            .Select(a => a.OpeningBalance)
            .FirstAsync();

        var balanceRows = await db.Database.SqlQuery<BalanceRow>($"""
            SELECT
                t."Id" AS "Id",
                a."OpeningBalance" + SUM(t."Amount") OVER (
                    ORDER BY COALESCE(t."PostDate", t."Date"), t."CreatedAt", t."Id"
                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                ) AS "RunningBalance",
                COALESCE(t."PostDate", t."Date") AS "EffectiveDate"
            FROM "Transactions" t
            JOIN "Accounts" a ON a."Id" = t."AccountId"
            WHERE t."AccountId" = {accountId}
            ORDER BY COALESCE(t."PostDate", t."Date"), t."CreatedAt", t."Id"
            """).ToListAsync();

        var balances = balanceRows.ToDictionary(r => r.Id, r => r.RunningBalance);

        // Header balance is "as of today" — same rule as the Accounts page and
        // Dashboard, so all three always agree. Rows dated in the future still
        // get running balances (their projected value), they just don't count
        // toward the headline number. balanceRows is already ordered ascending,
        // so the last row on or before today is the correct "as of today" value.
        var balanceToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var currentBalance = balanceRows
            .Where(r => r.EffectiveDate <= balanceToday)
            .Select(r => (decimal?)r.RunningBalance)
            .LastOrDefault() ?? openingBalance;

        var total = loaded.Count;
        var items = loaded
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => MapTransaction(t, dek, balances.GetValueOrDefault(t.Id)))
            .ToList();

        return Ok(new { total, page, pageSize, currentBalance, items });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int accountId, int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        if (!await AccountBelongsToUser(accountId, userId)) return NotFound();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var tx = await db.Transactions
            .Include(t => t.Payee)
            .Include(t => t.Category)
            .Include(t => t.Splits).ThenInclude(s => s.Category)
            .FirstOrDefaultAsync(t => t.Id == id && t.AccountId == accountId);

        return tx is null ? NotFound() : Ok(MapTransaction(tx, user.EncryptedDataKey));
    }

    [HttpPost]
    public async Task<IActionResult> Create(int accountId, TransactionDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        if (!await AccountBelongsToUser(accountId, userId)) return NotFound();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        if (await ValidateReferences(dto, userId) is string refError)
            return BadRequest(new { message = refError });

        bool hasSplits = dto.Splits is { Count: > 0 };

        var tx = new Transaction
        {
            AccountId             = accountId,
            Date                  = dto.Date,
            PostDate              = dto.PostDate,
            CheckNumberEncrypted  = encryption.Encrypt(dto.CheckNumber, user.EncryptedDataKey),
            PayeeId               = dto.PayeeId,
            CategoryId            = hasSplits ? null : dto.CategoryId,
            MemoEncrypted         = encryption.Encrypt(dto.Memo, user.EncryptedDataKey),
            Amount                = dto.Amount,
            Status                = dto.Status,
            TransferTransactionId = dto.TransferTransactionId,
            CreatedAt             = DateTime.UtcNow,
            UpdatedAt             = DateTime.UtcNow,
        };

        await using (var dbTx = await db.Database.BeginTransactionAsync())
        {
            db.Transactions.Add(tx);
            await db.SaveChangesAsync();

            if (hasSplits)
            {
                foreach (var split in dto.Splits!)
                {
                    db.TransactionSplits.Add(new TransactionSplit
                    {
                        TransactionId = tx.Id,
                        CategoryId    = split.CategoryId,
                        Amount        = split.Amount,
                        MemoEncrypted = encryption.Encrypt(split.Memo, user.EncryptedDataKey),
                    });
                }
                await db.SaveChangesAsync();
            }

            await dbTx.CommitAsync();
        }

        await db.Entry(tx).Reference(t => t.Payee).LoadAsync();
        await db.Entry(tx).Reference(t => t.Category).LoadAsync();
        await db.Entry(tx).Collection(t => t.Splits).Query().Include(s => s.Category).LoadAsync();

        var mapped = MapTransaction(tx, user.EncryptedDataKey);
        await audit.LogAsync("CREATE", "Transaction", tx.Id, new { accountId, date = tx.Date, amount = tx.Amount });
        return CreatedAtAction(nameof(GetById), new { accountId, id = tx.Id }, mapped);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int accountId, int id, TransactionDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        if (!await AccountBelongsToUser(accountId, userId)) return NotFound();

        var tx = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.AccountId == accountId);
        if (tx is null) return NotFound();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        if (await ValidateReferences(dto, userId) is string refError)
            return BadRequest(new { message = refError });

        bool hasSplits = dto.Splits is { Count: > 0 };

        // ValidateReferences only sees dto.TransferTransactionId, which is null on
        // edits of an existing transfer (the link lives on the tracked entity) —
        // re-check here or a transfer leg could acquire splits.
        if (hasSplits && tx.TransferTransactionId.HasValue)
            return BadRequest(new { message = "Transfer transactions cannot be split." });

        // The linked leg (if any) is needed both for validation and the sync below.
        Transaction? linked = tx.TransferTransactionId.HasValue
            ? await db.Transactions.FindAsync(tx.TransferTransactionId.Value)
            : null;

        int? previousPayeeId = tx.PayeeId != dto.PayeeId ? tx.PayeeId : null;
        bool accountChanged = false;

        if (dto.TargetAccountId.HasValue && dto.TargetAccountId.Value != accountId)
        {
            if (!await AccountBelongsToUser(dto.TargetAccountId.Value, userId)) return BadRequest("Target account not found.");
            if (linked is not null && dto.TargetAccountId.Value == linked.AccountId)
                return BadRequest(new { message = "Both sides of a transfer cannot be in the same account." });
            tx.AccountId = dto.TargetAccountId.Value;
            accountChanged = true;
        }

        tx.Date                 = dto.Date;
        tx.PostDate             = dto.PostDate;
        tx.CheckNumberEncrypted = encryption.Encrypt(dto.CheckNumber, user.EncryptedDataKey);
        tx.PayeeId              = dto.PayeeId;
        tx.CategoryId           = hasSplits ? null : dto.CategoryId;
        tx.MemoEncrypted        = encryption.Encrypt(dto.Memo, user.EncryptedDataKey);
        tx.Amount               = dto.Amount;
        tx.Status               = dto.Status;
        tx.UpdatedAt            = DateTime.UtcNow;

        // Apply the edit, linked-transfer sync, split replacement, and payee
        // cleanup atomically
        await using (var dbTx = await db.Database.BeginTransactionAsync())
        {
            // Sync linked transfer transaction (amount, date, and memo mirror;
            // PostDate stays on the credit side only, and Status deliberately
            // does NOT mirror — cleared/reconciled is per-account state, each
            // account reconciles against its own statement). If this leg moved
            // to a different account, the linked leg's TransferAccountId must
            // follow so the pair still points at each other's current accounts.
            if (linked is not null)
            {
                linked.Amount        = -tx.Amount;
                linked.Date          = tx.Date;
                linked.MemoEncrypted = tx.MemoEncrypted;
                linked.UpdatedAt     = DateTime.UtcNow;
                if (accountChanged) linked.TransferAccountId = tx.AccountId;
            }

            // Replace the split set wholesale: simpler and safer than diffing,
            // and the whole edit is one atomic save from the client's point of view.
            var existingSplits = await db.TransactionSplits.Where(s => s.TransactionId == id).ToListAsync();
            db.TransactionSplits.RemoveRange(existingSplits);
            if (hasSplits)
            {
                foreach (var split in dto.Splits!)
                {
                    db.TransactionSplits.Add(new TransactionSplit
                    {
                        TransactionId = id,
                        CategoryId    = split.CategoryId,
                        Amount        = split.Amount,
                        MemoEncrypted = encryption.Encrypt(split.Memo, user.EncryptedDataKey),
                    });
                }
            }

            await db.SaveChangesAsync();

            if (previousPayeeId.HasValue)
            {
                // "In use" includes scheduled transactions — their Payee FK is
                // SetNull on delete, so removing the payee would silently strip
                // it from every future auto-posted occurrence.
                bool payeeStillInUse = await db.Transactions.AnyAsync(t => t.PayeeId == previousPayeeId)
                    || await db.ScheduledTransactions.AnyAsync(s => s.PayeeId == previousPayeeId);
                if (!payeeStillInUse)
                {
                    var payee = await db.Payees.FindAsync(previousPayeeId.Value);
                    if (payee is not null) { db.Payees.Remove(payee); await db.SaveChangesAsync(); }
                }
            }

            await dbTx.CommitAsync();
        }

        await db.Entry(tx).Reference(t => t.Payee).LoadAsync();
        await db.Entry(tx).Reference(t => t.Category).LoadAsync();
        await db.Entry(tx).Collection(t => t.Splits).Query().Include(s => s.Category).LoadAsync();

        await audit.LogAsync("UPDATE", "Transaction", id, new { accountId = tx.AccountId, date = tx.Date, amount = tx.Amount });
        return Ok(MapTransaction(tx, user.EncryptedDataKey));
    }

    /// <summary>
    /// Status-only update. The register's cleared/reconciled toggle previously
    /// PUT a body containing only Status to the full Update endpoint — the other
    /// TransactionDto fields bound to their CLR defaults and silently wiped the
    /// row (date → 0001-01-01, amount → 0, payee/category/memo → null).
    /// </summary>
    [HttpPatch("{id}/status")]
    public async Task<IActionResult> UpdateStatus(int accountId, int id, StatusDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        if (!await AccountBelongsToUser(accountId, userId)) return NotFound();

        var tx = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.AccountId == accountId);
        if (tx is null) return NotFound();

        tx.Status    = dto.Status;
        tx.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await audit.LogAsync("UPDATE", "Transaction", id, new { accountId, status = dto.Status.ToString() });
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int accountId, int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        if (!await AccountBelongsToUser(accountId, userId)) return NotFound();

        var tx = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.AccountId == accountId);
        if (tx is null) return NotFound();

        int? payeeId = tx.PayeeId;
        int? linkedTransferId = tx.TransferTransactionId;

        // Delete the transaction, its transfer pair, and orphaned payee atomically
        await using (var dbTx = await db.Database.BeginTransactionAsync())
        {
            db.Transactions.Remove(tx);

            if (linkedTransferId.HasValue)
            {
                var linked = await db.Transactions.FindAsync(linkedTransferId.Value);
                if (linked is not null) db.Transactions.Remove(linked);
            }

            await db.SaveChangesAsync();

            if (payeeId.HasValue)
            {
                // Same scheduled-transaction guard as in Update above.
                bool payeeStillInUse = await db.Transactions.AnyAsync(t => t.PayeeId == payeeId)
                    || await db.ScheduledTransactions.AnyAsync(s => s.PayeeId == payeeId);
                if (!payeeStillInUse)
                {
                    var payee = await db.Payees.FindAsync(payeeId.Value);
                    if (payee is not null) { db.Payees.Remove(payee); await db.SaveChangesAsync(); }
                }
            }

            await dbTx.CommitAsync();
        }

        await audit.LogAsync("DELETE", "Transaction", id, new { accountId });
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a case-insensitive regex from a user pattern.
    /// * matches any sequence of characters; ? matches exactly one character.
    /// Plain text with no wildcards is treated as a substring (contains) search.
    /// </summary>
    private static Regex BuildPattern(string pattern)
    {
        bool hasWildcard = pattern.Contains('*') || pattern.Contains('?');
        string regexStr = hasWildcard
            ? "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$"
            : Regex.Escape(pattern); // substring match — IsMatch finds it anywhere

        return new Regex(regexStr, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}

public record TransactionDto(
    DateOnly          Date,
    DateOnly?         PostDate,
    string?           CheckNumber,
    int?              PayeeId,
    int?              CategoryId,
    string?           Memo,
    decimal           Amount,
    TransactionStatus Status,
    int?              TransferTransactionId,
    int?              TargetAccountId,
    List<SplitDto>?   Splits = null);

public record SplitDto(int? CategoryId, decimal Amount, string? Memo);
public record StatusDto(TransactionStatus Status);

// Keyless projection for the running-balance window-function query — has no
// corresponding entity/table, only used with Database.SqlQuery<BalanceRow>.
public record BalanceRow(int Id, decimal RunningBalance, DateOnly EffectiveDate);
