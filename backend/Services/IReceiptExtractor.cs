namespace MoneyTracker.Services;

public interface IReceiptExtractor
{
    Task<ExtractedReceiptDto> ExtractAsync(byte[] imageBytes, string mediaType);
}

public record ExtractedReceiptDto(
    string? Date,
    string? Payee,
    double? Amount,
    string? Memo,
    string? SuggestedCategory
);
