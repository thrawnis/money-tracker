using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

/// <summary>
/// A pair suggested by Utilities → Possible Transfers that the user marked
/// "Not a transfer", so it isn't suggested again. Stored with the smaller
/// transaction id first.
/// </summary>
public class DismissedTransferPair
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    public int TransactionIdA { get; set; }
    public int TransactionIdB { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
