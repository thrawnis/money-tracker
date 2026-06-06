using System.Text;
using System.Text.Json;

namespace MoneyTracker.Services;

public class OpenAiReceiptExtractor(IHttpClientFactory httpFactory, IConfiguration config) : IReceiptExtractor
{
    public async Task<ExtractedReceiptDto> ExtractAsync(byte[] imageBytes, string mediaType)
    {
        var apiKey  = config["Receipt:OpenAi:ApiKey"]
            ?? throw new InvalidOperationException("Receipt:OpenAi:ApiKey not configured.");
        var model   = config["Receipt:OpenAi:Model"]   ?? "gpt-4o-mini";
        var baseUrl = config["Receipt:OpenAi:BaseUrl"]?.TrimEnd('/') ?? "https://api.openai.com/v1";

        var dataUrl = $"data:{mediaType};base64,{Convert.ToBase64String(imageBytes)}";

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
                        new { type = "image_url", image_url = new { url = dataUrl } },
                        new { type = "text",      text      = ReceiptExtractionHelper.Prompt }
                    }
                }
            }
        };

        var client = httpFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        var response = await client.PostAsync(
            $"{baseUrl}/chat/completions",
            new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));

        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        return ReceiptExtractionHelper.ParseResponse(text);
    }
}
