using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

public class Payee
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    // Stored encrypted with the user's DEK
    public string NameEncrypted { get; set; } = string.Empty;

    public int? DefaultCategoryId { get; set; }
    public Category? DefaultCategory { get; set; }

    // Opt-out for payees that legitimately span many categories (e.g. "Amazon",
    // "Cash Withdrawal") — when true, the app never auto-populates or
    // auto-backfills DefaultCategoryId for this payee (see TransactionForm's
    // create/backfill logic). Doesn't affect manually setting one from the
    // Payees page; that's an explicit user action, not automatic.
    public bool BlockAutoDefaultCategory { get; set; } = false;

    public ICollection<Transaction> Transactions { get; set; } = [];
}
