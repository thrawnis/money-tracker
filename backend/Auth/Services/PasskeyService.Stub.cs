#if !FIDO2_AVAILABLE
using MoneyTracker.Models;

namespace MoneyTracker.Auth.Services;

/// <summary>
/// Stub used in dev/test environments where Fido2NetLib >= 3.0.0 is unavailable.
/// All methods return a faulted task — passkeys are fully implemented
/// in PasskeyService.Fido2.cs which compiles when FIDO2_AVAILABLE is defined.
/// </summary>
public class PasskeyService : IPasskeyService
{
    private static readonly NotSupportedException _ex =
        new("Passkey support requires Fido2NetLib >= 3.0.0. See backend/Migrations/README.md.");

    public Task<string> BeginRegistrationAsync(ApplicationUser user)
        => Task.FromException<string>(_ex);

    public Task<UserPasskeyCredential> CompleteRegistrationAsync(string attestationResponseJson, string optionsJson, string? deviceName)
        => Task.FromException<UserPasskeyCredential>(_ex);

    public Task<string> BeginAuthenticationAsync(string email)
        => Task.FromException<string>(_ex);

    public Task<UserPasskeyCredential> CompleteAuthenticationAsync(string assertionResponseJson, string optionsJson)
        => Task.FromException<UserPasskeyCredential>(_ex);
}
#endif
