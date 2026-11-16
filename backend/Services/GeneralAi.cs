namespace MoneyTracker.Services;

/// <summary>
/// The AI settings from .env:
///   AI_PROVIDER / AI_BASE_URL / AI_API_KEY / AI_MODEL           (Ai:*)      — the AI server
///   RECEIPT_PROVIDER / RECEIPT_BASE_URL / RECEIPT_API_KEY / RECEIPT_MODEL (Receipt:*) — receipts only,
///     when they need something different; blank RECEIPT_PROVIDER means "use the AI settings".
/// Providers: openai (any OpenAI-compatible server via the base URL) | ollama | anthropic | gemini.
/// The older per-provider keys (OLLAMA_*, OPENAI_*, ANTHROPIC_*, GEMINI_*) still feed receipt
/// scanning as fallbacks, through Receipt:Ollama:* etc.
/// </summary>
public static class GeneralAi
{
    private static readonly string[] Providers = ["openai", "ollama", "anthropic", "gemini"];

    private static string? Get(IConfiguration c, string key) => string.IsNullOrWhiteSpace(c[key]) ? null : c[key]!.Trim();

    private static string? ProviderAt(IConfiguration c, string section) =>
        Get(c, $"{section}:Provider")?.ToLowerInvariant() is string p && Providers.Contains(p) ? p
        // A base URL alone means an OpenAI-compatible server.
        : Get(c, $"{section}:BaseUrl") is not null ? "openai" : null;

    /// <summary>The AI_PROVIDER, or null when no AI server is set up.</summary>
    public static string? Provider(IConfiguration c) => ProviderAt(c, "Ai");

    public static string StripV1(string url)
    {
        var u = url.TrimEnd('/');
        return u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? u[..^3] : u;
    }

    public record Endpoint(string Provider, string BaseUrl, string? ApiKey, string Model);

    /// <summary>The AI server's OpenAI-compatible endpoint (for the assistant), or null with why it isn't usable.</summary>
    public static (Endpoint? Endpoint, string? Problem) Resolve(IConfiguration c)
    {
        var provider = Provider(c);
        if (provider is null) return (null, "No AI server is set up. Set AI_PROVIDER (and its key/model) in .env.");
        var key = Get(c, "Ai:ApiKey");
        var url = Get(c, "Ai:BaseUrl")?.TrimEnd('/');
        var model = Get(c, "Ai:Model");
        switch (provider)
        {
            case "openai":
                url ??= "https://api.openai.com/v1";
                if (key is null && url.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase))
                    return (null, "AI_API_KEY isn't set for OpenAI.");
                return (new Endpoint(provider, url, key, model ?? "gpt-4o-mini"), null);
            case "anthropic":
                if (key is null) return (null, "AI_API_KEY isn't set for Anthropic.");
                return (new Endpoint(provider, url ?? "https://api.anthropic.com/v1", key, model ?? "claude-haiku-4-5-20251001"), null);
            case "gemini":
                if (key is null) return (null, "AI_API_KEY isn't set for Gemini.");
                return (new Endpoint(provider, url ?? "https://generativelanguage.googleapis.com/v1beta/openai", key, model ?? "gemini-2.0-flash"), null);
            default: // ollama: AI_BASE_URL is the Ollama server (blank = the bundled container); its OpenAI API is under /v1
                if (model is null) return (null, "AI_MODEL isn't set — choose an Ollama model that supports tools (e.g. qwen2.5:14b).");
                return (new Endpoint(provider, StripV1(url ?? "http://ollama:11434") + "/v1", key, model), null);
        }
    }

    /// <summary>
    /// Config overrides that point receipt scanning at the right provider: the
    /// RECEIPT_* settings if RECEIPT_PROVIDER is set, otherwise the AI_* ones,
    /// otherwise the bundled Ollama (as before). Unset values fall back to the
    /// older per-provider keys and the extractors' defaults.
    /// </summary>
    public static Dictionary<string, string?> ReceiptOverrides(IConfiguration c)
    {
        var section = ProviderAt(c, "Receipt") is not null ? "Receipt" : Provider(c) is not null ? "Ai" : null;
        var provider = section is null ? "ollama" : ProviderAt(c, section)!;
        var values = new Dictionary<string, string?> { ["Receipt:Provider"] = provider };
        if (section is null) return values;
        var target = provider switch { "openai" => "OpenAi", "anthropic" => "Anthropic", "gemini" => "Gemini", _ => "Ollama" };
        if (Get(c, $"{section}:ApiKey") is string k) values[$"Receipt:{target}:ApiKey"] = k;
        if (Get(c, $"{section}:Model") is string m) values[$"Receipt:{target}:Model"] = m;
        // Ollama's receipt client uses its native API (no /v1); OpenAI's uses /v1.
        if (Get(c, $"{section}:BaseUrl") is string u) values[$"Receipt:{target}:BaseUrl"] = target == "Ollama" ? StripV1(u) : u;
        return values;
    }
}
