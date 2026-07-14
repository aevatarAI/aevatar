using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

public sealed class SecretStoreKeyringDocument
{
    private const int KeyBytes = 32;
    private const UnixFileMode OwnerOnlyFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string? ActiveKeyId { get; set; }

    public Dictionary<string, string>? Keys { get; set; }

    public string? FingerprintKey { get; set; }

    public static SecretStoreKeyringDocument Create(string activeKeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeKeyId);

        return new SecretStoreKeyringDocument
        {
            ActiveKeyId = activeKeyId,
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [activeKeyId] = GenerateKeyBase64(),
            },
            FingerprintKey = GenerateKeyBase64(),
        };
    }

    public static SecretStoreKeyringDocument LoadFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException($"Garnet secret-store keyring file does not exist: {path}");

        try
        {
            EnsureOwnerOnlyPermissions(path);
            var document = JsonSerializer.Deserialize<SecretStoreKeyringDocument>(File.ReadAllText(path), JsonOptions);
            return document ?? throw new InvalidOperationException("Garnet secret-store keyring is malformed.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Garnet secret-store keyring is malformed JSON.", ex);
        }
    }

    public void SaveToFile(string path, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Validate();
        if (File.Exists(path) && !overwrite)
            throw new InvalidOperationException($"Garnet secret-store keyring file already exists: {path}");

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var streamOptions = new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                streamOptions.UnixCreateMode = OwnerOnlyFileMode;

            using (var stream = new FileStream(tempPath, streamOptions))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(JsonSerializer.Serialize(this, JsonOptions));
            }

            EnsureOwnerOnlyPermissions(tempPath);
            File.Move(tempPath, fullPath, overwrite: true);
            EnsureOwnerOnlyPermissions(fullPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public IReadOnlyDictionary<string, byte[]> DecodeKeys()
    {
        if (Keys is not { Count: > 0 })
            throw new InvalidOperationException("Garnet secret-store keyring requires at least one key.");

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var pair in Keys)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw new InvalidOperationException("Garnet secret-store keyring contains an empty key id.");

            keys[pair.Key] = DecodeKey(pair.Value, $"key '{pair.Key}'");
        }

        return keys;
    }

    public byte[] DecodeFingerprintKey()
    {
        if (string.IsNullOrWhiteSpace(FingerprintKey))
            throw new InvalidOperationException("Garnet secret-store keyring requires fingerprintKey.");

        return DecodeKey(FingerprintKey, "fingerprintKey");
    }

    public void AddDataKey(string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        Keys ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (Keys.ContainsKey(keyId))
            throw new InvalidOperationException($"Garnet secret-store keyring already contains key '{keyId}'.");

        Keys[keyId] = GenerateKeyBase64();
        ActiveKeyId = keyId;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ActiveKeyId))
            throw new InvalidOperationException("Garnet secret-store keyring requires activeKeyId.");

        var keys = DecodeKeys();
        if (!keys.ContainsKey(ActiveKeyId))
            throw new InvalidOperationException("Garnet secret-store keyring activeKeyId must reference a configured key.");

        DecodeFingerprintKey();
    }

    public static string GenerateKeyBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));

    private static void EnsureOwnerOnlyPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            if (File.GetUnixFileMode(path) != OwnerOnlyFileMode)
                File.SetUnixFileMode(path, OwnerOnlyFileMode);

            if (File.GetUnixFileMode(path) != OwnerOnlyFileMode)
            {
                throw new InvalidOperationException(
                    $"Garnet secret-store keyring file must have owner-only permissions: {path}");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Unable to enforce owner-only permissions on Garnet secret-store keyring file: {path}",
                ex);
        }
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
}
