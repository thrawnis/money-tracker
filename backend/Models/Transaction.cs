namespace MoneyTracker.Models;

public enum TransactionStatus
{
    Uncleared,
    Cleared,
    Reconciled
}

public class Transaction
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;

    public DateOnly Date { get; set; }
    public string? CheckNumber { get; set; }
    public int? PayeeId { get; set; }
    public Payee? Payee { get; set; }

    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    public string? Memo { get; set; }

    // Positive = deposit/credit, Negative = withdrawal/debit
    public decimal Amount { get; set; }

    public TransactionStatus Status { get; set; } = TransactionStatus.Uncleared;

    // For transfer transactions, points to the matching transaction in another account
    public int? TransferTransactionId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
