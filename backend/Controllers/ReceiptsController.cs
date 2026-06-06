using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/receipts")]
public class ReceiptsController(IConfiguration config, IHttpClientFactory httpFactory) : ControllerBase
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    [HttpPost("extract")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB
    public async Task<IActionResult> Extract(IFormFile image)
    {
        var apiKey = config["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(503, new { message = "Receipt scanning is not configured. Add ANTHROPIC_API_KEY to your .env file." });

        if (image is null || image.Length == 0)
            return BadRequest(new { message = "No image provided." });

        // Read and base64-encode the image
        using var ms = new MemoryStream();
        await image.CopyToAsync(ms);
        var base64 = Convert.ToBase64String(ms.ToArray());
        var mediaType = image.ContentType switch
        {
            "image/png"  => "image/png",
            "image/webp" => "image/webp",
            "image/gif"  => "image/gif",
            _            => "image/jpeg",
        };

        var prompt = """
            Analyze this receipt image and extract the following information.
            Respond ONLY with a valid JSON object — no markdown, no explanation.

            {
              "date": "YYYY-MM-DD or null if not found",
              "payee": "merchant or store name, or null",
              "amount": number (total amount as a negative number since it's a purchase, or null),
              "memo": "brief description of purchase, or null",
              "suggestedCategory": "best guess category name, or null"
            }

            Rules:
            - date: use today's date context if only partial info is available
            - payee: use the clean business name, not an abbreviated code
            - amount: the final total paid (negative for expenses, positive for refunds)
            - memo: 1-5 words describing what was purchased
            - suggestedCategory: pick the most appropriate from common personal finance categories
              (e.g. Groceries, Dining, Gas, Healthcare, Shopping, Entertainment, Utilities, etc.)
            """;

        var requestBody = new
        {
            model = "claude-haiku-4-5-20251001",
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
                            type = "image",
                            source = new
                            {
                                type = "base64",
                                media_type = mediaType,
                                data = base64,
                            }
                        },
                        new { type = "text", text = prompt }
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

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            return StatusCode(502, new { message = $"AI service error: {err}" });
        }

        var responseText = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseText);
        var content = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "{}";

        // Strip markdown code fences if model wrapped the JSON
        content = content.Trim();
        if (content.StartsWith("```")) content = string.Join('\n', content.Split('\n').Skip(1));
        if (content.EndsWith("```")) content = content[..content.LastIndexOf("```")];
        content = content.Trim();

        try
        {
            using var result = JsonDocument.Parse(content);
            return Ok(result.RootElement.Clone());
        }
        catch
        {
            return Ok(new { date = (string?)null, payee = (string?)null, amount = (double?)null, memo = (string?)null, suggestedCategory = (string?)null });
        }
    }
}
