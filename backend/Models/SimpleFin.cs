using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

/// <summary>
/// A user's link to SimpleFIN Bridge (https://www.simplefin.org) — a read-only
/// bank-data aggregator. One per user.
///
/// The access URL embeds a username/password (HTTP Basic credentials) that
/// grants read access to the user's balances and transaction history at
/// every bank they linked on SimpleFIN's side. It's a credential, so it's
/// encrypted with the user's DEK like every other sensitive field, and it is
/// never returned to the client after setup. Revoking it on SimpleFIN's side
/// (or Disconnect here) cuts off access immediately.
/// </summary>
public class SimpleFinConnection
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    [Required]
    public string AccessUrlEncrypted { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSyncAt { get; set; }

    // Messages SimpleFIN returned on the last sync (e.g. "a bank connection
    // needs attention"). Encrypted: they routinely name the bank and account.
    public string? LastErrorsEncrypted { get; set; }

    // Daily automatic balance update for balance-only accounts: the hour (0-23,
    // Pacific Time for every user) after which it runs each day, or null when
    // turned off. LastAutoUpdateDate is the Pacific day it last succeeded;
    // LastAutoAttemptAt spaces out retries after a failure.
    public int? DailyUpdateHour { get; set; } = 20;
    public DateOnly? LastAutoUpdateDate { get; set; }
    public DateTime? LastAutoAttemptAt { get; set; }

    public ICollection<SimpleFinAccount> Accounts { get; set; } = [];
}

/// <summary>
/// One bank account as SimpleFIN reports it, optionally linked to one of the
/// user's local accounts. Only linked accounts are synced.
/// </summary>
public class SimpleFinAccount
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;

    public int ConnectionId { get; set; }
    public SimpleFinConnection Connection { get; set; } = null!;

    // SimpleFIN's opaque account id — not personal data on its own, and kept
    // plaintext so a sync can match returned accounts to stored rows in SQL.
    [Required, MaxLength(200)]
    public string ExternalId { get; set; } = string.Empty;

    // Encrypted: an account name/institution pair identifies where the user banks.
    [Required]
    public string NameEncrypted { get; set; } = string.Empty;
    public string? OrgNameEncrypted { get; set; }

    // Kept plaintext for the same reason Transaction.Amount is — it's a number,
    // shown for context next to the link dropdown.
    public decimal? Balance { get; set; }
    public string? Currency { get; set; }

    public int? LinkedAccountId { get; set; }
    public Account? LinkedAccount { get; set; }

    // Balance-only: no transactions are imported; each sync (and the daily
    // update) records the reported value instead, and that value becomes the
    // linked account's balance. Meant for investment and retirement accounts,
    // whose value moves with the market rather than through transactions.
    public bool BalanceOnly { get; set; }

    // Newest transaction date already committed to the linked account from a
    // SimpleFIN sync. Advanced only when a sync draft is actually imported (see
    // ImportController.Import) — discarding a draft leaves it unchanged, so the
    // next sync simply fetches the same range again and nothing is skipped.
    public DateOnly? SyncedThrough { get; set; }

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    public ICollection<SimpleFinSkippedTransaction> SkippedTransactions { get; set; } = [];
}

/// <summary>
/// A bank transaction (by SimpleFIN's own id) that is already accounted for
/// in the linked register: imported by a sync, or matched to a transaction
/// that was already there. Later syncs recognize it by id instead of by
/// date and amount, so a charge that posts weeks late is still found, and a
/// genuinely new charge identical to an imported one isn't mistaken for it.
/// </summary>
public class SimpleFinImportedTransaction
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;

    public int SimpleFinAccountId { get; set; }
    public SimpleFinAccount SimpleFinAccount { get; set; } = null!;

    // SimpleFIN's opaque transaction id (plaintext, like SimpleFinAccount.ExternalId).
    [Required, MaxLength(200)]
    public string ExternalId { get; set; } = string.Empty;

    // The register transaction it became or was matched to. Null once that
    // transaction is deleted — the bank id stays recorded, so a transaction
    // deliberately deleted from the register isn't imported again.
    public int? TransactionId { get; set; }
    public Transaction? Transaction { get; set; }

    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A bank transaction the user unticked when reviewing a sync. Later syncs
/// leave it out instead of offering it again every time. Restoring it deletes
/// this row and rewinds the account's cursor so the next sync picks it up.
/// </summary>
public class SimpleFinSkippedTransaction
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;

    public int SimpleFinAccountId { get; set; }
    public SimpleFinAccount SimpleFinAccount { get; set; } = null!;

    // SimpleFIN's opaque transaction id, plaintext for the same reason as
    // SimpleFinAccount.ExternalId.
    [Required, MaxLength(200)]
    public string ExternalId { get; set; } = string.Empty;

    // Enough to show the user what was skipped. Payee text is encrypted like
    // every other payee/memo string; date and amount are plaintext as on Transaction.
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string? PayeeEncrypted { get; set; }

    public DateTime SkippedAt { get; set; } = DateTime.UtcNow;
}
