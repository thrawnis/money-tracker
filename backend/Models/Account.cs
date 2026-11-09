using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

public enum AccountType
{
    Checking,
    Savings,
    CreditCard,
    Cash,
    Loan,
    Investment,
    Other
}

public class Account
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public AccountType Type { get; set; }
    public decimal OpeningBalance { get; set; }
    public int? InstitutionId { get; set; }
    public Institution? Institution { get; set; }

    // Stored encrypted
    public string? AccountNumberEncrypted { get; set; }

    // Stored encrypted
    public string? NotesEncrypted { get; set; }

    // Cash-flow forecast warns when the projected balance would drop below
    // this. Null means the default: $0 for checking, savings and cash; no
    // warning for other types (a credit card normally runs negative).
    public decimal? LowBalanceThreshold { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Transaction> Transactions { get; set; } = [];
}
