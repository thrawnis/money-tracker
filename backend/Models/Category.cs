using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

public class Category
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public int? ParentId { get; set; }
    public Category? Parent { get; set; }

    public ICollection<Category> SubCategories { get; set; } = [];
    public ICollection<Transaction> Transactions { get; set; } = [];
}
