using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Models;

public class UserPasskeyCredential
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    // Raw credential ID bytes returned by the authenticator
    public byte[] CredentialId { get; set; } = [];

    // COSE-encoded public key
    public byte[] PublicKey { get; set; } = [];

    // Monotonically increasing counter — used to detect cloned authenticators
    public uint SignCount { get; set; }

    [MaxLength(100)]
    public string? DeviceName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}
