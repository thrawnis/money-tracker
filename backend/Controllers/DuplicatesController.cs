using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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

    private record DecoratedTx(Transaction Tx, string Memo, string? Category, string CheckNumber, string? Payee);

    /// <summary>
    /// Fingerprints a duplicate group's current contents — every transaction's id
    /// and the values that could make it stop looking like the same duplicate
    /// (payee, category, memo, check number, status). Built from decrypted plaintext,
    /// not ciphertext — AES-GCM re-encrypts with a fresh nonce on every save, so an
    /// unrelated field's re-save would otherwise produce different ciphertext for an
    /// unchanged check number and falsely invalidate the dismissal.
    /// A dismissal is only honored while this matches what it matched at dismiss
    /// time; any edit to a member transaction, or another transaction joining the
    /// group, changes the hash and brings the group back.
    /// </summary>
    private static string ComputeSignature(IEnumerable<DecoratedTx> members)
    {
        var parts = members
            .OrderBy(m => m.Tx.Id)
            .Select(m => string.Join('|',
                m.Tx.Id,
                m.Payee ?? "",
                m.Category ?? "",
                m.Memo,
                m.CheckNumber,
                m.Tx.Status));
        var raw = string.Join('\n', parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Groups the user's transactions by (account, date, amount) — and optionally
    /// also memo and/or category — and returns only the groups with more than one
    /// transaction. Transfer legs are excluded: two linked transfer legs naturally
    /// share date/amount across different accounts and aren't duplicates.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] int? accountId,
        [FromQuery] bool includeMemo = false,
        [FromQuery] bool includeCategory = false,
        [FromQuery] bool showIgnored = false)
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
            .Where(t => accounts.Keys.Contains(t.AccountId) && t.TransferTransactionId == null && !t.IsVoided)
            .Include(t => t.Payee)
            .Include(t => t.Category)
            .ToListAsync();

        // Memo/category are encrypted, so matching on them has to happen in memory
        // after decryption — group key includes them only when the caller opts in.
        var decorated = candidates.Select(t => new DecoratedTx(
            t,
            encryption.Decrypt(t.MemoEncrypted, dek) ?? "",
            t.Category is null ? null : encryption.Decrypt(t.Category.NameEncrypted, dek),
            encryption.Decrypt(t.CheckNumberEncrypted, dek) ?? "",
            t.Payee is null ? null : encryption.Decrypt(t.Payee.NameEncrypted, dek)));

        var dismissed = await db.DismissedDuplicateGroups
            .Where(d => d.UserId == userId)
            .ToDictionaryAsync(d => (d.AccountId, d.Date, d.Amount), d => d.SignatureHash);

        var groups = decorated
            .GroupBy(x => (
                x.Tx.AccountId,
                x.Tx.Date,
                x.Tx.Amount,
                Memo:     includeMemo ? x.Memo : "",
                Category: includeCategory ? x.Category ?? "" : ""))
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var key = (g.Key.AccountId, g.Key.Date, g.Key.Amount);
                var signature = ComputeSignature(g);
                var isIgnored = dismissed.TryGetValue(key, out var dismissedSig) && dismissedSig == signature;
                return new { g, signature, isIgnored };
            })
            .Where(x => showIgnored || !x.isIgnored)
            .OrderByDescending(x => x.g.Key.Date)
            .Select(x => new
            {
                accountId   = x.g.Key.AccountId,
                accountName = accounts[x.g.Key.AccountId],
                date        = x.g.Key.Date,
                amount      = x.g.Key.Amount,
                ignored     = x.isIgnored,
                transactions = x.g
                    .OrderBy(m => m.Tx.Id)
                    .Select(m => new
                    {
                        id          = m.Tx.Id,
                        payee       = m.Payee,
                        category    = m.Category,
                        memo        = m.Memo,
                        checkNumber = m.CheckNumber,
                        status      = m.Tx.Status,
                        createdAt   = m.Tx.CreatedAt,
                    }),
            });

        return Ok(groups);
    }

    public record DismissDto(int AccountId, DateOnly Date, decimal Amount);

    /// <summary>
    /// Ignores a duplicate group as it currently looks. Re-derives the group live
    /// (rather than trusting anything from the client) so the stored signature is
    /// always accurate.
    /// </summary>
    [HttpPost("ignore")]
    public async Task<IActionResult> Ignore(DismissDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        if (!await db.Accounts.AnyAsync(a => a.Id == dto.AccountId && a.UserId == userId))
            return NotFound();

        var members = await db.Transactions
            .Where(t => t.AccountId == dto.AccountId && t.Date == dto.Date && t.Amount == dto.Amount
                     && t.TransferTransactionId == null)
            .Include(t => t.Payee)
            .Include(t => t.Category)
            .ToListAsync();

        if (members.Count < 2)
            return BadRequest(new { message = "That group no longer has more than one transaction." });

        var decorated = members.Select(t => new DecoratedTx(
            t,
            encryption.Decrypt(t.MemoEncrypted, dek) ?? "",
            t.Category is null ? null : encryption.Decrypt(t.Category.NameEncrypted, dek),
            encryption.Decrypt(t.CheckNumberEncrypted, dek) ?? "",
            t.Payee is null ? null : encryption.Decrypt(t.Payee.NameEncrypted, dek)));
        var signature = ComputeSignature(decorated);

        var existing = await db.DismissedDuplicateGroups.FirstOrDefaultAsync(d =>
            d.UserId == userId && d.AccountId == dto.AccountId && d.Date == dto.Date && d.Amount == dto.Amount);

        if (existing is null)
        {
            db.DismissedDuplicateGroups.Add(new DismissedDuplicateGroup
            {
                UserId = userId,
                AccountId = dto.AccountId,
                Date = dto.Date,
                Amount = dto.Amount,
                SignatureHash = signature,
            });
        }
        else
        {
            existing.SignatureHash = signature;
            existing.CreatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Un-ignores a previously-dismissed duplicate group.</summary>
    [HttpPost("unignore")]
    public async Task<IActionResult> Unignore(DismissDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var existing = await db.DismissedDuplicateGroups.FirstOrDefaultAsync(d =>
            d.UserId == userId && d.AccountId == dto.AccountId && d.Date == dto.Date && d.Amount == dto.Amount);
        if (existing is null) return NotFound();

        db.DismissedDuplicateGroups.Remove(existing);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
