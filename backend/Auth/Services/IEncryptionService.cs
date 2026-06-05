namespace MoneyTracker.Auth.Services;

public interface IEncryptionService
{
    /// <summary>
    /// Generates a new random data encryption key (DEK) for a user and returns it
    /// encrypted with the application master key, ready to store in the database.
    /// </summary>
    string GenerateEncryptedDek();

    /// <summary>
    /// Encrypts plaintext using the user's DEK. Returns a base64-encoded ciphertext
    /// that includes the nonce. Returns null if plaintext is null.
    /// </summary>
    string? Encrypt(string? plaintext, string encryptedDek);

    /// <summary>
    /// Decrypts ciphertext using the user's DEK. Returns null if ciphertext is null.
    /// </summary>
    string? Decrypt(string? ciphertext, string encryptedDek);
}
