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

/// <summary>
/// The AI assistant: its settings (Settings → AI Assistant) and the saved,
/// encrypted conversations on the Ask page. See AiAssistant / AiTools.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ai")]
public class AiController(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    IEncryptionService encryption,
    AiAssistant assistant,
    AiProposals proposals,
    IAuditService audit) : ControllerBase
{
    private const int MaxQuestionLength = 4000;
    private const int HistoryTurns = 20;

    private async Task<ApplicationUser?> CurrentUserAsync() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) is string id ? await userManager.FindByIdAsync(id) : null;

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    // ── Settings ────────────────────────────────────────────────────────────
    // The AI server comes from .env only (GeneralAi); users just turn the
    // assistant on and choose what it may look up.

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var (server, problem) = assistant.Server;
        return Ok(new
        {
            enabled = user.AiEnabled,
            share = new
            {
                balances = user.AiShareBalances,
                transactions = user.AiShareTransactions,
                memos = user.AiShareMemos,
                bills = user.AiShareBills,
                ious = user.AiShareIous,
            },
            allowProposals = user.AiAllowProposals,
            server = server is null ? null : new { provider = server.Provider, host = Host(server.BaseUrl), model = server.Model },
            serverProblem = problem,
            ready = user.AiEnabled && server is not null,
        });
    }

    public record ShareDto(bool Balances, bool Transactions, bool Memos, bool Bills, bool Ious);
    public record SettingsDto(bool Enabled, ShareDto Share, bool AllowProposals = false);

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings(SettingsDto dto)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        if (dto.Enabled && assistant.Server.Endpoint is null)
            return BadRequest(new { message = "The AI assistant isn't set up on this server yet. " + assistant.Server.Problem });

        user.AiEnabled = dto.Enabled;
        user.AiShareBalances = dto.Share.Balances;
        user.AiShareTransactions = dto.Share.Transactions;
        user.AiShareMemos = dto.Share.Memos;
        user.AiShareBills = dto.Share.Bills;
        user.AiShareIous = dto.Share.Ious;
        user.AiAllowProposals = dto.AllowProposals;
        await userManager.UpdateAsync(user);
        await audit.LogAsync("AI_SETTINGS_CHANGED", details: new { enabled = user.AiEnabled });
        return await GetSettings();
    }

    /// <summary>Checks the configured AI server answers (Settings → Test connection).</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test(CancellationToken ct)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var (ep, problem) = assistant.Server;
        if (ep is null) return BadRequest(new { message = problem });
        try
        {
            var reply = await assistant.TestAsync(ep, ct);
            return Ok(new { ok = true, message = $"Connected to {Host(ep.BaseUrl)} using {ep.Model}." + (reply.Length > 0 ? $" It replied: \"{(reply.Length > 60 ? reply[..60] + "…" : reply)}\"" : "") });
        }
        catch (AiAssistant.AiException ex) { return BadRequest(new { message = ex.Message }); }
    }

    // ── Conversations ───────────────────────────────────────────────────────

    [HttpGet("conversations")]
    public async Task<IActionResult> List()
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var list = await db.AiConversations.AsNoTracking().Where(c => c.UserId == user.Id)
            .OrderByDescending(c => c.UpdatedAt).Take(200).ToListAsync();
        return Ok(list.Select(c => new
        {
            id = c.Id,
            title = encryption.Decrypt(c.TitleEncrypted, user.EncryptedDataKey),
            updatedAt = c.UpdatedAt,
        }));
    }

    [HttpGet("conversations/{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var c = await db.AiConversations.AsNoTracking().Include(x => x.Messages).ThenInclude(m => m.Proposals)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id);
        if (c is null) return NotFound();
        return Ok(new
        {
            id = c.Id,
            title = encryption.Decrypt(c.TitleEncrypted, user.EncryptedDataKey),
            messages = c.Messages.OrderBy(m => m.Id).Select(m => new
            {
                id = m.Id, role = m.Role, createdAt = m.CreatedAt,
                content = encryption.Decrypt(m.ContentEncrypted, user.EncryptedDataKey),
                proposals = m.Proposals.OrderBy(x => x.Id).Select(x => proposals.Describe(x, user)),
            }),
        });
    }

    [HttpDelete("conversations/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var n = await db.AiConversations.Where(x => x.Id == id && x.UserId == user.Id).ExecuteDeleteAsync();
        return n == 0 ? NotFound() : NoContent();
    }

    public record AskDto(int? ConversationId, string Message);

    private record Prepared(ApplicationUser User, string Question, AiConversation? Convo, List<(string Role, string Content)> History);

    /// <summary>Checks the request and loads the conversation; an IActionResult means "refuse with this".</summary>
    private async Task<(Prepared? Ok, IActionResult? Refuse)> PrepareAsync(AskDto dto)
    {
        var user = await CurrentUserAsync();
        if (user is null) return (null, Unauthorized());
        var question = dto.Message?.Trim() ?? "";
        if (question.Length == 0) return (null, BadRequest(new { message = "Type a question." }));
        if (question.Length > MaxQuestionLength) return (null, BadRequest(new { message = $"Questions can be up to {MaxQuestionLength} characters." }));
        if (!user.AiEnabled) return (null, BadRequest(new { message = "The AI assistant is turned off. Turn it on in Settings → AI Assistant." }));

        AiConversation? convo = null;
        var history = new List<(string, string)>();
        if (dto.ConversationId is int cid)
        {
            convo = await db.AiConversations.Include(c => c.Messages).FirstOrDefaultAsync(c => c.Id == cid && c.UserId == user.Id);
            if (convo is null) return (null, NotFound());
            history = convo.Messages.OrderBy(m => m.Id).TakeLast(HistoryTurns * 2)
                .Select(m => (m.Role, encryption.Decrypt(m.ContentEncrypted, user.EncryptedDataKey) ?? "")).ToList();
        }
        return (new Prepared(user, question, convo, history), null);
    }

    /// <summary>Saves the question and answer (encrypted), starting the conversation if needed.</summary>
    private async Task<(int Id, string? Title, List<AiProposals.View> Proposals)> SaveAsync(Prepared p, string answer)
    {
        var dek = p.User.EncryptedDataKey;
        var convo = p.Convo;
        var now = DateTime.UtcNow;
        if (convo is null)
        {
            var title = p.Question.ReplaceLineEndings(" ");
            convo = new AiConversation { UserId = p.User.Id, TitleEncrypted = encryption.Encrypt(title.Length > 80 ? title[..80] + "…" : title, dek)!, CreatedAt = now };
            db.AiConversations.Add(convo);
        }
        convo.UpdatedAt = now;
        convo.Messages.Add(new AiMessage { Role = "user", ContentEncrypted = encryption.Encrypt(p.Question, dek)!, CreatedAt = now });
        var reply = new AiMessage { Role = "assistant", ContentEncrypted = encryption.Encrypt(answer, dek)!, CreatedAt = DateTime.UtcNow };
        proposals.AttachTo(reply, p.User);    // changes it proposed while answering
        convo.Messages.Add(reply);
        // Saved even if the browser went away mid-answer: the work is done.
        await db.SaveChangesAsync(CancellationToken.None);
        return (convo.Id, encryption.Decrypt(convo.TitleEncrypted, dek), reply.Proposals.Select(x => proposals.Describe(x, p.User)).ToList());
    }

    /// <summary>
    /// Asks a question in a conversation (a new one when ConversationId is
    /// null), returning the whole answer at once. The question and answer are
    /// saved, encrypted; an error saves nothing. The Ask page uses ask/stream.
    /// </summary>
    [HttpPost("ask")]
    public async Task<IActionResult> Ask(AskDto dto, CancellationToken ct)
    {
        var (p, refuse) = await PrepareAsync(dto);
        if (p is null) return refuse!;
        string answer;
        try { answer = await assistant.AskAsync(p.User, p.History, p.Question, ct); }
        catch (AiAssistant.AiException ex) { return StatusCode(502, new { message = ex.Message }); }
        var (id, title, proposed) = await SaveAsync(p, answer);
        return Ok(new { conversationId = id, title, answer, proposals = proposed });
    }

    /// <summary>
    /// Like ask, but streams Server-Sent Events while it works:
    ///   {"type":"status","text":"Searching transactions…"}   what it's doing
    ///   {"type":"delta","text":"…"}                           the next piece of the answer
    ///   {"type":"done","conversationId":1,"title":"…","answer":"…"}
    ///   {"type":"error","message":"…"}                        nothing was saved
    /// Problems found before it starts (turned off, empty question…) are ordinary 4xx JSON responses.
    /// </summary>
    [HttpPost("ask/stream")]
    public async Task Stream(AskDto dto, CancellationToken ct)
    {
        var (p, refuse) = await PrepareAsync(dto);
        if (p is null) { await refuse!.ExecuteResultAsync(ControllerContext); return; }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";   // nginx: pass each event straight through
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        // If the page is closed mid-answer, keep going (bounded by the AI client's
        // timeout) and save it, so it's there when the user comes back; just stop sending.
        async Task Send(object e)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(e, json)}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { /* browser gone */ }
        }

        string answer;
        try { answer = await assistant.AskAsync(p.User, p.History, p.Question, CancellationToken.None, e => Send(new { type = e.Kind, text = e.Text })); }
        catch (AiAssistant.AiException ex) { await Send(new { type = "error", message = ex.Message }); return; }

        var (id, title, proposed) = await SaveAsync(p, answer);
        await Send(new { type = "done", conversationId = id, title, answer, proposals = proposed });
    }

    // ── Proposed changes ────────────────────────────────────────────────────

    public record ApplyDto(string[]? Exclude);

    /// <summary>Applies a change the assistant proposed (re-checked now); Exclude lists item keys the user unticked.</summary>
    [HttpPost("proposals/{id:int}/apply")]
    public async Task<IActionResult> Apply(int id, ApplyDto? dto)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var p = await db.AiProposals.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id);
        if (p is null) return NotFound();
        try
        {
            await proposals.ApplyAsync(user, p, dto?.Exclude ?? []);
            return Ok(proposals.Describe(p, user));
        }
        catch (AiProposals.ApplyException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("proposals/{id:int}/dismiss")]
    public async Task<IActionResult> Dismiss(int id)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var p = await db.AiProposals.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id);
        if (p is null) return NotFound();
        try { await proposals.DismissAsync(user, p); return Ok(proposals.Describe(p, user)); }
        catch (AiProposals.ApplyException ex) { return BadRequest(new { message = ex.Message }); }
    }
}
