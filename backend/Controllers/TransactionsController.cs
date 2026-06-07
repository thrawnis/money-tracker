using System.Security.Claims;
using System.Text.RegularExpressions;
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
[Route("api/accounts/{accountId}/transactions")]
public class TransactionsController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private async Task<bool> AccountBelongsToUser(int accountId, string userId) =>
        await db.Accounts.AnyAsync(a => a.Id == accountId && a.UserId == userId);

    private object MapTransaction(Transaction tx, string dek) => new
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
        [FromQuery] int        page     = 1,
        [FromQuery] int        pageSize = 50)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        if (!await AccountBelongsToUser(accountId, userId)) return NotFound();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        // ── SQL filters (plaintext columns) ───────────────────────────────────
        var query = db.Transactions
            .Where(t => t.AccountId == accountId)
            .Include(t => t.Payee)
            .Include(t => t.Category)
            .AsQueryable();

        if (from.HasValue)        query = query.Where(t => t.Date >= from.Value);
        if (to.HasValue)          query = query.Where(t => t.Date <= to.Value);
        if (amountMin.HasValue)   query = query.Where(t => t.Amount >= amountMin.Value);
        if (amountMax.HasValue)   query = query.Where(t => t.Amount <= amountMax.Value);
        if (payeeId.HasValue)     query = query.Where(t => t.PayeeId == payeeId.Value);
        if (categoryId.HasValue)  query = query.Where(t =>
            t.CategoryId == categoryId.Value || t.Category!.ParentId == categoryId.Value);
        if (uncategorized == true) query = query.Where(t => t.CategoryId == null);

        // Fetch into memory — needed for encrypted-field filtering
        var loaded = await query
            .OrderByDescending(t => t.PostDate ?? t.Date)
            .ThenByDescending(t => t.CreatedAt)
            .ToListAsync();

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

        var total = loaded.Count;
        var items = loaded
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => MapTransaction(t, dek))
            .ToList();

        return Ok(new { total, page, pageSize, items });
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

        var tx = new Transaction
        {
            AccountId             = accountId,
            Date                  = dto.Date,
            PostDate              = dto.PostDate,
            CheckNumberEncrypted  = encryption.Encrypt(dto.CheckNumber, user.EncryptedDataKey),
            PayeeId               = dto.PayeeId,
            CategoryId            = dto.CategoryId,
            MemoEncrypted         = encryption.Encrypt(dto.Memo, user.EncryptedDataKey),
            Amount                = dto.Amount,
            Status                = dto.Status,
            TransferTransactionId = dto.TransferTransactionId,
            CreatedAt             = DateTime.UtcNow,
            UpdatedAt             = DateTime.UtcNow,
        };

        db.Transactions.Add(tx);
        await db.SaveChangesAsync();
        await db.Entry(tx).Reference(t => t.Payee).LoadAsync();
        await db.Entry(tx).Reference(t => t.Category).LoadAsync();

        return CreatedAtAction(nameof(GetById), new { accountId, id = tx.Id },
            MapTransaction(tx, user.EncryptedDataKey));
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

        int? previousPayeeId = tx.PayeeId != dto.PayeeId ? tx.PayeeId : null;

        if (dto.TargetAccountId.HasValue && dto.TargetAccountId.Value != accountId)
        {
            if (!await AccountBelongsToUser(dto.TargetAccountId.Value, userId)) return BadRequest("Target account not found.");
            tx.AccountId = dto.TargetAccountId.Value;
        }

        tx.Date                 = dto.Date;
        tx.PostDate             = dto.PostDate;
        tx.CheckNumberEncrypted = encryption.Encrypt(dto.CheckNumber, user.EncryptedDataKey);
        tx.PayeeId              = dto.PayeeId;
        tx.CategoryId           = dto.CategoryId;
        tx.MemoEncrypted        = encryption.Encrypt(dto.Memo, user.EncryptedDataKey);
        tx.Amount               = dto.Amount;
        tx.Status               = dto.Status;
        tx.UpdatedAt            = DateTime.UtcNow;

        await db.SaveChangesAsync();

        if (previousPayeeId.HasValue)
        {
            bool payeeStillInUse = await db.Transactions.AnyAsync(t => t.PayeeId == previousPayeeId);
            if (!payeeStillInUse)
            {
                var payee = await db.Payees.FindAsync(previousPayeeId.Value);
                if (payee is not null) { db.Payees.Remove(payee); await db.SaveChangesAsync(); }
            }
        }

        await db.Entry(tx).Reference(t => t.Payee).LoadAsync();
        await db.Entry(tx).Reference(t => t.Category).LoadAsync();

        return Ok(MapTransaction(tx, user.EncryptedDataKey));
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
        db.Transactions.Remove(tx);
        await db.SaveChangesAsync();

        if (payeeId.HasValue)
        {
            bool payeeStillInUse = await db.Transactions.AnyAsync(t => t.PayeeId == payeeId);
            if (!payeeStillInUse)
            {
                var payee = await db.Payees.FindAsync(payeeId.Value);
                if (payee is not null) { db.Payees.Remove(payee); await db.SaveChangesAsync(); }
            }
        }

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
    int?              TargetAccountId);
