using FluentAssertions;
using Microsoft.Extensions.Configuration;
using MoneyTracker.Auth.Services;

namespace MoneyTracker.Tests.Unit;

public class EncryptionServiceTests
{
    private static IEncryptionService BuildService(string key = "dGVzdGtleWZvcnVuaXR0ZXN0aW5nMTIz")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["EncryptionKey"] = key })
            .Build();
        return new EncryptionService(config);
    }

    [Fact]
    public void Encrypt_ThenDecrypt_ReturnsOriginalPlaintext()
    {
        var svc = BuildService();
        var dek = svc.GenerateEncryptedDek();

        var plaintext = "Hello, World!";
        var cipher    = svc.Encrypt(plaintext, dek);
        var result    = svc.Decrypt(cipher, dek);

        result.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_NullInput_ReturnsNull()
    {
        var svc = BuildService();
        var dek = svc.GenerateEncryptedDek();

        svc.Encrypt(null, dek).Should().BeNull();
    }

    [Fact]
    public void Decrypt_NullInput_ReturnsNull()
    {
        var svc = BuildService();
        var dek = svc.GenerateEncryptedDek();

        svc.Decrypt(null, dek).Should().BeNull();
    }

    [Fact]
    public void EncryptSamePlaintext_TwiceDifferentCiphertexts_DueToRandomNonce()
    {
        var svc = BuildService();
        var dek = svc.GenerateEncryptedDek();

        var cipher1 = svc.Encrypt("same text", dek);
        var cipher2 = svc.Encrypt("same text", dek);

        cipher1.Should().NotBe(cipher2);
    }

    [Fact]
    public void GenerateEncryptedDek_ProducesUniqueKeysEachCall()
    {
        var svc  = BuildService();
        var dek1 = svc.GenerateEncryptedDek();
        var dek2 = svc.GenerateEncryptedDek();

        dek1.Should().NotBe(dek2);
    }

    [Fact]
    public void Decrypt_WithWrongDek_ThrowsCryptographicException()
    {
        var svc  = BuildService();
        var dek1 = svc.GenerateEncryptedDek();
        var dek2 = svc.GenerateEncryptedDek();

        var cipher = svc.Encrypt("secret", dek1);

        var act = () => svc.Decrypt(cipher, dek2);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Encrypt_Unicode_RoundTripsCorrectly()
    {
        var svc  = BuildService();
        var dek  = svc.GenerateEncryptedDek();
        var text = "Aldi Süd – Einkauf €42,50 🛒";

        svc.Decrypt(svc.Encrypt(text, dek), dek).Should().Be(text);
    }

    [Fact]
    public void Encrypt_EmptyString_RoundTripsCorrectly()
    {
        var svc = BuildService();
        var dek = svc.GenerateEncryptedDek();

        svc.Decrypt(svc.Encrypt("", dek), dek).Should().Be("");
    }

    [Fact]
    public void MasterKeyMismatch_DekDecryptionFails()
    {
        var svc1 = BuildService("a2V5b25lZm9yZW5jcnlwdGlvbnRlc3Qx");
        var svc2 = BuildService("a2V5dHdvZm9yZW5jcnlwdGlvbnRlc3Qy");

        var dek    = svc1.GenerateEncryptedDek();
        var cipher = svc1.Encrypt("data", dek);

        // svc2 uses a different master key — can't decrypt svc1's DEK
        var act = () => svc2.Decrypt(cipher, dek);
        act.Should().Throw<Exception>();
    }
}
