using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

public class Payee
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    // Stored encrypted with the user's DEK
    public string NameEncrypted { get; set; } = string.Empty;

    public int? DefaultCategoryId { get; set; }
    public Category? DefaultCategory { get; set; }

    public ICollection<Transaction> Transactions { get; set; } = [];
}
