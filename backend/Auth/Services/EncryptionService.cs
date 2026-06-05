using System.Security.Cryptography;
using System.Text;

namespace MoneyTracker.Auth.Services;

/// <summary>
/// Provides AES-256-GCM envelope encryption.
///
/// Each user has a Data Encryption Key (DEK) stored encrypted with the
/// application master key (ENCRYPTION_KEY env var). Field values are
/// encrypted with the user's DEK, so the master key only needs to be
/// used when loading/creating a user session — not on every read.
/// </summary>
public class EncryptionService : IEncryptionService
{
    private readonly byte[] _masterKey;

    private const int NonceSize  = 12; // AES-GCM recommended nonce size
    private const int TagSize    = 16; // AES-GCM authentication tag size
    private const int KeySize    = 32; // AES-256

    public EncryptionService(IConfiguration config)
    {
        var raw = config["EncryptionKey"]
            ?? throw new InvalidOperationException("EncryptionKey must be set in configuration.");

        // Accept either a raw string (padded/truncated to 32 bytes) or base64
        try
        {
            _masterKey = Convert.FromBase64String(raw);
        }
        catch
        {
            _masterKey = PadOrTruncate(Encoding.UTF8.GetBytes(raw), KeySize);
        }

        if (_masterKey.Length != KeySize)
            _masterKey = PadOrTruncate(_masterKey, KeySize);
    }

    public string GenerateEncryptedDek()
    {
        var dek = RandomNumberGenerator.GetBytes(KeySize);
        return EncryptBytes(dek, _masterKey);
    }

    public string? Encrypt(string? plaintext, string encryptedDek)
    {
        if (plaintext is null) return null;

        var dek = DecryptBytes(encryptedDek, _masterKey);
        return EncryptBytes(Encoding.UTF8.GetBytes(plaintext), dek);
    }

    public string? Decrypt(string? ciphertext, string encryptedDek)
    {
        if (ciphertext is null) return null;

        var dek       = DecryptBytes(encryptedDek, _masterKey);
        var plaintext = DecryptBytes(ciphertext, dek);
        return Encoding.UTF8.GetString(plaintext);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static string EncryptBytes(byte[] plaintext, byte[] key)
    {
        var nonce      = RandomNumberGenerator.GetBytes(NonceSize);
        var tag        = new byte[TagSize];
        var ciphertext = new byte[plaintext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Format: nonce (12) | tag (16) | ciphertext
        var combined = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce,      0, combined, 0,                            NonceSize);
        Buffer.BlockCopy(tag,        0, combined, NonceSize,                    TagSize);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceSize + TagSize, ciphertext.Length);

        return Convert.ToBase64String(combined);
    }

    private static byte[] DecryptBytes(string base64, byte[] key)
    {
        var combined   = Convert.FromBase64String(base64);
        var nonce      = combined[..NonceSize];
        var tag        = combined[NonceSize..(NonceSize + TagSize)];
        var ciphertext = combined[(NonceSize + TagSize)..];
        var plaintext  = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    private static byte[] PadOrTruncate(byte[] input, int length)
    {
        var result = new byte[length];
        Buffer.BlockCopy(input, 0, result, 0, Math.Min(input.Length, length));
        return result;
    }
}
