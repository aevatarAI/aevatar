using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

internal static class GarnetSecretRecordCrypto
{
    private const string Algorithm = "AES-256-GCM";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static GarnetSecretCiphertext Encrypt(
        string secret,
        GarnetSecretStoreKeyring keyring,
        ReadOnlySpan<byte> associatedData)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(keyring);

        var key = keyring.ActiveKey;
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using var gcm = new AesGcm(key, TagBytes);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        CryptographicOperations.ZeroMemory(plaintext);

        return new GarnetSecretCiphertext
        {
            KeyId = keyring.ActiveKeyId,
            Algorithm = Algorithm,
            Nonce = ByteString.CopyFrom(nonce),
            Ciphertext = ByteString.CopyFrom(ciphertext),
            Tag = ByteString.CopyFrom(tag),
        };
    }

    public static string Decrypt(
        GarnetSecretCiphertext encrypted,
        GarnetSecretStoreKeyring keyring,
        ReadOnlySpan<byte> associatedData)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        ArgumentNullException.ThrowIfNull(keyring);
        if (!string.Equals(encrypted.Algorithm, Algorithm, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported Garnet secret encryption algorithm '{encrypted.Algorithm}'.");

        var key = keyring.GetKey(encrypted.KeyId);
        var nonce = encrypted.Nonce.ToByteArray();
        var ciphertext = encrypted.Ciphertext.ToByteArray();
        var tag = encrypted.Tag.ToByteArray();
        var plaintext = new byte[ciphertext.Length];

        using var gcm = new AesGcm(key, TagBytes);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

        var secret = Encoding.UTF8.GetString(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        return secret;
    }

    public static string Fingerprint(string secret, GarnetSecretStoreKeyring keyring)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(keyring);

        var hash = HMACSHA256.HashData(keyring.FingerprintKey, Encoding.UTF8.GetBytes(secret));
        return "hmac-sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
