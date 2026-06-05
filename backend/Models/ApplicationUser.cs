using Microsoft.AspNetCore.Identity;

namespace MoneyTracker.Models;

public class ApplicationUser : IdentityUser
{
    // Email is used as the username — set on registration
    public bool MfaEnrolled { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // AES-256-GCM encrypted DEK, encrypted with the application master key
    public string EncryptedDataKey { get; set; } = string.Empty;

    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<UserPasskeyCredential> PasskeyCredentials { get; set; } = [];
}
