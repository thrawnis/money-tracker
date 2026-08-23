using System.ComponentModel.DataAnnotations;

namespace MoneyTracker.Auth.Dtos;

public record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(12)] string Password,
    // IANA id, required at sign-up: every date the app shows (balances "as of
    // today", report periods, when a bill posts) depends on it, and guessing
    // UTC for someone who never sets it silently shifts all of them.
    [Required, MaxLength(100)] string TimeZoneId = ""
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
    bool MfaEnrolled,
    // True for accounts that predate the timezone requirement. The client must
    // collect one before anything else; the API enforces the same rule (see
    // RequireTimeZoneFilter) so it can't be skipped by calling it directly.
    bool RequiresTimeZone = false
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
