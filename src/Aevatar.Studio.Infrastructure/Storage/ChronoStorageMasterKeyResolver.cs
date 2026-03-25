using System.Diagnostics;
using System.Security.Cryptography;
using Aevatar.Configuration;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageMasterKeyResolver
{
    private const int KeyBytes = 32;
    private const string KeychainService = "aevatar-agent-framework";
    private const string KeychainAccount = "aevatar-masterkey";

    private readonly string _rootDirectory;
    private readonly bool _allowKeychain;

    public ChronoStorageMasterKeyResolver(string? rootDirectory = null, bool allowKeychain = true)
    {
        _rootDirectory = rootDirectory ?? AevatarPaths.Root;
        _allowKeychain = allowKeychain;
    }

    public string ResolveMasterKey(string? configuredMasterKey)
    {
        var normalized = configuredMasterKey?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        var existingKey = TryGetExistingKey();
        if (existingKey is not null)
        {
            return Convert.ToBase64String(existingKey);
        }

        var createdKey = CreateFileKey(GetMasterKeyFilePath());
        return Convert.ToBase64String(createdKey);
    }

    private byte[]? TryGetExistingKey()
    {
        if (_allowKeychain && OperatingSystem.IsMacOS())
        {
            var keychainKey = TryGetKeychainKey();
            if (keychainKey is not null)
            {
                return keychainKey;
            }
        }

        return TryLoadFileKey(GetMasterKeyFilePath());
    }

    private string GetMasterKeyFilePath() => Path.Combine(_rootDirectory, "masterkey.bin");

    private static byte[] CreateFileKey(string keyPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath) ?? throw new InvalidOperationException("Master key directory is required."));

        var existingKey = TryLoadFileKey(keyPath);
        if (existingKey is not null)
        {
            return existingKey;
        }

        var generatedKey = RandomNumberGenerator.GetBytes(KeyBytes);
        var tempFilePath = $"{keyPath}.tmp.{Guid.NewGuid():N}";
        File.WriteAllBytes(tempFilePath, generatedKey);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                File.SetUnixFileMode(tempFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
            }
        }

        try
        {
            try
            {
                File.Move(tempFilePath, keyPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(keyPath))
            {
            }

            return TryLoadFileKey(keyPath) ?? generatedKey;
        }
        finally
        {
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch
            {
            }
        }
    }

    private static byte[]? TryGetKeychainKey()
    {
        try
        {
            if (!File.Exists("/usr/bin/security"))
            {
                return null;
            }

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("find-generic-password");
            process.StartInfo.ArgumentList.Add("-a");
            process.StartInfo.ArgumentList.Add(KeychainAccount);
            process.StartInfo.ArgumentList.Add("-s");
            process.StartInfo.ArgumentList.Add(KeychainService);
            process.StartInfo.ArgumentList.Add("-w");

            process.Start();
            if (!process.WaitForExit(2000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            var base64 = process.StandardOutput.ReadToEnd().Trim();
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length == KeyBytes ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryLoadFileKey(string keyPath)
    {
        try
        {
            if (!File.Exists(keyPath))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(keyPath);
            return bytes.Length == KeyBytes ? bytes : null;
        }
        catch
        {
            return null;
        }
    }
}
