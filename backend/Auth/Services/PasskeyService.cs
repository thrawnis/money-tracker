using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Auth.Services;

public class PasskeyService(IFido2 fido2, AppDbContext db) : IPasskeyService
{
    public async Task<CredentialCreateOptions> BeginRegistrationAsync(ApplicationUser user)
    {
        var fidoUser = new Fido2User
        {
            Id          = System.Text.Encoding.UTF8.GetBytes(user.Id),
            Name        = user.Email!,
            DisplayName = user.Email!,
        };

        // Exclude credentials the user already has registered
        var existingCredentials = await db.PasskeyCredentials
            .Where(c => c.UserId == user.Id)
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToListAsync();

        var authenticatorSelection = new AuthenticatorSelection
        {
            ResidentKey        = ResidentKeyRequirement.Preferred,
            UserVerification   = UserVerificationRequirement.Required,
        };

        var options = fido2.RequestNewCredential(
            fidoUser,
            existingCredentials,
            authenticatorSelection,
            AttestationConveyancePreference.None
        );

        return options;
    }

    public async Task<UserPasskeyCredential> CompleteRegistrationAsync(
        AuthenticatorAttestationRawResponse response,
        CredentialCreateOptions options,
        string? deviceName)
    {
        var result = await fido2.MakeNewCredentialAsync(
            response,
            options,
            async (args, _) =>
            {
                bool exists = await db.PasskeyCredentials
                    .AnyAsync(c => c.CredentialId == args.CredentialId);
                return !exists;
            }
        );

        var credential = new UserPasskeyCredential
        {
            UserId       = System.Text.Encoding.UTF8.GetString(result.Result!.UserId),
            CredentialId = result.Result.CredentialId,
            PublicKey    = result.Result.PublicKey,
            SignCount    = result.Result.SignCount,
            DeviceName   = deviceName,
        };

        return credential;
    }

    public async Task<AssertionOptions> BeginAuthenticationAsync(string email)
    {
        // Empty list = allow any registered credential for this email
        // The browser will pick the right one
        var options = fido2.GetAssertionOptions(
            [],
            UserVerificationRequirement.Required
        );

        return await Task.FromResult(options);
    }

    public async Task<UserPasskeyCredential> CompleteAuthenticationAsync(
        AuthenticatorAssertionRawResponse response,
        AssertionOptions options)
    {
        var credential = await db.PasskeyCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == response.Id)
            ?? throw new InvalidOperationException("Credential not found.");

        var storedCounter = credential.SignCount;

        var result = await fido2.MakeAssertionAsync(
            response,
            options,
            credential.PublicKey,
            storedCounter,
            (args, _) => Task.FromResult(true)
        );

        credential.SignCount    = result.Counter;
        credential.LastUsedAt   = DateTime.UtcNow;

        return credential;
    }
}
