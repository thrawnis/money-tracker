using System.Text;
using System.Text.Json;

namespace MoneyTracker.Services;

public class AnthropicReceiptExtractor(IHttpClientFactory httpFactory, IConfiguration config) : IReceiptExtractor
{
    public async Task<ExtractedReceiptDto> ExtractAsync(byte[] imageBytes, string mediaType)
    {
        var apiKey = config["Receipt:Anthropic:ApiKey"]
            ?? config["Anthropic:ApiKey"]   // legacy key name
            ?? throw new InvalidOperationException("Receipt:Anthropic:ApiKey not configured.");

        var model = config["Receipt:Anthropic:Model"] ?? "claude-haiku-4-5-20251001";

        var requestBody = new
        {
            model,
            max_tokens = 256,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type   = "image",
                            source = new
                            {
                                type       = "base64",
                                media_type = mediaType,
                                data       = Convert.ToBase64String(imageBytes),
                            }
                        },
                        new { type = "text", text = ReceiptExtractionHelper.Prompt }
                    }
                }
            }
        };

        var client = httpFactory.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var response = await client.PostAsync(
            "https://api.anthropic.com/v1/messages",
            new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));

        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var text = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "";

        return ReceiptExtractionHelper.ParseResponse(text);
    }
}
