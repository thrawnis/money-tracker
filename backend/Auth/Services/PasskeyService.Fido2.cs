#if FIDO2_AVAILABLE
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;
using System.Text;
using System.Text.Json;

namespace MoneyTracker.Auth.Services;

public class PasskeyService(IFido2 fido2, AppDbContext db) : IPasskeyService
{
    public async Task<string> BeginRegistrationAsync(ApplicationUser user)
    {
        var fidoUser = new Fido2User
        {
            Id          = Encoding.UTF8.GetBytes(user.Id),
            Name        = user.Email!,
            DisplayName = user.Email!,
        };

        var existingCredentials = await db.PasskeyCredentials
            .Where(c => c.UserId == user.Id)
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToListAsync();

        var authenticatorSelection = new AuthenticatorSelection
        {
            ResidentKey      = ResidentKeyRequirement.Preferred,
            UserVerification = UserVerificationRequirement.Required,
        };

        var options = fido2.RequestNewCredential(
            fidoUser,
            existingCredentials,
            authenticatorSelection,
            AttestationConveyancePreference.None);

        return options.ToJson();
    }

    public async Task<UserPasskeyCredential> CompleteRegistrationAsync(
        string attestationResponseJson,
        string optionsJson,
        string? deviceName)
    {
        var response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestationResponseJson)
            ?? throw new InvalidOperationException("Invalid attestation response.");
        var options = CredentialCreateOptions.FromJson(optionsJson);

        var result = await fido2.MakeNewCredentialAsync(
            response,
            options,
            async (args, _) => !await db.PasskeyCredentials.AnyAsync(c => c.CredentialId == args.CredentialId));

        return new UserPasskeyCredential
        {
            UserId       = Encoding.UTF8.GetString(result.Result!.UserId),
            CredentialId = result.Result.CredentialId,
            PublicKey    = result.Result.PublicKey,
            SignCount    = result.Result.SignCount,
            DeviceName   = deviceName,
        };
    }

    public Task<string> BeginAuthenticationAsync(string email)
    {
        var options = fido2.GetAssertionOptions([], UserVerificationRequirement.Required);
        return Task.FromResult(options.ToJson());
    }

    public async Task<UserPasskeyCredential> CompleteAuthenticationAsync(
        string assertionResponseJson,
        string optionsJson)
    {
        var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionResponseJson)
            ?? throw new InvalidOperationException("Invalid assertion response.");
        var options = AssertionOptions.FromJson(optionsJson);

        var credential = await db.PasskeyCredentials
            .FirstOrDefaultAsync(c => c.CredentialId == response.Id)
            ?? throw new InvalidOperationException("Credential not found.");

        var result = await fido2.MakeAssertionAsync(
            response, options, credential.PublicKey, credential.SignCount,
            (_, _) => Task.FromResult(true));

        credential.SignCount  = result.Counter;
        credential.LastUsedAt = DateTime.UtcNow;
        return credential;
    }
}
#endif
