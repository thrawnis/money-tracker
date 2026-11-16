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
            %CHANGES%
            - Be concise. Format money like $1,234.56. Use short bullet lists or small tables (Markdown) when helpful.
            """.Replace("%CHANGES%", user.AiAllowProposals
                ? "- You can't change anything directly, but you can PROPOSE changes with the propose_* tools (set categories, link transfers, "
                  + "rename or merge payees, add a payee mapping rule, create a bill). Look things up first, then propose. Each proposal appears "
                  + "under your answer for the user to review and apply — say what you proposed and never claim it's done. "
                  + "Only propose what the user asked for or clearly benefits from; prefer one well-scoped proposal over many."
                : "- You can only read data; you can't change anything. If asked to, explain how to do it in the app instead "
                  + "(or that they can let you suggest changes in Settings → AI Assistant).");
    }

    /// <summary>Progress while answering: "status" (what it's doing) or "delta" (the next piece of the answer).</summary>
    public record Progress(string Kind, string Text);

    /// <summary>
    /// Asks the model. <paramref name="history"/> is the earlier turns of this
    /// conversation (role "user"/"assistant"); returns the answer text.
    /// <paramref name="progress"/>, if given, hears what's happening as it happens.
    /// </summary>
    public async Task<string> AskAsync(ApplicationUser user, IEnumerable<(string Role, string Content)> history, string question,
        CancellationToken ct, Func<Progress, Task>? progress = null)
    {
        var ep = Server.Endpoint ?? throw new AiException("The AI assistant isn't set up on this server. " + Server.Problem);
        var report = progress ?? (_ => Task.CompletedTask);
        var messages = new JsonArray { Msg("system", SystemPrompt(user)) };
        foreach (var (role, content) in history) messages.Add(Msg(role, content));
        messages.Add(Msg("user", question));
        var toolDefs = AiTools.Definitions(user);

        for (var round = 0; ; round++)
        {
            await report(new Progress("status", round == 0 ? "Thinking…" : "Reading what it found…"));
            var body = new JsonObject
            {
                ["model"] = ep.Model,
                ["messages"] = messages.DeepClone(),
                ["temperature"] = 0.2,
                ["stream"] = true,
            };
            // Last round: no tools, so the model has to answer with what it has.
            if (round < MaxToolRounds) { body["tools"] = toolDefs.DeepClone(); body["tool_choice"] = "auto"; }

            var writing = false;
            var (content, calls) = await StreamRoundAsync(ep, body, async delta =>
            {
                if (!writing) { writing = true; await report(new Progress("status", "Writing the answer…")); }
                await report(new Progress("delta", delta));
            }, ct);

            if (calls.Count == 0)
            {
                var text = content.Trim();
                return text.Length == 0 ? "(The assistant didn't return an answer.)" : text;
            }

            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = content.Length == 0 ? null : content,
                ["tool_calls"] = calls.DeepClone(),
            });
            foreach (var call in calls)
            {
                var name = call?["function"]?["name"]?.GetValue<string>() ?? "";
                var argsNode = call?["function"]?["arguments"];
                // Most servers send arguments as a JSON string; some send an object.
                var args = argsNode is JsonValue v && v.TryGetValue<string>(out var s) ? s : argsNode?.ToJsonString() ?? "{}";
                logger.LogDebug("AI tool call {Tool} for user {UserId}", name, user.Id);
                await report(new Progress("status", AiTools.Describe(name, args)));
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

    /// <summary>
    /// One chat-completions request with stream=true: passes answer text to
    /// <paramref name="onDelta"/> as it arrives and assembles any tool calls
    /// (whose name/arguments come in pieces). Servers that ignore "stream" and
    /// reply with one JSON body work too.
    /// </summary>
    private async Task<(string Content, JsonArray Calls)> StreamRoundAsync(GeneralAi.Endpoint ep, JsonObject body,
        Func<string, Task> onDelta, CancellationToken ct)
    {
        using var res = await SendAsync(ep, body, ct, HttpCompletionOption.ResponseHeadersRead);
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(ep, res, ct);

        var content = new StringBuilder();
        var calls = new SortedDictionary<int, JsonObject>();

        if (res.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var whole = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            var msg = whole?["choices"]?[0]?["message"] ?? throw new AiException("The AI server sent back an unexpected response.");
            var text = msg["content"] is JsonValue cv && cv.TryGetValue<string>(out var ctext) ? ctext : "";
            var list = msg["tool_calls"] as JsonArray ?? [];
            if (list.Count == 0 && text.Length > 0) await onDelta(text);
            return (text, (JsonArray)list.DeepClone());
        }

        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is string line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            JsonNode? chunk;
            try { chunk = JsonNode.Parse(data); } catch (JsonException) { continue; }
            if (chunk?["error"] is JsonNode err)
                throw new AiException("The AI server reported an error: " + (err["message"]?.ToString() ?? err.ToJsonString()));
            var delta = chunk?["choices"]?[0]?["delta"];
            if (delta is null) continue;

            if (delta["content"] is JsonValue c && c.TryGetValue<string>(out var piece) && piece.Length > 0)
            {
                content.Append(piece);
                // Text alongside tool calls isn't the answer; only stream when no tools were asked for.
                if (calls.Count == 0) await onDelta(piece);
            }
            if (delta["tool_calls"] is JsonArray parts)
                foreach (var part in parts)
                {
                    var index = part?["index"]?.GetValue<int>() ?? calls.Count;
                    if (!calls.TryGetValue(index, out var call))
                        calls[index] = call = new JsonObject { ["id"] = null, ["type"] = "function", ["function"] = new JsonObject { ["name"] = "", ["arguments"] = "" } };
                    if (part?["id"]?.GetValue<string>() is string id) call["id"] = id;
                    var fn = (JsonObject)call["function"]!;
                    if (part?["function"]?["name"]?.GetValue<string>() is string n) fn["name"] = fn["name"]!.GetValue<string>() + n;
                    var a = part?["function"]?["arguments"];
                    if (a is JsonValue av && av.TryGetValue<string>(out var astr)) fn["arguments"] = fn["arguments"]!.GetValue<string>() + astr;
                    else if (a is JsonObject ao) fn["arguments"] = ao.ToJsonString();
                }
        }
        var result = new JsonArray();
        foreach (var (index, call) in calls)
        {
            call["id"] ??= $"call_{index}";
            result.Add(call);
        }
        return (content.ToString(), result);
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
        using var res = await SendAsync(ep, body, ct, HttpCompletionOption.ResponseContentRead);
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(ep, res, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        try { return JsonNode.Parse(text) ?? throw new AiException("The AI server sent an empty response."); }
        catch (JsonException) { throw new AiException("The AI server's response wasn't valid JSON — is the address an OpenAI-compatible API (usually ending in /v1)?"); }
    }

    private async Task<HttpResponseMessage> SendAsync(GeneralAi.Endpoint ep, JsonObject body, CancellationToken ct, HttpCompletionOption completion)
    {
        var client = httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ep.BaseUrl}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(ep.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);
        try { return await client.SendAsync(req, completion, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new AiException("The AI server took too long to answer."); }
        catch (HttpRequestException ex)
        { throw new AiException($"Couldn't reach the AI server at {ep.BaseUrl} ({ex.Message})."); }
    }

    /// <summary>A plain-words error for a failed response (see the cases below).</summary>
    private async Task<AiException> ErrorAsync(GeneralAi.Endpoint ep, HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
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
        return new AiException(message);
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
