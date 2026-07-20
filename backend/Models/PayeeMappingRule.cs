using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

/// <summary>
/// A user-defined (or user-confirmed, learned-from-import) rule that maps a raw
/// payee string seen in an imported file — e.g. "AMAZON F98797" — to one of the
/// user's existing Payees. Applied automatically on future imports so recurring
/// merchant name variants don't keep creating new duplicate payees.
/// </summary>
public class PayeeMappingRule
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    // Stored encrypted with the user's DEK (same sensitivity as a payee name).
    // Interpreted per IsRegex: false => exact match, or a glob with '*'
    // wildcards (e.g. "Amazon*"); true => the pattern is a regular expression.
    [Required]
    public string PatternEncrypted { get; set; } = string.Empty;

    public bool IsRegex { get; set; }

    public int TargetPayeeId { get; set; }
    public Payee TargetPayee { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
