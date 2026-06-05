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
    public string Name { get; set; } = string.Empty;
    public AccountType Type { get; set; }
    public decimal OpeningBalance { get; set; }
    public string? Institution { get; set; }
    public string? AccountNumber { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Transaction> Transactions { get; set; } = [];
}
