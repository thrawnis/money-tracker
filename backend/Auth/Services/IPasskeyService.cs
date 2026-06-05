using MoneyTracker.Models;

namespace MoneyTracker.Auth.Services;

/// <summary>
/// Wraps WebAuthn (passkey) credential registration and authentication.
/// All Fido2NetLib types are kept internal to the implementation;
/// the interface communicates via JSON strings so it has no library dependency.
/// </summary>
public interface IPasskeyService
{
    /// <summary>Returns JSON options to send to the browser's navigator.credentials.create().</summary>
    Task<string> BeginRegistrationAsync(ApplicationUser user);

    /// <summary>Verifies the browser attestation and returns a credential ready to persist.</summary>
    Task<UserPasskeyCredential> CompleteRegistrationAsync(
        string attestationResponseJson,
        string optionsJson,
        string? deviceName);

    /// <summary>Returns JSON options to send to the browser's navigator.credentials.get().</summary>
    Task<string> BeginAuthenticationAsync(string email);

    /// <summary>Verifies the browser assertion and returns the updated credential.</summary>
    Task<UserPasskeyCredential> CompleteAuthenticationAsync(
        string assertionResponseJson,
        string optionsJson);
}
