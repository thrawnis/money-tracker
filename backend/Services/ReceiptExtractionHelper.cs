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
        - memo: 1-5 words describing what was purchased (e.g. the dish or items) —
          never include the merchant or restaurant name; it's already the payee
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
            return dto is null ? Empty : dto with { Memo = StripPayeePrefix(dto.Memo, dto.Payee) };
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>
    /// Drops the merchant name from the front of the memo ("Nopa - Burger"
    /// → "Burger"): it's already the payee, so the memo should say only what
    /// was bought. Leaves the memo alone if nothing would remain.
    /// </summary>
    public static string? StripPayeePrefix(string? memo, string? payee)
    {
        if (string.IsNullOrWhiteSpace(memo) || string.IsNullOrWhiteSpace(payee)) return memo;
        var m = memo.Trim();
        var p = payee.Trim();
        if (!m.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return m;
        var rest = m[p.Length..].TrimStart(' ', '-', '–', '—', ':', ',', '|', '·', '\'').Trim();
        // "Nopa's burger" → "burger"; but "Nopales salad" (payee "Nopa") is a
        // different word, so the name must end at a word boundary.
        if (rest.StartsWith("s ", StringComparison.OrdinalIgnoreCase) && m.Length > p.Length && m[p.Length] == '\'')
            rest = rest[2..].Trim();
        else if (m.Length > p.Length && char.IsLetterOrDigit(m[p.Length]))
            return m;
        return rest.Length == 0 ? m : rest;
    }

    public static readonly ExtractedReceiptDto Empty =
        new(null, null, null, null, null);
}
