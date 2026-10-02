using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

/// <summary>
/// An account's value on one day, as reported by the bank (SimpleFIN) for a
/// balance-only account. One row per account per day; a later update the same
/// day replaces it. Kept against the local account, so the history survives
/// relinking or disconnecting SimpleFIN. Amounts are plaintext, like
/// Transaction.Amount.
/// </summary>
public class AccountValueSnapshot
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;

    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;

    // The day the value is for, in the user's time zone (from SimpleFIN's
    // balance date, which can lag the day it was fetched).
    public DateOnly Date { get; set; }
    public decimal Balance { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One investment position on one day, when the brokerage reports holdings.
/// Identity, symbol and name are all encrypted (they say what the user owns);
/// a day's holdings for an account are always replaced as a set, and history
/// is grouped by the decrypted key in memory.
/// </summary>
public class HoldingSnapshot
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;

    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;

    public DateOnly Date { get; set; }

    // SimpleFIN's holding id (or the symbol when the feed has no id).
    [Required]
    public string HoldingKeyEncrypted { get; set; } = string.Empty;

    public string? SymbolEncrypted { get; set; }
    public string? DescriptionEncrypted { get; set; }

    public decimal? Shares { get; set; }
    public decimal? MarketValue { get; set; }
    public decimal? CostBasis { get; set; }
}
