namespace MoneyTracker.Models;

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // Null means top-level category; set to parent Id for subcategories
    public int? ParentId { get; set; }
    public Category? Parent { get; set; }

    public ICollection<Category> SubCategories { get; set; } = [];
    public ICollection<Transaction> Transactions { get; set; } = [];
}
