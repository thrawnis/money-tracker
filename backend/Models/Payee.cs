using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

public class Payee
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public int? DefaultCategoryId { get; set; }
    public Category? DefaultCategory { get; set; }

    public ICollection<Transaction> Transactions { get; set; } = [];
}
