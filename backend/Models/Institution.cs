namespace MoneyTracker.Models;

public class Institution
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public ICollection<Account> Accounts { get; set; } = [];
}
