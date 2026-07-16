using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Controllers;

/// <summary>
/// Helps a user find and clean up likely-duplicate transactions — e.g. left over
/// from a bad import, or a file that was accidentally imported twice.
/// </summary>
[ApiController]
[Authorize]
[Route("api/duplicates")]
public class DuplicatesController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// Groups the user's transactions by (account, date, amount) and returns only
    /// the groups with more than one transaction. Transfer legs are excluded: two
    /// linked transfer legs naturally share date/amount across different accounts
    /// and aren't duplicates.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? accountId)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        var accounts = await db.Accounts
            .Where(a => a.UserId == userId && (accountId == null || a.Id == accountId))
            .ToDictionaryAsync(a => a.Id, a => a.Name);

        if (accountId.HasValue && !accounts.ContainsKey(accountId.Value))
            return NotFound();

        var candidates = await db.Transactions
            .Where(t => accounts.Keys.Contains(t.AccountId) && t.TransferTransactionId == null)
            .Include(t => t.Payee)
            .Include(t => t.Category)
            .ToListAsync();

        var groups = candidates
            .GroupBy(t => (t.AccountId, t.Date, t.Amount))
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Key.Date)
            .Select(g => new
            {
                accountId   = g.Key.AccountId,
                accountName = accounts[g.Key.AccountId],
                date        = g.Key.Date,
                amount      = g.Key.Amount,
                transactions = g
                    .OrderBy(t => t.Id)
                    .Select(t => new
                    {
                        id          = t.Id,
                        payee       = t.Payee is null ? null : encryption.Decrypt(t.Payee.NameEncrypted, dek),
                        category    = t.Category is null ? null : encryption.Decrypt(t.Category.NameEncrypted, dek),
                        memo        = encryption.Decrypt(t.MemoEncrypted, dek),
                        checkNumber = encryption.Decrypt(t.CheckNumberEncrypted, dek),
                        status      = t.Status,
                        createdAt   = t.CreatedAt,
                    }),
            });

        return Ok(groups);
    }
}
