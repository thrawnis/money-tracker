using MoneyTracker.Models;

namespace MoneyTracker.Auth.Services;

/// <summary>
/// WebAuthn (passkey) registration and sign-in. The interface speaks JSON
/// strings so callers have no dependency on the Fido2 library: options go to
/// the browser's navigator.credentials.create()/get(), and the browser's
/// response comes back as JSON.
/// </summary>
public interface IPasskeyService
{
    /// <summary>Options for navigator.credentials.create(), excluding the user's existing passkeys.</summary>
    Task<string> BeginRegistrationAsync(ApplicationUser user);

    /// <summary>Verifies the browser's attestation; returns a credential for <paramref name="user"/>, ready to save.</summary>
    Task<UserPasskeyCredential> CompleteRegistrationAsync(
        ApplicationUser user, string attestationResponseJson, string optionsJson, string? deviceName);

    /// <summary>Options for navigator.credentials.get(), limited to <paramref name="user"/>'s passkeys.</summary>
    Task<string> BeginAuthenticationAsync(ApplicationUser user);

    /// <summary>
    /// Verifies the browser's assertion against one of <paramref name="user"/>'s
    /// passkeys and returns that credential with its counter updated (not yet
    /// saved). Throws if it isn't the user's passkey or doesn't verify.
    /// </summary>
    Task<UserPasskeyCredential> CompleteAuthenticationAsync(
        ApplicationUser user, string assertionResponseJson, string optionsJson);
}
