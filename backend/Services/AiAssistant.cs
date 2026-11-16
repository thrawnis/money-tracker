using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoneyTracker.Auth.Services;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// The "Ask" assistant: sends the user's question to an OpenAI-compatible
/// chat-completions server (OpenAI, Ollama, LM Studio, vLLM…) with read-only
/// tools (AiTools) it can call to look things up, and returns its answer.
///
/// Server: the user's own (Settings → AI Assistant) if they set one, otherwise
/// the server-wide Ai:BaseUrl / Ai:ApiKey / Ai:Model. Ai:AllowUserServers=false
/// stops users choosing their own.
/// </summary>
public class AiAssistant(IHttpClientFactory httpFactory, IConfiguration config, IEncryptionService encryption, AiTools tools,
    ILogger<AiAssistant> logger)
{
    public const string HttpClientName = "ai";
    private const int MaxToolRounds = 8;

    public record Endpoint(string BaseUrl, string? ApiKey, string Model, bool Personal);

    public class AiException(string message) : Exception(message);

    private string? Cfg(string key) => string.IsNullOrWhiteSpace(config[key]) ? null : config[key]!.Trim();

    public bool AllowUserServers => !string.Equals(Cfg("Ai:AllowUserServers"), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>The server-wide default, if the admin set one.</summary>
    public Endpoint? ServerDefault =>
        Cfg("Ai:BaseUrl") is string url && Cfg("Ai:Model") is string model ? new Endpoint(url.TrimEnd('/'), Cfg("Ai:ApiKey"), model, false) : null;

    /// <summary>Which server this user's questions go to, or null if none is set up.</summary>
    public Endpoint? Resolve(ApplicationUser user)
    {
        var server = ServerDefault;
        if (AllowUserServers && !string.IsNullOrWhiteSpace(user.AiBaseUrl))
        {
            var key = encryption.Decrypt(user.AiApiKeyEncrypted, user.EncryptedDataKey);
            var model = !string.IsNullOrWhiteSpace(user.AiModel) ? user.AiModel.Trim() : server?.Model;
            if (model is null) return null;
            return new Endpoint(user.AiBaseUrl.Trim().TrimEnd('/'), key, model, true);
        }
        if (server is null) return null;
        // Using the server's endpoint but a different model is allowed.
        return string.IsNullOrWhiteSpace(user.AiModel) ? server : server with { Model = user.AiModel.Trim() };
    }

    private string SystemPrompt(ApplicationUser user)
    {
        var today = UserClock.Today(user);
        return $"""
            You are the assistant inside Money Tracker, a personal finance app. You answer the user's questions about
            their own accounts, transactions, spending, bills and IOUs.

            Today is {today:dddd, MMMM d, yyyy} ({today:yyyy-MM-dd}) in the user's time zone. Amounts are US dollars.
            Negative amounts are money out (spending), positive are money in.

            - Use the tools to look up real data before answering; never guess or invent numbers, payees or dates.
              Call several tools, or the same tool again with different filters, when needed.
            - If a tool says information isn't shared, tell the user they can allow it in Settings → AI Assistant.
            - "This month", "last year" etc. are relative to today. Use exact YYYY-MM-DD dates in tool calls.
            - search_transactions returns at most a page of rows but always the full count and total; use
              spending_summary for totals by category, payee or month.
            - You can only read data; you can't change anything. If asked to, explain how to do it in the app instead.
            - Be concise. Format money like $1,234.56. Use short bullet lists or small tables (Markdown) when helpful.
            """;
    }

    /// <summary>
    /// Asks the model. <paramref name="history"/> is the earlier turns of this
    /// conversation (role "user"/"assistant"); returns the answer text.
    /// </summary>
    public async Task<string> AskAsync(ApplicationUser user, IEnumerable<(string Role, string Content)> history, string question, CancellationToken ct)
    {
        var ep = Resolve(user) ?? throw new AiException("The AI assistant isn't set up. Choose a server in Settings → AI Assistant.");
        var messages = new JsonArray { Msg("system", SystemPrompt(user)) };
        foreach (var (role, content) in history) messages.Add(Msg(role, content));
        messages.Add(Msg("user", question));
        var toolDefs = AiTools.Definitions(user);

        for (var round = 0; ; round++)
        {
            var body = new JsonObject
            {
                ["model"] = ep.Model,
                ["messages"] = messages.DeepClone(),
                ["temperature"] = 0.2,
            };
            // Last round: no tools, so the model has to answer with what it has.
            if (round < MaxToolRounds) { body["tools"] = toolDefs.DeepClone(); body["tool_choice"] = "auto"; }

            var reply = await PostAsync(ep, body, ct);
            var message = reply["choices"]?[0]?["message"] as JsonObject
                ?? throw new AiException("The AI server sent back an unexpected response.");
            var calls = message["tool_calls"] as JsonArray;
            if (calls is null || calls.Count == 0)
            {
                var text = message["content"]?.GetValue<string>()?.Trim();
                return string.IsNullOrEmpty(text) ? "(The assistant didn't return an answer.)" : text;
            }

            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = message["content"]?.DeepClone(), ["tool_calls"] = calls.DeepClone() });
            foreach (var call in calls)
            {
                var name = call?["function"]?["name"]?.GetValue<string>() ?? "";
                var argsNode = call?["function"]?["arguments"];
                // Most servers send arguments as a JSON string; some send an object.
                var args = argsNode is JsonValue v && v.TryGetValue<string>(out var s) ? s : argsNode?.ToJsonString() ?? "{}";
                logger.LogDebug("AI tool call {Tool} for user {UserId}", name, user.Id);
                var result = await tools.RunAsync(user, name, args);
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call?["id"]?.GetValue<string>() ?? name,
                    ["name"] = name,
                    ["content"] = result,
                });
            }
        }
    }

    /// <summary>Checks a server/key/model answers (Settings → Test connection).</summary>
    public async Task<string> TestAsync(Endpoint ep, CancellationToken ct)
    {
        var reply = await PostAsync(ep, new JsonObject
        {
            ["model"] = ep.Model,
            ["messages"] = new JsonArray { Msg("user", "Reply with just the word OK.") },
            ["max_tokens"] = 10,
        }, ct);
        return reply["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Trim() ?? "";
    }

    private static JsonObject Msg(string role, string content) => new() { ["role"] = role, ["content"] = content };

    private async Task<JsonNode> PostAsync(Endpoint ep, JsonObject body, CancellationToken ct)
    {
        var client = httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ep.BaseUrl}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(ep.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);

        HttpResponseMessage res;
        try { res = await client.SendAsync(req, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new AiException("The AI server took too long to answer."); }
        catch (HttpRequestException ex)
        { throw new AiException($"Couldn't reach the AI server at {ep.BaseUrl} ({ex.Message})."); }

        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                string? detail = null;
                try { detail = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>(); } catch (JsonException) { }
                logger.LogWarning("AI server {Url} returned {Status}", ep.BaseUrl, (int)res.StatusCode);
                throw new AiException((int)res.StatusCode switch
                {
                    401 or 403 => "The AI server rejected the API key.",
                    404 => $"The AI server didn't recognize the request — check the address (it usually ends in /v1) and the model name \"{ep.Model}\".",
                    429 => "The AI server is rate-limiting requests or the account is out of credit. Try again later.",
                    _ => $"The AI server returned an error ({(int)res.StatusCode})",
                } + (detail is null ? "" : $" Details: {Truncate(detail, 300)}"));
            }
            try { return JsonNode.Parse(text) ?? throw new AiException("The AI server sent an empty response."); }
            catch (JsonException) { throw new AiException("The AI server's response wasn't valid JSON — is the address an OpenAI-compatible API (usually ending in /v1)?"); }
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
