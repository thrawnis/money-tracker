namespace MoneyTracker.Models;

public class Payee
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // Optional default category suggested when this payee is selected
    public int? DefaultCategoryId { get; set; }
    public Category? DefaultCategory { get; set; }

    public ICollection<Transaction> Transactions { get; set; } = [];
}
