using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Auth.Services;

/// <summary>
/// Passkeys via the Fido2 library. A passkey is used either as the second
/// factor after a password (only that user's passkeys are offered/accepted)
/// or on its own, for passwordless sign-in. Either way user verification (a
/// PIN, fingerprint or face on the device) is required, so a passkey alone is
/// still two factors: the device, plus what unlocks it.
/// </summary>
public class PasskeyService(IFido2 fido2, AppDbContext db) : IPasskeyService
{
    private static byte[] UserHandle(ApplicationUser user) => Encoding.UTF8.GetBytes(user.Id);

    public async Task<string> BeginRegistrationAsync(ApplicationUser user)
    {
        var existing = await db.PasskeyCredentials
            .Where(c => c.UserId == user.Id)
            .Select(c => c.CredentialId)
            .ToListAsync();

        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = UserHandle(user), Name = user.Email!, DisplayName = user.Email! },
            // Stops the same authenticator being registered twice.
            ExcludeCredentials = existing.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                // Preferred, not required: phone/computer passkeys are always stored on
                // the device (so they work passwordless), while older security keys
                // that can't store one still work as a second factor.
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });
        return options.ToJson();
    }

    public async Task<UserPasskeyCredential> CompleteRegistrationAsync(
        ApplicationUser user, string attestationResponseJson, string optionsJson, string? deviceName)
    {
        var response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestationResponseJson)
            ?? throw new InvalidOperationException("Invalid passkey response.");
        var options = CredentialCreateOptions.FromJson(optionsJson);
        if (!options.User.Id.AsSpan().SequenceEqual(UserHandle(user)))
            throw new InvalidOperationException("This passkey registration belongs to a different account.");

        var result = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = response,
            OriginalOptions = options,
            IsCredentialIdUniqueToUserCallback = async (args, ct) =>
                !await db.PasskeyCredentials.AnyAsync(c => c.CredentialId == args.CredentialId, ct),
        });

        return new UserPasskeyCredential
        {
            UserId = user.Id,
            CredentialId = result.Id,
            PublicKey = result.PublicKey,
            SignCount = result.SignCount,
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim()[..Math.Min(deviceName.Trim().Length, 100)],
        };
    }

    public async Task<string> BeginAuthenticationAsync(ApplicationUser user)
    {
        var mine = await db.PasskeyCredentials
            .Where(c => c.UserId == user.Id)
            .Select(c => c.CredentialId)
            .ToListAsync();

        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = mine.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            UserVerification = UserVerificationRequirement.Required,
        });
        return options.ToJson();
    }

    public async Task<UserPasskeyCredential> CompleteAuthenticationAsync(
        ApplicationUser user, string assertionResponseJson, string optionsJson)
    {
        var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionResponseJson)
            ?? throw new InvalidOperationException("Invalid passkey response.");
        var options = AssertionOptions.FromJson(optionsJson);

        // Only this user's passkeys count — a valid passkey for someone else
        // must not complete this user's sign-in.
        var credential = await db.PasskeyCredentials
            .FirstOrDefaultAsync(c => c.UserId == user.Id && c.CredentialId == response.RawId)
            ?? throw new InvalidOperationException("That passkey isn't registered to this account.");

        var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = response,
            OriginalOptions = options,
            StoredPublicKey = credential.PublicKey,
            StoredSignatureCounter = credential.SignCount,
            // The authenticator's user handle (when it sends one) must be this user.
            IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(UserHandle(user))),
        });

        credential.SignCount = result.SignCount;
        credential.LastUsedAt = DateTime.UtcNow;
        return credential;
    }

    public string BeginPasswordlessAuthentication() =>
        // No allow-list: the browser offers whichever of this site's passkeys
        // the device holds, and the chosen one identifies the account.
        fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        }).ToJson();

    public async Task<UserPasskeyCredential> CompletePasswordlessAuthenticationAsync(
        string assertionResponseJson, string optionsJson)
    {
        var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionResponseJson)
            ?? throw new InvalidOperationException("Invalid passkey response.");
        var options = AssertionOptions.FromJson(optionsJson);

        var credential = await db.PasskeyCredentials
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.CredentialId == response.RawId)
            ?? throw new InvalidOperationException("That passkey isn't registered here.");

        // Verify the signature first; only a verified assertion says anything
        // about which account is signing in.
        var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = response,
            OriginalOptions = options,
            StoredPublicKey = credential.PublicKey,
            StoredSignatureCounter = credential.SignCount,
            // Passwordless sign-in must name its owner: the authenticator's user
            // handle is required and must be the credential's user.
            IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                Task.FromResult(args.UserHandle is { Length: > 0 }
                    && args.UserHandle.AsSpan().SequenceEqual(UserHandle(credential.User))),
        });
        if (response.Response.UserHandle is not { Length: > 0 })
            throw new InvalidOperationException("This passkey can't be used without a password.");

        credential.SignCount = result.SignCount;
        credential.LastUsedAt = DateTime.UtcNow;
        return credential;
    }
}
