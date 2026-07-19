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

    // Null when the transaction has Splits — the split lines carry the
    // category breakdown instead of a single category on the transaction.
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

    // Set when this transaction was materialized from a recurring schedule —
    // either because it became due, or (if the user enabled the preference)
    // pre-created ahead of time so it's editable before it's actually due.
    // Never set/changed by the public Create/Update endpoints, only by
    // ScheduledTransactionPostingService. Used to dedupe: the same occurrence
    // (ScheduledTransactionId, Date) is never materialized twice, and an
    // already-materialized occurrence is excluded from the "upcoming
    // scheduled transactions" preview (it's showing as a real transaction
    // instead). SetNull on schedule deletion — deleting a bill shouldn't
    // delete transactions it already generated.
    public int? ScheduledTransactionId { get; set; }
    public ScheduledTransaction? ScheduledTransaction { get; set; }

    // Present only when this transaction is split across multiple categories;
    // when non-empty, CategoryId is null and Splits.Sum(Amount) == Amount.
    public ICollection<TransactionSplit> Splits { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class TransactionSplit
{
    public int Id { get; set; }
    public int TransactionId { get; set; }
    public Transaction Transaction { get; set; } = null!;

    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    public decimal Amount { get; set; }

    // Stored encrypted
    public string? MemoEncrypted { get; set; }
}
