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
            server = server is null ? null : new { provider = server.Provider, host = Host(server.BaseUrl), model = server.Model },
            serverProblem = problem,
            ready = user.AiEnabled && server is not null,
        });
    }

    public record ShareDto(bool Balances, bool Transactions, bool Memos, bool Bills, bool Ious);
    public record SettingsDto(bool Enabled, ShareDto Share);

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
        var c = await db.AiConversations.AsNoTracking().Include(x => x.Messages)
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

    /// <summary>
    /// Asks a question in a conversation (a new one when ConversationId is
    /// null). The question and answer are saved, encrypted; an error saves nothing.
    /// </summary>
    [HttpPost("ask")]
    public async Task<IActionResult> Ask(AskDto dto, CancellationToken ct)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var question = dto.Message?.Trim() ?? "";
        if (question.Length == 0) return BadRequest(new { message = "Type a question." });
        if (question.Length > MaxQuestionLength) return BadRequest(new { message = $"Questions can be up to {MaxQuestionLength} characters." });
        if (!user.AiEnabled) return BadRequest(new { message = "The AI assistant is turned off. Turn it on in Settings → AI Assistant." });

        var dek = user.EncryptedDataKey;
        AiConversation? convo = null;
        var history = new List<(string, string)>();
        if (dto.ConversationId is int cid)
        {
            convo = await db.AiConversations.Include(c => c.Messages).FirstOrDefaultAsync(c => c.Id == cid && c.UserId == user.Id);
            if (convo is null) return NotFound();
            history = convo.Messages.OrderBy(m => m.Id).TakeLast(HistoryTurns * 2)
                .Select(m => (m.Role, encryption.Decrypt(m.ContentEncrypted, dek) ?? "")).ToList();
        }

        string answer;
        try { answer = await assistant.AskAsync(user, history, question, ct); }
        catch (AiAssistant.AiException ex) { return StatusCode(502, new { message = ex.Message }); }

        var now = DateTime.UtcNow;
        if (convo is null)
        {
            var title = question.ReplaceLineEndings(" ");
            convo = new AiConversation { UserId = user.Id, TitleEncrypted = encryption.Encrypt(title.Length > 80 ? title[..80] + "…" : title, dek)!, CreatedAt = now };
            db.AiConversations.Add(convo);
        }
        convo.UpdatedAt = now;
        convo.Messages.Add(new AiMessage { Role = "user", ContentEncrypted = encryption.Encrypt(question, dek)!, CreatedAt = now });
        convo.Messages.Add(new AiMessage { Role = "assistant", ContentEncrypted = encryption.Encrypt(answer, dek)!, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);

        return Ok(new { conversationId = convo.Id, title = encryption.Decrypt(convo.TitleEncrypted, dek), answer });
    }
}
