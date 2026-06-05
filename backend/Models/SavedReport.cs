using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MoneyTracker.Models;

public enum ReportType
{
    MonthlyIncomeExpense,
    TransactionsByCategory,
}

public class SavedReport
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public ReportType Type { get; set; }

    // JSON blob storing report-specific filter parameters
    [Required]
    public string ParametersJson { get; set; } = "{}";

    public bool IsDefault { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
