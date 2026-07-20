using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

/// <summary>
/// An in-progress import that hasn't been committed yet — created automatically
/// the moment a file is previewed for a single account, so a user interrupted
/// partway through review (browser closed, tab lost, etc.) doesn't lose the
/// upload or their duplicate/payee-mapping review decisions. One draft per
/// (user, account); re-previewing the same account replaces its draft.
/// Cleared automatically once the import is committed, or explicitly discarded.
/// </summary>
public class ImportDraft
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;

    public string FileName { get; set; } = string.Empty;

    // The original uploaded file's bytes, base64-encoded then encrypted with the
    // user's DEK — resuming re-runs the exact same parse/preview/import logic
    // used for a fresh upload, so nothing about duplicate detection or transfer
    // matching needs to be duplicated or re-derived from a bespoke schema.
    [Required]
    public string FileContentEncrypted { get; set; } = string.Empty;

    public int RowCount { get; set; }

    // Review decisions made so far, so resuming restores exactly where the user
    // left off instead of forcing them to redo the review. Both are JSON blobs
    // (not encrypted — they only reference other encrypted/plain records by id):
    //   IncludeDuplicateIdsJson: int[] of existing transaction ids to import-anyway
    //   PayeeOverridesJson: { [rawPayeeText]: targetPayeeId } chosen resolutions
    public string? IncludeDuplicateIdsJson { get; set; }
    public string? PayeeOverridesJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
