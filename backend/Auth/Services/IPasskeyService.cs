using Fido2NetLib;
using MoneyTracker.Models;

namespace MoneyTracker.Auth.Services;

/// <summary>
/// Wraps Fido2NetLib to handle WebAuthn credential registration and authentication.
/// Requires Fido2NetLib >= 3.0.0.
/// </summary>
public interface IPasskeyService
{
    Task<CredentialCreateOptions> BeginRegistrationAsync(ApplicationUser user);

    Task<UserPasskeyCredential> CompleteRegistrationAsync(
        AuthenticatorAttestationRawResponse response,
        CredentialCreateOptions options,
        string? deviceName);

    Task<AssertionOptions> BeginAuthenticationAsync(string email);

    Task<UserPasskeyCredential> CompleteAuthenticationAsync(
        AuthenticatorAssertionRawResponse response,
        AssertionOptions options);
}
