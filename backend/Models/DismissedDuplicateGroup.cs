using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

/// <summary>
/// Records that a user chose to ignore a specific "possible duplicate" group
/// (same account/date/amount) on the Find Duplicates screen. SignatureHash
/// captures the identity/values of every transaction in the group at the
/// time it was dismissed — if any of those change, or another transaction
/// joins the group, the live signature no longer matches and the group is
/// shown again (see DuplicatesController.ComputeSignature).
/// </summary>
public class DismissedDuplicateGroup
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    public int AccountId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }

    [Required]
    public string SignatureHash { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
