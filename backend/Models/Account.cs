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

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public AccountType Type { get; set; }
    public decimal OpeningBalance { get; set; }
    public int? InstitutionId { get; set; }
    public Institution? Institution { get; set; }

    [MaxLength(50)]
    public string? AccountNumber { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Transaction> Transactions { get; set; } = [];
}
