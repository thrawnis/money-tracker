using Microsoft.AspNetCore.Identity;

namespace MoneyTracker.Models;

public class ApplicationUser : IdentityUser
{
    // Email is used as the username — set on registration
    public bool MfaEnrolled { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // AES-256-GCM encrypted DEK, encrypted with the application master key
    public string EncryptedDataKey { get; set; } = string.Empty;

    // IANA timezone id ("America/Chicago") used to resolve "today" for
    // balances, report period shortcuts, and scheduled-bill posting. Null =
    // UTC, the historical behavior. See UserClock.
    public string? TimeZoneId { get; set; }

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

    // One-time cleanup, run on first login/refresh after the Void feature
    // shipped: transactions whose category was literally named "VOID ..."
    // (bank exports use that as a category placeholder for voided/reversed
    // transactions) get converted to real voided transactions instead.
    // See VoidCategoryMigrationService.
    public bool VoidCategoriesMigrated { get; set; } = false;

    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<UserPasskeyCredential> PasskeyCredentials { get; set; } = [];
}
