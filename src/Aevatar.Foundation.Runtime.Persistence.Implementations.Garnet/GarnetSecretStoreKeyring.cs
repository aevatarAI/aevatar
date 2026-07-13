namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

public sealed class GarnetSecretStoreKeyring
{
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
        var document = SecretStoreKeyringDocument.LoadFromFile(path);
        document.Validate();
        var keys = document.DecodeKeys();
        var activeKeyId = document.ActiveKeyId ??
            throw new InvalidOperationException("Garnet secret-store keyring requires activeKeyId.");

        if (!keys.ContainsKey(activeKeyId))
            throw new InvalidOperationException("Garnet secret-store keyring activeKeyId must reference a configured key.");

        return new GarnetSecretStoreKeyring(activeKeyId, keys, document.DecodeFingerprintKey());
    }

    public byte[] GetKey(string keyId)
    {
        if (!Keys.TryGetValue(keyId, out var key))
            throw new InvalidOperationException($"Garnet secret-store keyring does not contain key '{keyId}'.");

        return key.ToArray();
    }
}
