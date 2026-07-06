using System.Text.Json;

namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

public sealed class GarnetSecretStoreKeyring
{
    private const int KeyBytes = 32;

    private GarnetSecretStoreKeyring(
        string activeKeyId,
        IReadOnlyDictionary<string, byte[]> keys,
        byte[] fingerprintKey)
    {
        ActiveKeyId = activeKeyId;
        Keys = keys;
        FingerprintKey = fingerprintKey;
    }

    public string ActiveKeyId { get; }

    public IReadOnlyDictionary<string, byte[]> Keys { get; }

    public byte[] FingerprintKey { get; }

    public byte[] ActiveKey => GetKey(ActiveKeyId);

    public static GarnetSecretStoreKeyring LoadFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException($"Garnet secret-store keyring file does not exist: {path}");

        KeyringDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<KeyringDocument>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Garnet secret-store keyring is malformed JSON.", ex);
        }

        if (document == null)
            throw new InvalidOperationException("Garnet secret-store keyring is malformed.");
        if (string.IsNullOrWhiteSpace(document.ActiveKeyId))
            throw new InvalidOperationException("Garnet secret-store keyring requires activeKeyId.");
        if (document.Keys is not { Count: > 0 })
            throw new InvalidOperationException("Garnet secret-store keyring requires at least one key.");

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var pair in document.Keys)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw new InvalidOperationException("Garnet secret-store keyring contains an empty key id.");

            keys[pair.Key] = DecodeKey(pair.Value, $"key '{pair.Key}'");
        }

        if (!keys.ContainsKey(document.ActiveKeyId))
            throw new InvalidOperationException("Garnet secret-store keyring activeKeyId must reference a configured key.");

        var fingerprintKey = string.IsNullOrWhiteSpace(document.FingerprintKey)
            ? keys[document.ActiveKeyId].ToArray()
            : DecodeKey(document.FingerprintKey, "fingerprintKey");

        return new GarnetSecretStoreKeyring(document.ActiveKeyId, keys, fingerprintKey);
    }

    public byte[] GetKey(string keyId)
    {
        if (!Keys.TryGetValue(keyId, out var key))
            throw new InvalidOperationException($"Garnet secret-store keyring does not contain key '{keyId}'.");

        return key.ToArray();
    }

    private static byte[] DecodeKey(string? base64, string label)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException($"Garnet secret-store keyring {label} is empty.");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Garnet secret-store keyring {label} is not valid base64.", ex);
        }

        if (key.Length != KeyBytes)
            throw new InvalidOperationException($"Garnet secret-store keyring {label} must be a 32-byte key.");

        return key;
    }

    private sealed class KeyringDocument
    {
        public string? ActiveKeyId { get; set; }

        public Dictionary<string, string>? Keys { get; set; }

        public string? FingerprintKey { get; set; }
    }
}
