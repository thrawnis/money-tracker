using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

public class Institution
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public ICollection<Account> Accounts { get; set; } = [];
}
