using Microsoft.AspNetCore.Identity;

namespace MoneyTracker.Models;

public class ApplicationUser : IdentityUser
{
    // Email is used as the username — set on registration
    public bool MfaEnrolled { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<UserPasskeyCredential> PasskeyCredentials { get; set; } = [];
}
