using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

/// <summary>
/// IOU payment pages, signed-in side: the user's payment details, creating /
/// emailing / revoking a person's 30-day link, and "I've sent it" notices.
/// The public page itself is PayController.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ious")]
public class IouSharingController(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    IEncryptionService encryption,
    IouPaymentPages pages,
    IAuditService audit) : ControllerBase
{
    private async Task<ApplicationUser?> CurrentUserAsync() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) is string id ? await userManager.FindByIdAsync(id) : null;

    /// <summary>Payment details, each person's live links, and unread "I've sent it" notices.</summary>
    [HttpGet("sharing")]
    public async Task<IActionResult> Overview()
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var now = DateTime.UtcNow;
        var links = await db.IouShareLinks.AsNoTracking()
            .Where(l => l.UserId == user.Id && l.RevokedAt == null && l.ExpiresAt > now)
            .GroupBy(l => l.PersonId).Select(g => new { personId = g.Key, count = g.Count(), expiresAt = g.Max(l => l.ExpiresAt) })
            .ToListAsync();
        var notices = await db.IouPaidNotices.AsNoTracking().Include(n => n.Person)
            .Where(n => n.UserId == user.Id && n.DismissedAt == null).OrderByDescending(n => n.CreatedAt).Take(50).ToListAsync();
        return Ok(new
        {
            details = pages.GetDetails(user),
            links,
            notices = notices.Select(n => new
            {
                id = n.Id, personId = n.PersonId,
                personName = encryption.Decrypt(n.Person.NameEncrypted, user.EncryptedDataKey),
                method = IouPaymentPages.TypeName(n.Method), amount = n.Amount, createdAt = n.CreatedAt,
            }),
        });
    }

    [HttpPut("payment-details")]
    public async Task<IActionResult> SaveDetails(IouPaymentPages.Details dto)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        try { await pages.SaveDetailsAsync(user, dto with { Methods = dto.Methods ?? [] }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        await audit.LogAsync("IOU_PAYMENT_DETAILS_CHANGED");
        return Ok(pages.GetDetails(user));
    }

    private async Task<(ApplicationUser? User, IouPerson? Person, IActionResult? Error)> PersonAsync(int id)
    {
        var user = await CurrentUserAsync();
        if (user is null) return (null, null, Unauthorized());
        var person = await db.IouPeople.FirstOrDefaultAsync(p => p.Id == id && p.UserId == user.Id);
        return person is null ? (null, null, NotFound()) : (user, person, null);
    }

    /// <summary>A new 30-day link to the person's payment page (to copy).</summary>
    [HttpPost("people/{id:int}/link")]
    public async Task<IActionResult> CreateLink(int id)
    {
        var (user, person, error) = await PersonAsync(id);
        if (error is not null) return error;
        if (pages.GetDetails(user!).Methods.Count == 0)
            return BadRequest(new { message = "Add at least one way to pay you (Payment details) first." });
        var (token, expires) = await pages.CreateLinkAsync(user!, person!);
        await audit.LogAsync("IOU_LINK_CREATED", "IouPerson", id);
        var url = pages.Url(token, Request);
        // Ready to paste into a text: the itemized breakdown, total, and the link.
        var owed = await pages.OwedAsync(user!, person!.Id);
        var from = pages.FromName(user!);
        var to = encryption.Decrypt(person.NameEncrypted, user!.EncryptedDataKey) ?? "there";
        var message = IouPaymentPages.MessageText(from, to, owed, url, expires);
        var html = IouPaymentPages.MessageHtml(from, to, owed, url, expires);
        // The app never sends IOU emails itself: the page opens the user's own email app with this subject and message.
        return Ok(new { url, expiresAt = expires, message, html, subject = $"Payment request from {from}" });
    }

    /// <summary>Turns off every link for the person.</summary>
    [HttpDelete("people/{id:int}/links")]
    public async Task<IActionResult> RevokeLinks(int id)
    {
        var (user, _, error) = await PersonAsync(id);
        if (error is not null) return error;
        var n = await db.IouShareLinks.Where(l => l.UserId == user!.Id && l.PersonId == id && l.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.RevokedAt, DateTime.UtcNow));
        await audit.LogAsync("IOU_LINKS_REVOKED", "IouPerson", id, new { count = n });
        return Ok(new { revoked = n });
    }

    [HttpPost("notices/{id:int}/dismiss")]
    public async Task<IActionResult> DismissNotice(int id)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var n = await db.IouPaidNotices.Where(x => x.Id == id && x.UserId == user.Id && x.DismissedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DismissedAt, DateTime.UtcNow));
        return n == 0 ? NotFound() : NoContent();
    }
}

/// <summary>
/// The public IOU payment page (/pay/{token}): no sign-in; the unguessable,
/// expiring token is the key. Shows only date, payee and amount still owed per
/// IOU, the total, and how to pay. Rate-limited like sign-in.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting("auth")]
[Route("api/pay")]
public class PayController(
    AppDbContext db,
    IEncryptionService encryption,
    IouPaymentPages pages,
    EmailSender email,
    ILogger<PayController> logger) : ControllerBase
{
    private void Private()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static readonly object Gone = new { message = "This link has expired or been turned off. Ask for a new one." };

    [HttpGet("{token}")]
    public async Task<IActionResult> Get(string token)
    {
        Private();
        if (await pages.ResolveAsync(token) is not var (link, user, person)) return NotFound(Gone);
        var owed = await pages.OwedAsync(user, person.Id);
        var from = pages.FromName(user);
        return Ok(new
        {
            from,
            to = encryption.Decrypt(person.NameEncrypted, user.EncryptedDataKey),
            total = owed.Total,
            itemsTotal = owed.ItemsTotal,
            items = owed.Items.Select(i => new { date = i.Date, payee = i.Payee, amount = i.Amount }),
            options = owed.Total > 0 ? IouPaymentPages.Options(pages.GetDetails(user), owed.Total, $"IOUs to {from}") : [],
            expiresAt = link.ExpiresAt,
        });
    }

    public record SentDto(string? Method);

    /// <summary>"I've sent it": records a notice for the user (they still record the payment) and emails them.</summary>
    [HttpPost("{token}/sent")]
    public async Task<IActionResult> Sent(string token, SentDto dto, CancellationToken ct)
    {
        Private();
        if (await pages.ResolveAsync(token) is not var (_, user, person)) return NotFound(Gone);
        var method = IouPaymentPages.Types.Contains(dto.Method ?? "") ? dto.Method! : "other";
        // One notice per person per 10 minutes is plenty (double taps, retries).
        var recent = DateTime.UtcNow.AddMinutes(-10);
        if (await db.IouPaidNotices.AnyAsync(n => n.UserId == user.Id && n.PersonId == person.Id && n.CreatedAt > recent))
            return Ok(new { ok = true });
        var owed = await pages.OwedAsync(user, person.Id);
        db.IouPaidNotices.Add(new IouPaidNotice { UserId = user.Id, PersonId = person.Id, Method = method, Amount = owed.Total });
        await db.SaveChangesAsync(ct);

        var name = encryption.Decrypt(person.NameEncrypted, user.EncryptedDataKey) ?? "Someone";
        if (email.IsConfigured && !string.IsNullOrWhiteSpace(user.Email))
        {
            var app = EmailLayout.AppUrl(HttpContext.RequestServices.GetRequiredService<IConfiguration>());
            var via = IouPaymentPages.TypeName(method);
            var text = $"{name} says they've sent you {IouPaymentPages.Money(owed.Total)} with {via}.\n\n"
                + "Once it arrives, record the payment on the IOUs page so their balance is up to date."
                + (app.Length > 0 ? $"\n{app}/ious" : "");
            var html = $"<p>{System.Net.WebUtility.HtmlEncode(text).Replace("\n", "<br>")}</p>";
            try { await email.SendAsync(user.Email, $"{name} says they've paid you", text, html, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Couldn't email the I've-paid notice"); }
        }
        return Ok(new { ok = true });
    }
}
