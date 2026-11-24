using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

public class RefreshToken
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    [Required]
    public string Token { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }
    public bool IsRevoked { get; set; } = false;

    // When IsRevoked was set. Refresh rotates the token on every use, so two
    // browser tabs refreshing at the same moment both present the same cookie
    // and the slower one used to get a 401 and be logged out. A token revoked
    // within the grace window is still accepted (see AuthController.Refresh);
    // one replayed after it is treated as theft and drops that token's family.
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// One sign-in on one device: set when tokens are first issued and carried to
    /// every token it rotates into. A replayed (stolen or lost-response) token
    /// revokes only its own family, so other devices stay signed in.
    /// </summary>
    [MaxLength(36)]
    public string FamilyId { get; set; } = Guid.NewGuid().ToString();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
