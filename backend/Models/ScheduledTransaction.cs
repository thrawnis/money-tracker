using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

public enum RecurrenceFrequency
{
    Once,
    Weekly,
    BiWeekly,
    Monthly,
    BiMonthly,
    Quarterly,
    SemiAnnually,
    Annually
}

public class ScheduledTransaction
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;

    public int? PayeeId { get; set; }
    public Payee? Payee { get; set; }

    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    [MaxLength(500)]
    public string? Memo { get; set; }

    public decimal Amount { get; set; }

    public RecurrenceFrequency Frequency { get; set; }
    public DateOnly NextDueDate { get; set; }

    // How many days before due date to show the reminder
    public int ReminderDays { get; set; } = 3;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
