// ─────────────────────────────────────────────────────────────
// AevatarSecretsStore - encrypted secrets.json reader/writer.
//
// Compatible with the Aevatar agent-framework encryption format:
// AES-256-GCM with master key stored in macOS Keychain
// (service: "aevatar-agent-framework", account: "aevatar-masterkey")
// or in ~/.aevatar/masterkey.bin as fallback.
//
// Also supports plaintext JSON for simple setups.
// ─────────────────────────────────────────────────────────────

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevatar.Configuration;

/// <summary>
/// Secrets store that reads/writes ~/.aevatar/secrets.json.
/// Supports both encrypted (AES-256-GCM) and plaintext JSON formats.
/// </summary>
public sealed class AevatarSecretsStore : IAevatarSecretsStore
{
    private const int KeyBytes = 32;   // AES-256
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const string Aad = "aevatar-user-secrets-v1";
    private const string KeychainService = "aevatar-agent-framework";
    private const string KeychainAccount = "aevatar-masterkey";

    private readonly string _filePath;
    private Dictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

    public AevatarSecretsStore(string? filePath = null)
    {
        _filePath = filePath ?? AevatarPaths.SecretsJson;
        Load();
    }

    // ─── Read ───

    /// <summary>Gets value by key. Returns null when absent.</summary>
    public string? Get(string key) => _secrets.GetValueOrDefault(key);

    /// <summary>Gets LLM provider API key using multiple key conventions.</summary>
    public string? GetApiKey(string providerName)
    {
        // Convention 1: LLMProviders:Providers:{name}:ApiKey (agent-framework format)
        if (_secrets.TryGetValue($"LLMProviders:Providers:{providerName}:ApiKey", out var val) &&
            !string.IsNullOrWhiteSpace(val))
            return val;

        // Convention 2: LLMProviders:{name}:ApiKey
        if (_secrets.TryGetValue($"LLMProviders:{providerName}:ApiKey", out val) &&
            !string.IsNullOrWhiteSpace(val))
            return val;

        // Convention 3: {PROVIDER}_API_KEY
        if (_secrets.TryGetValue($"{providerName}_API_KEY", out val) &&
            !string.IsNullOrWhiteSpace(val))
            return val;

        return null;
    }

    /// <summary>Gets the configured default LLM provider name.</summary>
    public string? GetDefaultProvider()
    {
        if (_secrets.TryGetValue("LLMProviders:Default", out var val) && !string.IsNullOrWhiteSpace(val))
            return val;
        return null;
    }

    /// <summary>Gets all secrets (read-only view).</summary>
    public IReadOnlyDictionary<string, string> GetAll() => _secrets;

    // ─── Write ───

    /// <summary>Sets value and saves.</summary>
    public void Set(string key, string value)
    {
        _secrets[key] = value;
        Save();
    }

    /// <summary>Removes key and saves.</summary>
    public void Remove(string key)
    {
        _secrets.Remove(key);
        Save();
    }

    // ─── Loading (supports both encrypted and plaintext) ───

    private void Load()
    {
        if (!File.Exists(_filePath)) return;

        string json;
        try { json = File.ReadAllText(_filePath, Encoding.UTF8); }
        catch { return; }

        if (string.IsNullOrWhiteSpace(json)) return;

        // Try encrypted format first
        if (TryLoadEncrypted(json))
            return;

        // Fallback: plaintext JSON
        TryLoadPlaintext(json);
    }

    private bool TryLoadEncrypted(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(json, JsonOpts);
            if (envelope?.SchemaVersion != 1 || string.IsNullOrEmpty(envelope.CiphertextB64))
                return false;

            var masterKey = TryGetMasterKey();
            if (masterKey == null) return false;

            var plaintext = TryDecrypt(envelope, masterKey);
            if (plaintext == null) return false;

            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext, JsonOpts);
            if (dict == null) return false;

            _secrets = new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TryLoadPlaintext(string json)
    {
        try
        {
            _secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _secrets = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        var masterKey = TryGetMasterKey();
        if (masterKey != null && TrySaveEncrypted(masterKey))
            return;

        SavePlaintext();
    }

    private bool TrySaveEncrypted(byte[] key)
    {
        try
        {
            AevatarPaths.EnsureDirectories();
            var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_secrets, JsonOpts));
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagBytes];

            using var gcm = new AesGcm(key, TagBytes);
            gcm.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(Aad));

            var envelope = new EncryptedEnvelope
            {
                SchemaVersion = 1,
                Algorithm = "AES-256-GCM",
                NonceB64 = Convert.ToBase64String(nonce),
                TagB64 = Convert.ToBase64String(tag),
                CiphertextB64 = Convert.ToBase64String(ciphertext),
            };

            var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            });

            WriteAtomically(json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SavePlaintext()
    {
        AevatarPaths.EnsureDirectories();
        var json = JsonSerializer.Serialize(_secrets, new JsonSerializerOptions { WriteIndented = true });
        WriteAtomically(json);
    }

    private void WriteAtomically(string content)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempFile = _filePath + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(tempFile, content, Encoding.UTF8);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try { File.SetUnixFileMode(tempFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        }

        try
        {
            File.Move(tempFile, _filePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch { }
        }
    }

    // ─── AES-256-GCM Decryption ───

    private static byte[]? TryDecrypt(EncryptedEnvelope env, byte[] key)
    {
        try
        {
            var nonce = Convert.FromBase64String(env.NonceB64 ?? "");
            var tag = Convert.FromBase64String(env.TagB64 ?? "");
            var ciphertext = Convert.FromBase64String(env.CiphertextB64 ?? "");

            if (nonce.Length != NonceBytes || tag.Length != TagBytes)
                return null;

            var plaintext = new byte[ciphertext.Length];
            using var gcm = new AesGcm(key, TagBytes);
            gcm.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(Aad));
            return plaintext;
        }
        catch
        {
            return null;
        }
    }

    // ─── Master Key Resolution ───

    private byte[]? TryGetMasterKey()
    {
        // Priority 1: macOS Keychain
        if (OperatingSystem.IsMacOS())
        {
            var key = TryGetKeychainKey();
            if (key != null) return key;
        }

        // Priority 2: file-based masterkey.bin
        var keyPath = Path.Combine(Path.GetDirectoryName(_filePath) ?? AevatarPaths.Root, "masterkey.bin");
        return TryLoadFileKey(keyPath);
    }

    private static byte[]? TryGetKeychainKey()
    {
        try
        {
            if (!File.Exists("/usr/bin/security")) return null;

            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            p.StartInfo.ArgumentList.Add("find-generic-password");
            p.StartInfo.ArgumentList.Add("-a");
            p.StartInfo.ArgumentList.Add(KeychainAccount);
            p.StartInfo.ArgumentList.Add("-s");
            p.StartInfo.ArgumentList.Add(KeychainService);
            p.StartInfo.ArgumentList.Add("-w");

            p.Start();
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            if (p.ExitCode != 0) return null;

            var b64 = p.StandardOutput.ReadToEnd().Trim();
            var bytes = Convert.FromBase64String(b64);
            return bytes.Length == KeyBytes ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryLoadFileKey(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return bytes.Length == KeyBytes ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    // ─── Envelope model ───

    private sealed class EncryptedEnvelope
    {
        public int SchemaVersion { get; set; }
        public string? Algorithm { get; set; }
        public string? NonceB64 { get; set; }
        public string? TagB64 { get; set; }
        public string? CiphertextB64 { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
