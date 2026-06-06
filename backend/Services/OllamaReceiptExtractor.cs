using System.Text;
using System.Text.Json;

namespace MoneyTracker.Services;

public class OllamaReceiptExtractor(IHttpClientFactory httpFactory, IConfiguration config) : IReceiptExtractor
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public async Task<ExtractedReceiptDto> ExtractAsync(byte[] imageBytes, string mediaType)
    {
        var baseUrl = config["Receipt:Ollama:BaseUrl"]?.TrimEnd('/') ?? "http://ollama:11434";
        var model   = config["Receipt:Ollama:Model"] ?? "llava";

        var requestBody = new
        {
            model,
            stream = false,
            messages = new[]
            {
                new
                {
                    role    = "user",
                    content = ReceiptExtractionHelper.Prompt,
                    images  = new[] { Convert.ToBase64String(imageBytes) },
                }
            }
        };

        var client = httpFactory.CreateClient();
        var response = await client.PostAsync(
            $"{baseUrl}/api/chat",
            new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));

        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var text = doc.RootElement
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        return ReceiptExtractionHelper.ParseResponse(text);
    }
}
