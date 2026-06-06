using System.Text.Json;

namespace MoneyTracker.Services;

public static class ReceiptExtractionHelper
{
    public const string Prompt = """
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

    public static ExtractedReceiptDto ParseResponse(string raw)
    {
        var content = raw.Trim();
        if (content.StartsWith("```"))
            content = string.Join('\n', content.Split('\n').Skip(1));
        if (content.EndsWith("```"))
            content = content[..content.LastIndexOf("```")];
        content = content.Trim();

        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var dto = JsonSerializer.Deserialize<ExtractedReceiptDto>(content, opts);
            return dto ?? Empty;
        }
        catch
        {
            return Empty;
        }
    }

    public static readonly ExtractedReceiptDto Empty =
        new(null, null, null, null, null);
}
