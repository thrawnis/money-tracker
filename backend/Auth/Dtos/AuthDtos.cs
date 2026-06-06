using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Auth.Dtos;

public record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(12)] string Password
);

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password,
    bool RememberMe = false
);

public record TotpVerifyRequest(
    [Required] string Code
);

public record TotpSetupResponse(
    string SharedKey,
    string AuthenticatorUri
);

public record PasskeyCredentialNameRequest(
    [MaxLength(100)] string? DeviceName
);

public record TokenResponse(
    string AccessToken,
    DateTime AccessTokenExpiry,
    string Role,
    bool MfaEnrolled
);

public record RefreshRequest(
    [Required] string RefreshToken
);

public record UserSummary(
    string Id,
    string Email,
    string Role,
    bool MfaEnrolled,
    DateTime CreatedAt
);
