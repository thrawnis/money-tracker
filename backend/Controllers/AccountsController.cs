using System.Security.Claims;
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
[Route("api/[controller]")]
public class AccountsController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager,
    IAuditService audit,
    IAccountBackupService accountBackups) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private object MapAccount(Account a, string dek) => new
    {
        id            = a.Id,
        name          = a.Name,
        type          = a.Type,
        openingBalance = a.OpeningBalance,
        institutionId = a.InstitutionId,
        institution   = a.Institution is null ? null : new { a.Institution.Id, a.Institution.Name },
        accountNumber = encryption.Decrypt(a.AccountNumberEncrypted, dek),
        notes         = encryption.Decrypt(a.NotesEncrypted, dek),
        isActive      = a.IsActive,
        createdAt     = a.CreatedAt,
    };

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool includeInactive = false)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var accounts = await db.Accounts
            .Where(a => a.UserId == userId && (includeInactive || a.IsActive))
            .Include(a => a.Institution)
            .OrderBy(a => a.Name)
            .ToListAsync();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var accountIds = accounts.Select(a => a.Id).ToList();

        var txSums = await db.Transactions
            .Where(t => accountIds.Contains(t.AccountId) && t.Date <= today)
            .GroupBy(t => t.AccountId)
            .Select(g => new { AccountId = g.Key, Sum = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Sum);

        var lastTxDates = await db.Transactions
            .Where(t => accountIds.Contains(t.AccountId))
            .GroupBy(t => t.AccountId)
            .Select(g => new { AccountId = g.Key, Last = g.Max(t => t.Date) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Last);

        var txCounts = await db.Transactions
            .Where(t => accountIds.Contains(t.AccountId))
            .GroupBy(t => t.AccountId)
            .Select(g => new { AccountId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.Count);

        return Ok(accounts.Select(a =>
        {
            var txSum = txSums.TryGetValue(a.Id, out var s) ? s : 0m;
            var currentBalance = a.OpeningBalance + txSum;
            return new
            {
                id            = a.Id,
                name          = a.Name,
                type          = a.Type,
                openingBalance = a.OpeningBalance,
                currentBalance,
                lastTransactionDate = lastTxDates.TryGetValue(a.Id, out var d) ? d : (DateOnly?)null,
                transactionCount = txCounts.TryGetValue(a.Id, out var c) ? c : 0,
                institutionId = a.InstitutionId,
                institution   = a.Institution is null ? null : new { a.Institution.Id, a.Institution.Name },
                accountNumber = encryption.Decrypt(a.AccountNumberEncrypted, user.EncryptedDataKey),
                notes         = encryption.Decrypt(a.NotesEncrypted, user.EncryptedDataKey),
                isActive      = a.IsActive,
                createdAt     = a.CreatedAt,
            };
        }));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var account = await db.Accounts
            .Include(a => a.Institution)
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId);
        if (account is null) return NotFound();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        return Ok(MapAccount(account, user.EncryptedDataKey));
    }

    [HttpPost]
    public async Task<IActionResult> Create(AccountDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var duplicate = await db.Accounts.AnyAsync(a =>
            a.UserId == userId && a.Name.ToLower() == dto.Name.ToLower());
        if (duplicate)
            return Conflict(new { message = $"An account named \"{dto.Name}\" already exists." });

        var account = new Account
        {
            UserId                 = userId,
            Name                   = dto.Name,
            Type                   = dto.Type,
            OpeningBalance         = dto.OpeningBalance,
            InstitutionId          = dto.InstitutionId,
            AccountNumberEncrypted = encryption.Encrypt(dto.AccountNumber, user.EncryptedDataKey),
            NotesEncrypted         = encryption.Encrypt(dto.Notes, user.EncryptedDataKey),
            IsActive               = dto.IsActive,
            CreatedAt              = DateTime.UtcNow,
        };

        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        await db.Entry(account).Reference(a => a.Institution).LoadAsync();

        await audit.LogAsync("CREATE", "Account", account.Id, new { name = account.Name, type = account.Type.ToString() });

        return CreatedAtAction(nameof(GetById), new { id = account.Id },
            MapAccount(account, user.EncryptedDataKey));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, AccountDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId);
        if (account is null) return NotFound();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var duplicate = await db.Accounts.AnyAsync(a =>
            a.UserId == userId && a.Id != id && a.Name.ToLower() == dto.Name.ToLower());
        if (duplicate)
            return Conflict(new { message = $"An account named \"{dto.Name}\" already exists." });

        var oldName = account.Name;
        account.Name                   = dto.Name;
        account.Type                   = dto.Type;
        account.InstitutionId          = dto.InstitutionId;
        account.AccountNumberEncrypted = encryption.Encrypt(dto.AccountNumber, user.EncryptedDataKey);
        account.NotesEncrypted         = encryption.Encrypt(dto.Notes, user.EncryptedDataKey);
        account.IsActive               = dto.IsActive;

        await db.SaveChangesAsync();
        await db.Entry(account).Reference(a => a.Institution).LoadAsync();

        await audit.LogAsync("UPDATE", "Account", id, new { before = oldName, after = dto.Name, type = dto.Type.ToString() });

        return Ok(MapAccount(account, user.EncryptedDataKey));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] string? note)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var account = await db.Accounts
            .Include(a => a.Institution)
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId);
        if (account is null) return NotFound();

        var transactions = await db.Transactions
            .Where(t => t.AccountId == id)
            .Include(t => t.Payee)
            .Include(t => t.Category)
            .Include(t => t.Splits).ThenInclude(s => s.Category)
            .ToListAsync();

        // Full backup first — before anything is touched — so a failure past
        // this point can never leave the account deleted with no backup.
        var backupFile = await accountBackups.CreateBackupAsync(account, transactions, note);

        await using (var dbTx = await db.Database.BeginTransactionAsync())
        {
            // Any transaction in another account whose transfer partner lives in
            // the account being deleted becomes a plain (unlinked) transaction
            // instead of pointing at a row that's about to be cascade-deleted.
            var accountTxIds = transactions.Select(t => t.Id).ToList();
            if (accountTxIds.Count > 0)
            {
                var linkedElsewhere = await db.Transactions
                    .Where(t => t.AccountId != id && t.TransferTransactionId != null && accountTxIds.Contains(t.TransferTransactionId.Value))
                    .ToListAsync();
                foreach (var linked in linkedElsewhere)
                {
                    linked.TransferTransactionId = null;
                    linked.TransferAccountId = null;
                }
                if (linkedElsewhere.Count > 0) await db.SaveChangesAsync();
            }

            // Cascades to this account's Transactions (+ their Splits) and
            // ScheduledTransactions at the database level.
            db.Accounts.Remove(account);
            await db.SaveChangesAsync();

            await dbTx.CommitAsync();
        }

        await audit.LogAsync("DELETE", "Account", id, new { name = account.Name, backupFile });

        return NoContent();
    }
}

public record AccountDto(
    string Name,
    AccountType Type,
    decimal OpeningBalance,
    int? InstitutionId,
    string? AccountNumber,
    string? Notes,
    bool IsActive = true);
