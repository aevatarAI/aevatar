using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

public enum GarnetSecretRecordKind
{
    SecretVault,
    RuntimeSecret,
}

public sealed record GarnetSecretRecordInspection(
    string Reference,
    string KeyId,
    bool UsesActiveKey);

public sealed record GarnetSecretRecordReencryptionResult(
    string Reference,
    string PreviousKeyId,
    string ActiveKeyId,
    bool Changed,
    byte[] RecordBytes);

public static class GarnetSecretRecordReencryption
{
    public static GarnetSecretRecordInspection Inspect(
        ReadOnlyMemory<byte> recordBytes,
        GarnetSecretRecordKind kind,
        GarnetSecretStoreKeyring keyring)
    {
        ArgumentNullException.ThrowIfNull(keyring);

        return kind switch
        {
            GarnetSecretRecordKind.SecretVault => InspectVault(recordBytes, keyring),
            GarnetSecretRecordKind.RuntimeSecret => InspectRuntime(recordBytes, keyring),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Garnet secret record kind."),
        };
    }

    public static GarnetSecretRecordReencryptionResult Reencrypt(
        ReadOnlyMemory<byte> recordBytes,
        GarnetSecretRecordKind kind,
        GarnetSecretStoreKeyring keyring)
    {
        ArgumentNullException.ThrowIfNull(keyring);

        return kind switch
        {
            GarnetSecretRecordKind.SecretVault => ReencryptVault(recordBytes, keyring),
            GarnetSecretRecordKind.RuntimeSecret => ReencryptRuntime(recordBytes, keyring),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Garnet secret record kind."),
        };
    }

    private static GarnetSecretRecordInspection InspectVault(
        ReadOnlyMemory<byte> recordBytes,
        GarnetSecretStoreKeyring keyring)
    {
        var record = GarnetSecretVaultRecord.Parser.ParseFrom(recordBytes.ToArray());
        GarnetSecretRecordCrypto.Decrypt(
            record.EncryptedSecret,
            keyring,
            GarnetSecretRecordIds.VaultAssociatedData(record));

        return new GarnetSecretRecordInspection(
            record.Ref,
            record.EncryptedSecret.KeyId,
            string.Equals(record.EncryptedSecret.KeyId, keyring.ActiveKeyId, StringComparison.Ordinal));
    }

    private static GarnetSecretRecordInspection InspectRuntime(
        ReadOnlyMemory<byte> recordBytes,
        GarnetSecretStoreKeyring keyring)
    {
        var record = GarnetRuntimeSecretRecord.Parser.ParseFrom(recordBytes.ToArray());
        GarnetSecretRecordCrypto.Decrypt(
            record.EncryptedSecret,
            keyring,
            GarnetSecretRecordIds.RuntimeAssociatedData(record));

        return new GarnetSecretRecordInspection(
            record.Ref,
            record.EncryptedSecret.KeyId,
            string.Equals(record.EncryptedSecret.KeyId, keyring.ActiveKeyId, StringComparison.Ordinal));
    }

    private static GarnetSecretRecordReencryptionResult ReencryptVault(
        ReadOnlyMemory<byte> recordBytes,
        GarnetSecretStoreKeyring keyring)
    {
        var record = GarnetSecretVaultRecord.Parser.ParseFrom(recordBytes.ToArray());
        var previousKeyId = record.EncryptedSecret.KeyId;
        var secret = GarnetSecretRecordCrypto.Decrypt(
            record.EncryptedSecret,
            keyring,
            GarnetSecretRecordIds.VaultAssociatedData(record));

        if (string.Equals(previousKeyId, keyring.ActiveKeyId, StringComparison.Ordinal))
        {
            return new GarnetSecretRecordReencryptionResult(
                record.Ref,
                previousKeyId,
                keyring.ActiveKeyId,
                Changed: false,
                recordBytes.ToArray());
        }

        var updated = record.Clone();
        updated.EncryptedSecret = GarnetSecretRecordCrypto.Encrypt(
            secret,
            keyring,
            GarnetSecretRecordIds.VaultAssociatedData(updated));

        return new GarnetSecretRecordReencryptionResult(
            updated.Ref,
            previousKeyId,
            keyring.ActiveKeyId,
            Changed: true,
            updated.ToByteArray());
    }

    private static GarnetSecretRecordReencryptionResult ReencryptRuntime(
        ReadOnlyMemory<byte> recordBytes,
        GarnetSecretStoreKeyring keyring)
    {
        var record = GarnetRuntimeSecretRecord.Parser.ParseFrom(recordBytes.ToArray());
        var previousKeyId = record.EncryptedSecret.KeyId;
        var secret = GarnetSecretRecordCrypto.Decrypt(
            record.EncryptedSecret,
            keyring,
            GarnetSecretRecordIds.RuntimeAssociatedData(record));

        if (string.Equals(previousKeyId, keyring.ActiveKeyId, StringComparison.Ordinal))
        {
            return new GarnetSecretRecordReencryptionResult(
                record.Ref,
                previousKeyId,
                keyring.ActiveKeyId,
                Changed: false,
                recordBytes.ToArray());
        }

        var updated = record.Clone();
        updated.EncryptedSecret = GarnetSecretRecordCrypto.Encrypt(
            secret,
            keyring,
            GarnetSecretRecordIds.RuntimeAssociatedData(updated));

        return new GarnetSecretRecordReencryptionResult(
            updated.Ref,
            previousKeyId,
            keyring.ActiveKeyId,
            Changed: true,
            updated.ToByteArray());
    }
}
