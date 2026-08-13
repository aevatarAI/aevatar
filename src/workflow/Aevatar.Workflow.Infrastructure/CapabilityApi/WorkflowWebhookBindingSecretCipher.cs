using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

/// <summary>
/// Protects dynamic webhook binding HMAC secrets at rest. Binding records are
/// scope-submitted data persisted outside the host process, so the secret
/// column must never be stored (or dumped) in plaintext; only this cipher,
/// keyed by host configuration, can recover it.
/// </summary>
internal interface IWorkflowWebhookBindingSecretCipher
{
    string Protect(string plaintext);

    string Unprotect(string stored);
}

internal sealed class AesGcmWorkflowWebhookBindingSecretCipher : IWorkflowWebhookBindingSecretCipher
{
    private const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmWorkflowWebhookBindingSecretCipher(string encryptionKey)
    {
        if (string.IsNullOrWhiteSpace(encryptionKey))
            throw new ArgumentException("Binding secret encryption key is required.", nameof(encryptionKey));

        // The configured value is a passphrase, not raw key material; hashing
        // yields a uniform 256-bit key regardless of the operator's encoding.
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptionKey.Trim()));
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var packed = new byte[NonceSize + TagSize + cipherBytes.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        cipherBytes.CopyTo(packed, NonceSize + TagSize);
        return Prefix + Convert.ToBase64String(packed);
    }

    public string Unprotect(string stored)
    {
        // Records written before encryption was introduced carry the raw
        // secret; returning them as-is lets a re-PUT migrate them forward.
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;

        var packed = Convert.FromBase64String(stored[Prefix.Length..]);
        if (packed.Length < NonceSize + TagSize)
            throw new CryptographicException("Stored webhook binding secret is malformed.");

        var nonce = packed.AsSpan(0, NonceSize);
        var tag = packed.AsSpan(NonceSize, TagSize);
        var cipherBytes = packed.AsSpan(NonceSize + TagSize);
        var plainBytes = new byte[cipherBytes.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
