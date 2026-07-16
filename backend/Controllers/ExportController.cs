using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Export;
using MoneyTracker.Models;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/export")]
public class ExportController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    ExportService exportService) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    // ── Step 1: re-authenticate to receive a single-use export token ──────────

    // Same per-IP throttle as login: this endpoint verifies passwords/TOTP codes,
    // so without it TOTP guessing here would be bounded only by account lockout.
    [HttpPost("confirm-identity")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> ConfirmIdentity(ConfirmIdentityRequest request)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        if (await userManager.IsLockedOutAsync(user))
            return StatusCode(429, "Account locked due to too many failed attempts. Try again later.");

        bool verified = false;

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            // lockoutOnFailure ensures re-auth attempts count toward lockout
            var check = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
            if (check.IsLockedOut)
                return StatusCode(429, "Account locked due to too many failed attempts. Try again later.");
            verified = check.Succeeded;
        }
        else if (!string.IsNullOrWhiteSpace(request.TotpCode))
        {
            verified = await userManager.VerifyTwoFactorTokenAsync(
                user,
                userManager.Options.Tokens.AuthenticatorTokenProvider,
                request.TotpCode);
            if (!verified) await userManager.AccessFailedAsync(user);
        }

        if (!verified)
            return Unauthorized("Identity verification failed.");

        await userManager.ResetAccessFailedCountAsync(user);

        // Expire any previous unused tokens for this user
        var old = db.ExportTokens.Where(t => t.UserId == userId && !t.IsUsed && t.ExpiresAt > DateTime.UtcNow);
        await old.ForEachAsync(t => t.IsUsed = true);

        var token = new ExportToken
        {
            UserId    = userId,
            Token     = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ExpiresAt = DateTime.UtcNow.AddSeconds(60),
        };

        db.ExportTokens.Add(token);
        await db.SaveChangesAsync();

        return Ok(new { exportToken = token.Token, expiresAt = token.ExpiresAt });
    }

    // ── Step 2: download in the chosen format ─────────────────────────────────

    [HttpGet("qif")]
    public Task<IActionResult> DownloadQif([FromHeader(Name = "X-Export-Token")] string exportToken) =>
        ExportAs(exportToken, "application/qif", "export.qif",
            (data, dek) => exportService.ToQif(data, dek));

    [HttpGet("ofx")]
    public Task<IActionResult> DownloadOfx([FromHeader(Name = "X-Export-Token")] string exportToken) =>
        ExportAs(exportToken, "application/x-ofx", "export.ofx",
            (data, dek) => exportService.ToOfx(data, dek));

    [HttpGet("csv")]
    public async Task<IActionResult> DownloadCsv([FromHeader(Name = "X-Export-Token")] string exportToken)
    {
        var (userId, user, data) = await ValidateAndLoad(exportToken);
        if (userId is null || user is null || data is null) return Unauthorized();

        MarkTokenUsed(exportToken);
        await db.SaveChangesAsync();

        var rows  = exportService.ToRows(data, user.EncryptedDataKey);
        var bytes = exportService.ToCsv(rows);
        return File(bytes, "text/csv", "export.csv");
    }

    [HttpGet("xlsx")]
    public async Task<IActionResult> DownloadXlsx([FromHeader(Name = "X-Export-Token")] string exportToken)
    {
        var (userId, user, data) = await ValidateAndLoad(exportToken);
        if (userId is null || user is null || data is null) return Unauthorized();

        var accounts = data.Select(d => d.Account).ToList();
        MarkTokenUsed(exportToken);
        await db.SaveChangesAsync();

        var bytes = exportService.ToXlsx(data, accounts, user.EncryptedDataKey);
        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "export.xlsx");
    }

    [HttpGet("json")]
    public async Task<IActionResult> DownloadJson([FromHeader(Name = "X-Export-Token")] string exportToken)
    {
        var (userId, user, data) = await ValidateAndLoad(exportToken);
        if (userId is null || user is null || data is null) return Unauthorized();

        var dek = user.EncryptedDataKey;

        var payload = data.Select(d => new
        {
            account = new
            {
                id             = d.Account.Id,
                name           = d.Account.Name,
                type           = d.Account.Type.ToString(),
                openingBalance = d.Account.OpeningBalance,
                institution    = d.Account.Institution?.Name,
            },
            transactions = d.Transactions.Select(t => new
            {
                id          = t.Id,
                date        = t.Date,
                payee       = t.Payee is null ? null : encryption.Decrypt(t.Payee.NameEncrypted, dek),
                category    = t.Category is null || t.Category.ParentId is not null ? null : encryption.Decrypt(t.Category.NameEncrypted, dek),
                subCategory = t.Category?.ParentId is null ? null : encryption.Decrypt(t.Category.NameEncrypted, dek),
                memo        = encryption.Decrypt(t.MemoEncrypted, dek),
                amount      = t.Amount,
                status      = t.Status.ToString(),
                checkNumber = encryption.Decrypt(t.CheckNumberEncrypted, dek),
                isTransfer  = t.TransferTransactionId != null,
                splits      = t.Splits.Count == 0 ? null : t.Splits.Select(s => new
                {
                    category    = s.Category is null || s.Category.ParentId is not null ? null : encryption.Decrypt(s.Category.NameEncrypted, dek),
                    subCategory = s.Category?.ParentId is null ? null : encryption.Decrypt(s.Category.NameEncrypted, dek),
                    memo        = encryption.Decrypt(s.MemoEncrypted, dek),
                    amount      = s.Amount,
                }),
            }),
        });

        MarkTokenUsed(exportToken);
        await db.SaveChangesAsync();

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "export.json");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IActionResult> ExportAs(
        string exportToken,
        string contentType,
        string fileName,
        Func<IList<(Account Account, IEnumerable<Transaction> Transactions)>, string, string> serialize)
    {
        var (userId, user, data) = await ValidateAndLoad(exportToken);
        if (userId is null || user is null || data is null) return Unauthorized();

        MarkTokenUsed(exportToken);
        await db.SaveChangesAsync();

        var content = serialize(data, user.EncryptedDataKey);
        return File(System.Text.Encoding.UTF8.GetBytes(content), contentType, fileName);
    }

    private async Task<(string? UserId, ApplicationUser? User,
        IList<(Account Account, IEnumerable<Transaction> Transactions)>? Data)>
        ValidateAndLoad(string exportToken)
    {
        var userId = GetUserId();
        if (userId is null) return (null, null, null);

        var stored = await db.ExportTokens
            .FirstOrDefaultAsync(t => t.Token == exportToken && t.UserId == userId
                                   && !t.IsUsed && t.ExpiresAt > DateTime.UtcNow);
        if (stored is null) return (null, null, null);

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return (null, null, null);

        var accounts = await db.Accounts
            .Where(a => a.UserId == userId && a.IsActive)
            .Include(a => a.Institution)
            .ToListAsync();

        var data = new List<(Account, IEnumerable<Transaction>)>();
        foreach (var account in accounts)
        {
            var txs = await db.Transactions
                .Where(t => t.AccountId == account.Id)
                .Include(t => t.Payee)
                .Include(t => t.Category)
                .Include(t => t.Splits).ThenInclude(s => s.Category)
                .OrderByDescending(t => t.Date)
                .ToListAsync();

            data.Add((account, txs));
        }

        return (userId, user, data);
    }

    private void MarkTokenUsed(string exportToken)
    {
        var token = db.ExportTokens.Local
            .FirstOrDefault(t => t.Token == exportToken);
        if (token is not null) token.IsUsed = true;
    }
}

public record ConfirmIdentityRequest(string? Password, string? TotpCode);
