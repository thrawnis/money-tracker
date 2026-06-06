using System.Text;
using System.Text.Json;

namespace MoneyTracker.Services;

public class GeminiReceiptExtractor(IHttpClientFactory httpFactory, IConfiguration config) : IReceiptExtractor
{
    public async Task<ExtractedReceiptDto> ExtractAsync(byte[] imageBytes, string mediaType)
    {
        var apiKey = config["Receipt:Gemini:ApiKey"]
            ?? throw new InvalidOperationException("Receipt:Gemini:ApiKey not configured.");
        var model  = config["Receipt:Gemini:Model"] ?? "gemini-1.5-flash";

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new
                        {
                            inline_data = new
                            {
                                mime_type = mediaType,
                                data      = Convert.ToBase64String(imageBytes),
                            }
                        },
                        new { text = ReceiptExtractionHelper.Prompt }
                    }
                }
            }
        };

        var client = httpFactory.CreateClient();
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        var response = await client.PostAsync(
            url,
            new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));

        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "";

        return ReceiptExtractionHelper.ParseResponse(text);
    }
}
