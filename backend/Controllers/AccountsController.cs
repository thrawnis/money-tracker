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
    IAuditService audit) : ControllerBase
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

        var account = new Account
        {
            UserId                 = userId,
            Name                   = dto.Name,
            Type                   = dto.Type,
            OpeningBalance         = dto.OpeningBalance,
            InstitutionId          = dto.InstitutionId,
            AccountNumberEncrypted = encryption.Encrypt(dto.AccountNumber, user.EncryptedDataKey),
            NotesEncrypted         = encryption.Encrypt(dto.Notes, user.EncryptedDataKey),
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

        var oldName = account.Name;
        account.Name                   = dto.Name;
        account.Type                   = dto.Type;
        account.InstitutionId          = dto.InstitutionId;
        account.AccountNumberEncrypted = encryption.Encrypt(dto.AccountNumber, user.EncryptedDataKey);
        account.NotesEncrypted         = encryption.Encrypt(dto.Notes, user.EncryptedDataKey);

        await db.SaveChangesAsync();
        await db.Entry(account).Reference(a => a.Institution).LoadAsync();

        await audit.LogAsync("UPDATE", "Account", id, new { before = oldName, after = dto.Name, type = dto.Type.ToString() });

        return Ok(MapAccount(account, user.EncryptedDataKey));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Deactivate(int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId);
        if (account is null) return NotFound();

        account.IsActive = false;
        await db.SaveChangesAsync();

        await audit.LogAsync("DELETE", "Account", id, new { name = account.Name });

        return NoContent();
    }
}

public record AccountDto(
    string Name,
    AccountType Type,
    decimal OpeningBalance,
    int? InstitutionId,
    string? AccountNumber,
    string? Notes);
