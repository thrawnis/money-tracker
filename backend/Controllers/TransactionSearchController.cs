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
[Route("api/transactions")]
public class TransactionSearchController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static Regex BuildPattern(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        return new Regex(escaped, RegexOptions.IgnoreCase);
    }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] int?    categoryId,
        [FromQuery] int?    payeeId,
        [FromQuery] string? payeeName,
        [FromQuery] string? memo,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int     page     = 1,
        [FromQuery] int     pageSize = 50)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        var query = db.Transactions
            .Where(t => t.Account.UserId == userId)
            .Include(t => t.Payee)
            .Include(t => t.Category)
            .Include(t => t.Account)
            .AsQueryable();

        if (categoryId.HasValue)
            query = query.Where(t => t.CategoryId == categoryId.Value || t.Category!.ParentId == categoryId.Value);
        if (payeeId.HasValue)
            query = query.Where(t => t.PayeeId == payeeId.Value);
        if (from.HasValue) query = query.Where(t => t.Date >= from.Value);
        if (to.HasValue)   query = query.Where(t => t.Date <= to.Value);

        var loaded = await query
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .ToListAsync();

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

        var total = loaded.Count;
        var items = loaded
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new
            {
                id          = t.Id,
                accountId   = t.AccountId,
                accountName = encryption.Decrypt(t.Account.Name, dek),
                date        = t.Date,
                payee       = t.Payee is null ? null : encryption.Decrypt(t.Payee.NameEncrypted, dek),
                category    = t.Category is null ? null : encryption.Decrypt(t.Category.NameEncrypted, dek),
                categoryParentId = t.Category?.ParentId,
                memo        = encryption.Decrypt(t.MemoEncrypted, dek),
                amount      = t.Amount,
                status      = t.Status,
            })
            .ToList();

        return Ok(new { total, page, pageSize, items });
    }
}
