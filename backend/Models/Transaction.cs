using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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

    // The date the transaction settled/posted to the statement (often 1-3 days after Date)
    public DateOnly? PostDate { get; set; }

    // Stored encrypted (AES-256-GCM); no MaxLength since ciphertext is longer than plaintext
    public string? CheckNumberEncrypted { get; set; }

    public int? PayeeId { get; set; }
    public Payee? Payee { get; set; }

    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    // Stored encrypted
    public string? MemoEncrypted { get; set; }

    // Kept as plaintext decimal for SQL aggregation (balance, reports)
    // Positive = deposit/credit, Negative = withdrawal/debit
    public decimal Amount { get; set; }

    public TransactionStatus Status { get; set; } = TransactionStatus.Uncleared;

    // For transfer transactions, points to the matching transaction in another account
    public int? TransferTransactionId { get; set; }
    public int? TransferAccountId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
