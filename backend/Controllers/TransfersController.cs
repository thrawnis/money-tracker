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
[Route("api/transfers")]
public class TransfersController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager,
    IAuditService audit) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    [HttpPost]
    public async Task<IActionResult> Create(TransferDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        if (dto.SourceAccountId == dto.DestinationAccountId)
            return BadRequest("Source and destination accounts must be different.");

        if (dto.Amount == 0)
            return BadRequest("Transfer amount must be non-zero.");

        var source = await db.Accounts.FirstOrDefaultAsync(a => a.Id == dto.SourceAccountId && a.UserId == userId);
        if (source is null) return NotFound("Source account not found.");

        var dest = await db.Accounts.FirstOrDefaultAsync(a => a.Id == dto.DestinationAccountId && a.UserId == userId);
        if (dest is null) return NotFound("Destination account not found.");

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var amount = Math.Abs(dto.Amount);
        var dek = user.EncryptedDataKey;
        var memoEnc = encryption.Encrypt(dto.Memo, dek);

        var debit = new Transaction
        {
            AccountId         = dto.SourceAccountId,
            Date              = dto.Date,
            Amount            = -amount,
            Status            = TransactionStatus.Uncleared,
            TransferAccountId = dto.DestinationAccountId,
            MemoEncrypted     = memoEnc,
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow,
        };

        var credit = new Transaction
        {
            AccountId         = dto.DestinationAccountId,
            Date              = dto.Date,
            PostDate          = dto.PostDate,
            Amount            = amount,
            Status            = TransactionStatus.Uncleared,
            TransferAccountId = dto.SourceAccountId,
            MemoEncrypted     = memoEnc,
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow,
        };

        // Insert + cross-link atomically so a crash can't leave unlinked halves
        await using (var dbTx = await db.Database.BeginTransactionAsync())
        {
            db.Transactions.Add(debit);
            db.Transactions.Add(credit);
            await db.SaveChangesAsync();

            debit.TransferTransactionId  = credit.Id;
            credit.TransferTransactionId = debit.Id;
            await db.SaveChangesAsync();

            await dbTx.CommitAsync();
        }

        await audit.LogAsync("CREATE", "Transfer", debit.Id, new
        {
            sourceAccountId      = dto.SourceAccountId,
            destinationAccountId = dto.DestinationAccountId,
            amount,
            date = dto.Date,
        });

        return Ok(new
        {
            debit  = MapTx(debit,  dek, source.Name, dest.Name),
            credit = MapTx(credit, dek, dest.Name,   source.Name),
        });
    }

    private object MapTx(Transaction tx, string dek, string thisAccountName, string otherAccountName) => new
    {
        id                    = tx.Id,
        accountId             = tx.AccountId,
        date                  = tx.Date,
        postDate              = tx.PostDate,
        checkNumber           = (string?)null,
        payeeId               = (int?)null,
        payee                 = (object?)null,
        categoryId            = (int?)null,
        category              = (object?)null,
        memo                  = encryption.Decrypt(tx.MemoEncrypted, dek),
        amount                = tx.Amount,
        status                = tx.Status,
        transferTransactionId = tx.TransferTransactionId,
        transferAccountId     = tx.TransferAccountId,
        createdAt             = tx.CreatedAt,
        updatedAt             = tx.UpdatedAt,
    };
}

public record TransferDto(
    int       SourceAccountId,
    int       DestinationAccountId,
    DateOnly  Date,
    DateOnly? PostDate,
    decimal   Amount,
    string?   Memo);
