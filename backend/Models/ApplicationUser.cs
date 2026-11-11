using Microsoft.AspNetCore.Identity;

namespace MoneyTracker.Models;

public class ApplicationUser : IdentityUser
{
    // Email is used as the username — set on registration
    // True when the user has at least one second factor: a passkey and/or the
    // authenticator app. Kept in step with the two below by AuthController.
    public bool MfaEnrolled { get; set; } = false;
    // The authenticator app (TOTP) is set up. Passkeys live in
    // PasskeyCredentials; a user may have both, and signs in with either.
    public bool TotpEnrolled { get; set; } = false;
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
    // Daily email digest (see EmailDigestService). Off by default; sent once a
    // day at DigestHour in the user's time zone. Amounts are left out unless
    // DigestIncludeAmounts, since email isn't end-to-end protected. That
    // setting covers every email: amounts and payee names only when it's on.
    public bool DigestEnabled { get; set; } = false;
    public int DigestHour { get; set; } = 7;
    public bool DigestIncludeAmounts { get; set; } = false;
    public bool DigestOnlyWhenNews { get; set; } = true;
    public DateOnly? DigestLastSentOn { get; set; }

    // Email on every sign-in (not silent session refreshes). On by default;
    // only sent when the server has email set up.
    public bool LoginAlertsEnabled { get; set; } = true;

    // Bank sync alerts (see BankSyncAlertService). Both off by default.
    // SyncAlertsEnabled: after a nightly sync, what arrived per account.
    // LargeTxAlertsEnabled: any sync that brings in a transaction whose size
    // (money in or out) is at least LargeTxThreshold.
    public bool SyncAlertsEnabled { get; set; } = false;
    public bool LargeTxAlertsEnabled { get; set; } = false;
    public decimal LargeTxThreshold { get; set; } = 500m;

    // Weekly email digest (see WeeklyDigestService): off by default; sent on
    // WeeklyDigestDay (0 = Sunday) at WeeklyDigestHour in the user's time zone.
    public bool WeeklyDigestEnabled { get; set; } = false;
    public int WeeklyDigestDay { get; set; } = 0;
    public int WeeklyDigestHour { get; set; } = 8;
    public DateOnly? WeeklyDigestLastSentOn { get; set; }

    public bool VoidCategoriesMigrated { get; set; } = false;

    // Sibling to VoidCategoriesMigrated: the same "VOID ..." bank-export
    // placeholder, but landing in the payee/description column instead of a
    // category column (the more common case for plain bank CSVs, which
    // usually have no category column at all). See VoidPayeeMigrationService.
    public bool VoidPayeesMigrated { get; set; } = false;

    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<UserPasskeyCredential> PasskeyCredentials { get; set; } = [];
}
