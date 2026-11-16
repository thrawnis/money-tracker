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

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        var server = assistant.ServerDefault;
        var active = assistant.Resolve(user);
        return Ok(new
        {
            enabled = user.AiEnabled,
            baseUrl = user.AiBaseUrl,
            model = user.AiModel,
            hasApiKey = !string.IsNullOrEmpty(user.AiApiKeyEncrypted),
            share = new
            {
                balances = user.AiShareBalances,
                transactions = user.AiShareTransactions,
                memos = user.AiShareMemos,
                bills = user.AiShareBills,
                ious = user.AiShareIous,
            },
            allowUserServers = assistant.AllowUserServers,
            server = server is null ? null : new { host = Host(server.BaseUrl), model = server.Model },
            // What questions would use right now (null: not set up).
            active = active is null ? null : new { host = Host(active.BaseUrl), model = active.Model, personal = active.Personal },
            ready = user.AiEnabled && active is not null,
        });
    }

    public record ShareDto(bool Balances, bool Transactions, bool Memos, bool Bills, bool Ious);
    /// <summary>ApiKey: null keeps the saved key, "" removes it.</summary>
    public record SettingsDto(bool Enabled, string? BaseUrl, string? Model, string? ApiKey, ShareDto Share);

    private static string? ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            return "Enter the server address as a full http:// or https:// URL, e.g. https://api.openai.com/v1 or http://192.168.1.20:11434/v1.";
        return null;
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings(SettingsDto dto)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        if (ValidateUrl(dto.BaseUrl) is string urlError) return BadRequest(new { message = urlError });
        if (!assistant.AllowUserServers && !string.IsNullOrWhiteSpace(dto.BaseUrl))
            return BadRequest(new { message = "This server doesn't allow choosing your own AI server." });
        if (dto.Model?.Length > 200 || dto.BaseUrl?.Length > 500 || dto.ApiKey?.Length > 1000)
            return BadRequest(new { message = "One of the values is too long." });

        user.AiBaseUrl = string.IsNullOrWhiteSpace(dto.BaseUrl) ? null : dto.BaseUrl.Trim().TrimEnd('/');
        user.AiModel = string.IsNullOrWhiteSpace(dto.Model) ? null : dto.Model.Trim();
        if (dto.ApiKey is not null)
            user.AiApiKeyEncrypted = dto.ApiKey.Trim() == "" ? null : encryption.Encrypt(dto.ApiKey.Trim(), user.EncryptedDataKey);
        // A personal key only makes sense with a personal server.
        if (user.AiBaseUrl is null) user.AiApiKeyEncrypted = null;
        user.AiShareBalances = dto.Share.Balances;
        user.AiShareTransactions = dto.Share.Transactions;
        user.AiShareMemos = dto.Share.Memos;
        user.AiShareBills = dto.Share.Bills;
        user.AiShareIous = dto.Share.Ious;

        if (dto.Enabled && assistant.Resolve(user) is null)
            return BadRequest(new { message = assistant.ServerDefault is null
                ? "Enter a server address and model to turn the assistant on."
                : "Enter a model name to use with your server." });
        user.AiEnabled = dto.Enabled;
        await userManager.UpdateAsync(user);
        await audit.LogAsync("AI_SETTINGS_CHANGED", details: new { enabled = user.AiEnabled, personalServer = user.AiBaseUrl is not null });
        return await GetSettings();
    }

    public record TestDto(string? BaseUrl, string? Model, string? ApiKey);

    /// <summary>Tries the given server (or, with blanks, the saved / server-wide one).</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test(TestDto dto, CancellationToken ct)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Unauthorized();
        if (ValidateUrl(dto.BaseUrl) is string urlError) return BadRequest(new { message = urlError });

        AiAssistant.Endpoint? ep;
        if (!string.IsNullOrWhiteSpace(dto.BaseUrl))
        {
            if (!assistant.AllowUserServers) return BadRequest(new { message = "This server doesn't allow choosing your own AI server." });
            var model = string.IsNullOrWhiteSpace(dto.Model) ? assistant.ServerDefault?.Model : dto.Model.Trim();
            if (model is null) return BadRequest(new { message = "Enter a model name." });
            // Blank key field: the saved key, if the address hasn't changed.
            var key = dto.ApiKey ?? (user.AiBaseUrl == dto.BaseUrl.Trim().TrimEnd('/') ? encryption.Decrypt(user.AiApiKeyEncrypted, user.EncryptedDataKey) : null);
            ep = new AiAssistant.Endpoint(dto.BaseUrl.Trim().TrimEnd('/'), key, model, true);
        }
        else
        {
            ep = assistant.ServerDefault;
            if (ep is not null && !string.IsNullOrWhiteSpace(dto.Model)) ep = ep with { Model = dto.Model.Trim() };
        }
        if (ep is null) return BadRequest(new { message = "Enter a server address and model." });

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
