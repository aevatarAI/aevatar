using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

    private readonly byte[] _activeKey;
    private readonly IReadOnlyList<byte[]> _readKeys;

    public AesGcmWorkflowWebhookBindingSecretCipher(string encryptionKey)
        : this(encryptionKey, [])
    {
    }

    private AesGcmWorkflowWebhookBindingSecretCipher(
        string activeEncryptionKey,
        IReadOnlyList<string> previousEncryptionKeys)
    {
        if (string.IsNullOrWhiteSpace(activeEncryptionKey))
            throw new ArgumentException(
                "Binding secret encryption key is required.",
                nameof(activeEncryptionKey));

        // The configured value is a passphrase, not raw key material; hashing
        // yields a uniform 256-bit key regardless of the operator's encoding.
        _activeKey = DeriveKey(activeEncryptionKey);
        _readKeys = [_activeKey, .. previousEncryptionKeys.Select(DeriveKey)];
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_activeKey, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var packed = new byte[NonceSize + TagSize + cipherBytes.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        cipherBytes.CopyTo(packed, NonceSize + TagSize);
        return Prefix + Convert.ToBase64String(packed);
    }

    /// <summary>
    /// Derives a binding-secret passphrase from the host's Garnet secret-store
    /// keyring so production needs no new configuration: the keyring is
    /// already mounted host key material, and the derivation is
    /// domain-separated from the secret store's own use of the active key.
    /// Returns null (fail closed: the binding store stays unregistered) when
    /// the keyring is absent or unreadable.
    /// </summary>
    public static string? TryDerivePassphraseFromKeyring(string? keyringPath)
    {
        if (string.IsNullOrWhiteSpace(keyringPath) || !File.Exists(keyringPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(keyringPath));
            if (!document.RootElement.TryGetProperty("activeKeyId", out var activeKeyIdElement) ||
                activeKeyIdElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var activeKeyId = activeKeyIdElement.GetString();
            if (string.IsNullOrWhiteSpace(activeKeyId) ||
                !document.RootElement.TryGetProperty("keys", out var keysElement) ||
                keysElement.ValueKind != JsonValueKind.Object ||
                !keysElement.TryGetProperty(activeKeyId, out var keyElement) ||
                keyElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var activeKey = keyElement.GetString();
            return string.IsNullOrWhiteSpace(activeKey)
                ? null
                : $"aevatar:workflow-webhook-binding:v1:{activeKeyId}:{activeKey}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a cipher that writes with the active key and can still read
    /// records encrypted with retained historical keys. This makes normal
    /// keyring rotation non-disruptive while keeping retired key removal a
    /// deliberate expiry boundary.
    /// </summary>
    public static IWorkflowWebhookBindingSecretCipher? TryCreateFromKeyring(string? keyringPath)
    {
        if (!TryReadKeyring(keyringPath, out var activeKeyId, out var keys))
            return null;

        var activePassphrase = DeriveKeyringPassphrase(activeKeyId, keys[activeKeyId]);
        var previousPassphrases = keys
            .Where(pair => !string.Equals(pair.Key, activeKeyId, StringComparison.Ordinal))
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => DeriveKeyringPassphrase(pair.Key, pair.Value))
            .ToArray();
        return new AesGcmWorkflowWebhookBindingSecretCipher(
            activePassphrase,
            previousPassphrases);
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
        foreach (var key in _readKeys)
        {
            var plainBytes = new byte[cipherBytes.Length];
            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (AuthenticationTagMismatchException)
            {
                // Try retained historical keys before failing closed.
            }
        }

        throw new CryptographicException(
            "Stored webhook binding secret cannot be decrypted with the configured keyring.");
    }

    private static byte[] DeriveKey(string passphrase) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(passphrase.Trim()));

    private static string DeriveKeyringPassphrase(string keyId, string key) =>
        $"aevatar:workflow-webhook-binding:v1:{keyId}:{key}";

    private static bool TryReadKeyring(
        string? keyringPath,
        out string activeKeyId,
        out IReadOnlyDictionary<string, string> keys)
    {
        activeKeyId = string.Empty;
        keys = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(keyringPath) || !File.Exists(keyringPath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(keyringPath));
            if (!document.RootElement.TryGetProperty("activeKeyId", out var activeKeyIdElement) ||
                activeKeyIdElement.ValueKind != JsonValueKind.String ||
                !document.RootElement.TryGetProperty("keys", out var keysElement) ||
                keysElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            activeKeyId = activeKeyIdElement.GetString()?.Trim() ?? string.Empty;
            var parsedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in keysElement.EnumerateObject())
            {
                var value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(property.Name) && !string.IsNullOrWhiteSpace(value))
                    parsedKeys[property.Name] = value;
            }

            if (activeKeyId.Length == 0 || !parsedKeys.ContainsKey(activeKeyId))
                return false;

            keys = parsedKeys;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
