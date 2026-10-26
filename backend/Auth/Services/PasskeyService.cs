using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Auth.Services;

/// <summary>
/// Passkeys via the Fido2 library. Passkeys are a second factor here (after
/// the password), so user verification (a PIN, fingerprint or face on the
/// device) is required, and sign-in only ever offers and accepts the
/// signing-in user's own passkeys.
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
}
