using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// The "Ask" assistant: sends the user's question to the AI server set in .env
/// (GeneralAi: AI_PROVIDER / AI_BASE_URL / AI_API_KEY / AI_MODEL) through its
/// OpenAI-compatible chat-completions API, with read-only tools (AiTools) it
/// can call to look things up, and returns its answer.
/// </summary>
public class AiAssistant(IHttpClientFactory httpFactory, IConfiguration config, AiTools tools, ILogger<AiAssistant> logger)
{
    public const string HttpClientName = "ai";
    private const int MaxToolRounds = 8;

    public class AiException(string message) : Exception(message);

    /// <summary>The configured server, or null with the reason it isn't usable.</summary>
    public (GeneralAi.Endpoint? Endpoint, string? Problem) Server => GeneralAi.Resolve(config);

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
        var ep = Server.Endpoint ?? throw new AiException("The AI assistant isn't set up on this server. " + Server.Problem);
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
    public async Task<string> TestAsync(GeneralAi.Endpoint ep, CancellationToken ct)
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

    private async Task<JsonNode> PostAsync(GeneralAi.Endpoint ep, JsonObject body, CancellationToken ct)
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
                var status = (int)res.StatusCode;
                var detail = ErrorDetail(text);
                logger.LogWarning("AI server {Url} returned {Status}: {Detail}", ep.BaseUrl, status, detail);
                var host = Uri.TryCreate(ep.BaseUrl, UriKind.Absolute, out var u) ? u.Authority : ep.BaseUrl;
                var aboutModel = detail?.Contains("model", StringComparison.OrdinalIgnoreCase) == true;
                var message = status switch
                {
                    _ when aboutModel && status is 400 or 401 or 403 or 404 =>
                        $"The AI server at {host} won't use the model \"{ep.Model}\" with this API key.",
                    401 or 403 => $"The AI server at {host} rejected the API key.",
                    404 => $"The AI server at {host} didn't recognize the request — check the address (it usually ends in /v1) and the model name \"{ep.Model}\".",
                    429 => $"The AI server at {host} is rate-limiting requests or the account is out of credit. Try again later.",
                    _ => $"The AI server at {host} returned an error ({status}).",
                };
                if (detail is not null) message += $" Details: {Truncate(detail, 300)}";
                // Say which model names this key can use — the usual fix is a typo or alias mismatch.
                if (aboutModel || status == 404)
                {
                    var models = await ModelsAsync(ep, ct);
                    if (models is { Count: > 0 })
                        message += $" Models this key can use: {string.Join(", ", models.Take(20))}{(models.Count > 20 ? ", …" : "")}.";
                }
                throw new AiException(message);
            }
            try { return JsonNode.Parse(text) ?? throw new AiException("The AI server sent an empty response."); }
            catch (JsonException) { throw new AiException("The AI server's response wasn't valid JSON — is the address an OpenAI-compatible API (usually ending in /v1)?"); }
        }
    }

    // OpenAI-style {"error":{"message":…}}, or {"error":"…"} / {"detail":…} from other servers.
    private static string? ErrorDetail(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            var e = n?["error"];
            var d = e is JsonObject o ? o["message"] : e ?? n?["detail"] ?? n?["message"];
            return d is JsonValue v && v.TryGetValue<string>(out var str) ? str : d?.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return string.IsNullOrWhiteSpace(body) ? null : body.Trim();
        }
    }

    /// <summary>The model ids GET {base}/models lists for this key, or null if that fails.</summary>
    private async Task<List<string>?> ModelsAsync(GeneralAi.Endpoint ep, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ep.BaseUrl}/models");
            if (!string.IsNullOrWhiteSpace(ep.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);
            using var res = await httpFactory.CreateClient(HttpClientName).SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            return (JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["data"] as JsonArray)?
                .Select(m => m?["id"]?.GetValue<string>()).OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
