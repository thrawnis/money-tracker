using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

/// <summary>
/// A group on Utilities → Duplicate Names the user marked "Not duplicates".
/// Identified by its kind and member ids only (no names — those are
/// encrypted, and even a hash of a short name could be guessed). If the
/// group's membership changes (a new similar name, or a member merged or
/// deleted), its key no longer matches and it's suggested again.
/// </summary>
public class DismissedNameGroup
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    /// <summary>"payee" or "category".</summary>
    [MaxLength(16)]
    public string Kind { get; set; } = null!;

    /// <summary>Sorted member ids, comma-separated.</summary>
    [MaxLength(2000)]
    public string MemberIds { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
