using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

public enum FrequencyUnit
{
    Days,
    Weeks,
    Months,
    Years,
}

public class ScheduledTransaction
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;

    public int? PayeeId { get; set; }
    public Payee? Payee { get; set; }

    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    // Set when this is a scheduled transfer; points to the destination account
    public int? TransferAccountId { get; set; }

    // Stored encrypted
    public string? MemoEncrypted { get; set; }

    public decimal Amount { get; set; }

    // e.g. every 2 weeks → FrequencyInterval=2, FrequencyUnit=Weeks
    public int FrequencyInterval { get; set; } = 1;
    public FrequencyUnit FrequencyUnit { get; set; } = FrequencyUnit.Months;
    public DateOnly NextDueDate { get; set; }

    // Only meaningful when FrequencyUnit == Weeks: a bitmask of specific
    // weekdays (bit N = System.DayOfWeek value N, so Sunday=1, Monday=2, ...
    // Saturday=64) for schedules like "every Mon/Wed/Fri" instead of a flat
    // N-week interval. Null/0 means the classic FrequencyInterval-weeks
    // behavior applies. Always null for non-Weeks units — enforced by the
    // controller, not just left unused, so it can't linger stale.
    public int? DaysOfWeekMask { get; set; }

    // How many days before due date to show the reminder
    public int ReminderDays { get; set; } = 3;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
