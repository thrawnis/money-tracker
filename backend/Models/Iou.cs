using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

/// <summary>
/// Someone the user lends money to or borrows from, for IOUs. Names are
/// encrypted like payee names, so uniqueness is checked in memory.
/// </summary>
public class IouPerson
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    [Required]
    public string NameEncrypted { get; set; } = string.Empty;
    public string? NotesEncrypted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One amount owed between the user and a person. Amount is signed:
/// positive = they owe the user, negative = the user owes them. Optionally
/// tied to the purchase it came from (several people can share one).
///
/// PaidOffDate is either set by payments covering the amount (kept in step by
/// IouLedger) or set by hand (PaidOffManually — e.g. settled outside the app or
/// forgiven), in which case payments are never applied to it.
/// </summary>
public class IouEntry
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;

    public int PersonId { get; set; }
    public IouPerson Person { get; set; } = null!;

    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }

    public string? DescriptionEncrypted { get; set; }
    public string? NotesEncrypted { get; set; }

    // The purchase this IOU came from, if tagged from the register.
    public int? TransactionId { get; set; }
    public Transaction? Transaction { get; set; }

    public DateOnly? PaidOffDate { get; set; }
    public bool PaidOffManually { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum IouPaymentDirection { Received, Paid }

public enum IouPaymentMethod { Cash, Zelle, Venmo, PayPal, Check, BankTransfer, Other }

/// <summary>
/// Money that changed hands to settle IOUs: received from the person (pays
/// down what they owe) or paid to them (pays down what the user owes).
/// Amount is always positive; Direction gives the sense. It can be tied to
/// the bank transaction it arrived or left in, and pointed at one IOU to
/// cover first; anything left goes to that person's oldest open IOUs of the
/// matching direction, and any excess stays as a credit on their balance.
/// </summary>
public class IouPayment
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;

    public int PersonId { get; set; }
    public IouPerson Person { get; set; } = null!;

    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public IouPaymentDirection Direction { get; set; }
    public IouPaymentMethod Method { get; set; }

    public string? NotesEncrypted { get; set; }

    // The bank transaction the money arrived/left in (e.g. a Venmo deposit).
    public int? TransactionId { get; set; }
    public Transaction? Transaction { get; set; }

    // Apply to this IOU before the person's oldest open ones.
    public int? AppliesToEntryId { get; set; }
    public IouEntry? AppliesToEntry { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// How much of a payment went to an IOU. Derived data: rebuilt for a person
/// by IouLedger whenever their IOUs or payments change.
/// </summary>
public class IouAllocation
{
    public int Id { get; set; }

    public int PaymentId { get; set; }
    public IouPayment Payment { get; set; } = null!;

    public int EntryId { get; set; }
    public IouEntry Entry { get; set; } = null!;

    public decimal Amount { get; set; }
}
