using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;

namespace Aevatar.Tools.Cli.Studio.Infrastructure.Storage;

public sealed class FileAevatarSettingsStore : IAevatarSettingsStore
{
    private const string DefaultProviderKey = "LLMProviders:Default";
    private const string ProviderPrefix = "LLMProviders:Providers:";
    private const string HomeEnv = "AEVATAR_HOME";
    private const string SecretsPathEnv = "AEVATAR_SECRETS_PATH";
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const string Aad = "aevatar-user-secrets-v1";
    private const string KeychainService = "aevatar-agent-framework";
    private const string KeychainAccount = "aevatar-masterkey";

    private readonly string _filePath;

    public FileAevatarSettingsStore()
    {
        _filePath = ResolveSecretsFilePath();
    }

    public Task<StoredAevatarSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var all = LoadSecrets();
        var providers = ExtractProviderNames(all)
            .Select(name => ResolveProvider(all, name))
            .OrderBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(provider => provider.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var defaultProviderName = ResolveDefaultProviderName(all, providers);

        return Task.FromResult(new StoredAevatarSettings(
            _filePath,
            defaultProviderName,
            ProviderProfiles.All
                .Select(profile => new StoredLlmProviderType(
                    profile.Id,
                    profile.DisplayName,
                    profile.Category,
                    profile.Description,
                    profile.Recommended,
                    profile.DefaultEndpoint,
                    profile.DefaultModel))
                .ToList(),
            providers));
    }

    public async Task<StoredAevatarSettings> SaveAsync(
        StoredAevatarSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var providers = settings.Providers ?? [];
        EnsureUniqueProviderNames(providers);

        var secrets = LoadSecrets();
        var existingNames = ExtractProviderNames(secrets);
        var nextNames = new HashSet<string>(
            providers
                .Select(provider => provider.ProviderName.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var name in existingNames.Where(name => !nextNames.Contains(name)))
        {
            RemoveKnownProviderKeys(secrets, name);
        }

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = RequireValue(provider.ProviderName, "providerName");
            var providerType = RequireValue(provider.ProviderType, "providerType");
            var model = RequireValue(provider.Model, "model");

            secrets[GetProviderKey(name, "ProviderType")] = providerType;
            secrets[GetProviderKey(name, "Model")] = model;
            SetOrRemove(secrets, GetProviderKey(name, "Endpoint"), provider.Endpoint);
            SetOrRemove(secrets, GetProviderKey(name, "ApiKey"), provider.ApiKey);
        }

        var requestedDefault = settings.DefaultProviderName?.Trim() ?? string.Empty;
        var effectiveDefault = !string.IsNullOrWhiteSpace(requestedDefault) && nextNames.Contains(requestedDefault)
            ? requestedDefault
            : providers.FirstOrDefault(provider => provider.ApiKeyConfigured || !string.IsNullOrWhiteSpace(provider.ApiKey))
                ?.ProviderName?.Trim() ?? string.Empty;

        SetOrRemove(secrets, DefaultProviderKey, effectiveDefault);
        SaveSecrets(secrets);

        return await GetAsync(cancellationToken);
    }

    private StoredLlmProvider ResolveProvider(IReadOnlyDictionary<string, string> all, string providerName)
    {
        var providerType = GetValue(all, GetProviderKey(providerName, "ProviderType"));
        if (string.IsNullOrWhiteSpace(providerType))
        {
            providerType = ProviderProfiles.TryInferProviderTypeFromInstanceName(providerName, out var inferredType)
                ? inferredType
                : providerName;
        }

        var profile = ProviderProfiles.Get(providerType);
        var endpoint = GetValue(all, GetProviderKey(providerName, "Endpoint"));
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = profile.DefaultEndpoint;
        }

        var model = GetValue(all, GetProviderKey(providerName, "Model"));
        if (string.IsNullOrWhiteSpace(model))
        {
            model = profile.DefaultModel;
        }

        var apiKey = GetValue(all, GetProviderKey(providerName, "ApiKey"));

        return new StoredLlmProvider(
            providerName,
            providerType,
            profile.DisplayName,
            profile.Category,
            profile.Description,
            model,
            endpoint,
            apiKey,
            !string.IsNullOrWhiteSpace(apiKey));
    }

    private Dictionary<string, string> LoadSecrets()
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        string json;
        try
        {
            json = File.ReadAllText(_filePath, Encoding.UTF8);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (TryLoadEncrypted(json, out var encrypted))
        {
            return encrypted;
        }

        return TryLoadPlaintext(json);
    }

    private bool TryLoadEncrypted(string json, out Dictionary<string, string> secrets)
    {
        secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(json, JsonOpts);
            if (envelope?.SchemaVersion != 1 || string.IsNullOrWhiteSpace(envelope.CiphertextB64))
            {
                return false;
            }

            var masterKey = TryGetMasterKey();
            if (masterKey is null)
            {
                return false;
            }

            var plaintext = TryDecrypt(envelope, masterKey);
            if (plaintext is null)
            {
                return false;
            }

            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext, JsonOpts);
            if (dict is null)
            {
                return false;
            }

            secrets = new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> TryLoadPlaintext(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveSecrets(Dictionary<string, string> secrets)
    {
        var masterKey = TryGetMasterKey();
        if (masterKey is not null && TrySaveEncrypted(secrets, masterKey))
        {
            return;
        }

        SavePlaintext(secrets);
    }

    private bool TrySaveEncrypted(Dictionary<string, string> secrets, byte[] key)
    {
        try
        {
            EnsureAevatarDirectories();

            var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(secrets, JsonOpts));
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

    private void SavePlaintext(Dictionary<string, string> secrets)
    {
        EnsureAevatarDirectories();
        var json = JsonSerializer.Serialize(secrets, new JsonSerializerOptions { WriteIndented = true });
        WriteAtomically(json);
    }

    private void WriteAtomically(string content)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempFile = $"{_filePath}.tmp.{Guid.NewGuid():N}";
        File.WriteAllText(tempFile, content, Encoding.UTF8);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                File.SetUnixFileMode(tempFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
            }
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
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
            }
        }
    }

    private byte[]? TryGetMasterKey()
    {
        if (OperatingSystem.IsMacOS())
        {
            var key = TryGetKeychainKey();
            if (key is not null)
            {
                return key;
            }
        }

        var keyPath = Path.Combine(Path.GetDirectoryName(_filePath) ?? ResolveAevatarHomeDirectory(), "masterkey.bin");
        return TryLoadFileKey(keyPath);
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

            var b64 = process.StandardOutput.ReadToEnd().Trim();
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
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            return bytes.Length == KeyBytes ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryDecrypt(EncryptedEnvelope envelope, byte[] key)
    {
        try
        {
            var nonce = Convert.FromBase64String(envelope.NonceB64 ?? string.Empty);
            var tag = Convert.FromBase64String(envelope.TagB64 ?? string.Empty);
            var ciphertext = Convert.FromBase64String(envelope.CiphertextB64 ?? string.Empty);

            if (nonce.Length != NonceBytes || tag.Length != TagBytes)
            {
                return null;
            }

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

    private static HashSet<string> ExtractProviderNames(IReadOnlyDictionary<string, string> all)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in all.Keys)
        {
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(ProviderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffixIndex = key.LastIndexOf(':');
            if (suffixIndex <= ProviderPrefix.Length)
            {
                continue;
            }

            var name = key[ProviderPrefix.Length..suffixIndex].Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static string ResolveDefaultProviderName(
        IReadOnlyDictionary<string, string> all,
        IReadOnlyList<StoredLlmProvider> providers)
    {
        var configuredDefault = GetValue(all, DefaultProviderKey);
        if (!string.IsNullOrWhiteSpace(configuredDefault) &&
            providers.Any(provider => string.Equals(provider.ProviderName, configuredDefault, StringComparison.OrdinalIgnoreCase)))
        {
            return configuredDefault;
        }

        return providers.FirstOrDefault(provider => provider.ApiKeyConfigured)?.ProviderName ?? string.Empty;
    }

    private static void RemoveKnownProviderKeys(IDictionary<string, string> secrets, string providerName)
    {
        foreach (var key in new[]
                 {
                     GetProviderKey(providerName, "ProviderType"),
                     GetProviderKey(providerName, "Model"),
                     GetProviderKey(providerName, "Endpoint"),
                     GetProviderKey(providerName, "ApiKey"),
                 })
        {
            secrets.Remove(key);
        }
    }

    private static void SetOrRemove(IDictionary<string, string> secrets, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            secrets.Remove(key);
            return;
        }

        secrets[key] = value.Trim();
    }

    private static void EnsureUniqueProviderNames(IEnumerable<StoredLlmProvider> providers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            var name = RequireValue(provider.ProviderName, "providerName");
            if (!seen.Add(name))
            {
                throw new InvalidOperationException($"LLM provider '{name}' is duplicated.");
            }
        }
    }

    private static string RequireValue(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"{fieldName} is required.");
        }

        return normalized;
    }

    private static string GetProviderKey(string providerName, string field) => $"{ProviderPrefix}{providerName}:{field}";

    private static string GetValue(IReadOnlyDictionary<string, string> all, string key) =>
        all.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;

    private static string ResolveSecretsFilePath()
    {
        var envPath = Environment.GetEnvironmentVariable(SecretsPathEnv);
        return !string.IsNullOrWhiteSpace(envPath)
            ? ExpandPath(envPath.Trim())
            : Path.Combine(ResolveAevatarHomeDirectory(), "secrets.json");
    }

    private static string ResolveAevatarHomeDirectory()
    {
        var envPath = Environment.GetEnvironmentVariable(HomeEnv);
        var rawPath = string.IsNullOrWhiteSpace(envPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aevatar")
            : envPath.Trim();

        return ExpandPath(rawPath);
    }

    private static void EnsureAevatarDirectories()
    {
        Directory.CreateDirectory(ResolveAevatarHomeDirectory());
    }

    private static string ExpandPath(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }

        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    private sealed class EncryptedEnvelope
    {
        public int SchemaVersion { get; set; }

        public string? Algorithm { get; set; }

        public string? NonceB64 { get; set; }

        public string? TagB64 { get; set; }

        public string? CiphertextB64 { get; set; }
    }

    private sealed record ProviderProfile(
        string Id,
        string DisplayName,
        string Category,
        string Description,
        string DefaultEndpoint,
        string DefaultModel,
        bool Recommended = false);

    private static class ProviderProfiles
    {
        public static readonly IReadOnlyList<ProviderProfile> All =
        [
            new("openai", "OpenAI", "tier1", "GPT-4o, o1, o3 series", "https://api.openai.com", "gpt-4o-mini", true),
            new("anthropic", "Anthropic", "tier1", "Claude 3.5 Sonnet, Opus, Haiku", "https://api.anthropic.com", "claude-sonnet-4-20250514", true),
            new("google", "Google", "tier1", "Gemini 2.0, 1.5 Pro/Flash", "https://generativelanguage.googleapis.com", "gemini-2.0-flash"),
            new("azure", "Azure OpenAI", "tier1", "Enterprise OpenAI (requires deployment)", string.Empty, string.Empty),
            new("deepseek", "DeepSeek", "tier2", "DeepSeek V3, Coder, Reasoner", "https://api.deepseek.com", "deepseek-chat", true),
            new("mistral", "Mistral", "tier2", "Mistral Large, Small, Codestral", "https://api.mistral.ai", "mistral-small-latest"),
            new("groq", "Groq", "tier2", "Ultra-fast inference (Llama, Mixtral)", "https://api.groq.com/openai", "llama-3.3-70b-versatile"),
            new("xai", "xAI", "tier2", "Grok", "https://api.x.ai", "grok-2-latest"),
            new("cohere", "Cohere", "tier2", "Command R+, Embed, Rerank", "https://api.cohere.com", "command-r-plus"),
            new("perplexity", "Perplexity", "tier2", "Sonar Pro", "https://api.perplexity.ai", "sonar-pro"),
            new("openrouter", "OpenRouter", "aggregator", "200+ models, unified API", "https://openrouter.ai/api/v1", "openai/gpt-4o-mini", true),
            new("deepinfra", "DeepInfra", "aggregator", "Open-source models, GPU inference", "https://api.deepinfra.com/v1/openai", "meta-llama/Llama-3.3-70B-Instruct-Turbo"),
            new("together", "Together AI", "aggregator", "Open-source models at scale", "https://api.together.xyz", "meta-llama/Llama-3.3-70B-Instruct-Turbo"),
            new("alibaba", "Alibaba (Qwen)", "regional", "Qwen via DashScope", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
            new("moonshot", "Moonshot AI", "regional", "Kimi series", "https://api.moonshot.cn/v1", "moonshot-v1-8k"),
            new("zhipu", "Zhipu AI", "regional", "GLM-4 series", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
            new("ollama", "Ollama", "local", "Local models", "http://localhost:11434/v1", "llama3.2"),
            new("lmstudio", "LM Studio", "local", "Local OpenAI-compatible server", "http://localhost:1234/v1", string.Empty),
        ];

        public static ProviderProfile Get(string providerType)
        {
            var normalized = providerType?.Trim() ?? string.Empty;
            return All.FirstOrDefault(profile => string.Equals(profile.Id, normalized, StringComparison.OrdinalIgnoreCase))
                ?? new ProviderProfile(normalized, normalized, "configured", "Configured via user secrets", string.Empty, string.Empty);
        }

        public static bool TryInferProviderTypeFromInstanceName(string instanceName, out string providerType)
        {
            providerType = string.Empty;
            var normalized = instanceName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (All.Any(profile => string.Equals(profile.Id, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                providerType = normalized;
                return true;
            }

            var separatorIndex = normalized.IndexOf('-', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                return false;
            }

            var head = normalized[..separatorIndex].Trim();
            if (All.Any(profile => string.Equals(profile.Id, head, StringComparison.OrdinalIgnoreCase)))
            {
                providerType = head;
                return true;
            }

            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
