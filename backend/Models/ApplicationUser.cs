using Microsoft.AspNetCore.Identity;

namespace MoneyTracker.Models;

public class ApplicationUser : IdentityUser
{
    // Email is used as the username — set on registration
    public bool MfaEnrolled { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // AES-256-GCM encrypted DEK, encrypted with the application master key
    public string EncryptedDataKey { get; set; } = string.Empty;

    // Default sort applied to the account register on load; null means the
    // built-in default (date, newest first). Not sensitive — stored plaintext.
    public string? DefaultRegisterSortBy { get; set; }
    public string? DefaultRegisterSortDir { get; set; }

    // How many days ahead the register's "upcoming scheduled transactions"
    // section looks; null means the built-in default (31 days).
    public int? DefaultFutureDays { get; set; }

    // Opt-in (default false): pre-create recurring transactions as real,
    // editable Transaction rows once they're within AutoCreateFutureDays of
    // their due date, instead of only materializing them when actually due.
    public bool AutoCreateFutureTransactions { get; set; } = false;
    // Null means the built-in default (31 days). Independent of DefaultFutureDays
    // (that one only controls the register's preview window).
    public int? AutoCreateFutureDays { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<UserPasskeyCredential> PasskeyCredentials { get; set; } = [];
}
