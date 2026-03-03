using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.Configuration;

namespace Aevatar.Tools.Cli.Hosting;

internal sealed record OpenClawConfigDocument(
    string ConfigPath,
    JsonObject Root,
    OpenClawProviderSet State,
    bool Exists,
    IReadOnlyList<string> Warnings);

internal static class OpenClawProviderSyncPersistence
{
    private const string AevatarProviderPrefix = "LLMProviders:Providers:";

    public static string ExpandPath(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (raw.Length == 0)
            return raw;
        raw = Environment.ExpandEnvironmentVariables(raw);
        if (raw == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (raw.StartsWith("~/", StringComparison.Ordinal) || raw.StartsWith("~\\", StringComparison.Ordinal))
        {
            var suffix = raw[2..];
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                suffix);
        }

        return raw;
    }

    public static OpenClawProviderSet ReadAevatarState(string secretsPath)
    {
        var store = new AevatarSecretsStore(secretsPath);
        var all = store.GetAll();
        var builders = new Dictionary<string, MutableProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in all)
        {
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(AevatarProviderPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var suffix = key[AevatarProviderPrefix.Length..];
            var separator = suffix.IndexOf(':');
            if (separator <= 0 || separator == suffix.Length - 1)
                continue;

            var providerName = suffix[..separator].Trim();
            var field = suffix[(separator + 1)..].Trim();
            if (providerName.Length == 0 || field.Length == 0)
                continue;

            var builder = builders.GetValueOrDefault(providerName) ?? new MutableProvider();
            switch (field)
            {
                case "ProviderType":
                    builder.ProviderType = Normalize(value);
                    break;
                case "Model":
                    builder.Model = Normalize(value);
                    break;
                case "Endpoint":
                    builder.Endpoint = Normalize(value);
                    break;
                case "ApiKey":
                    builder.ApiKey = Normalize(value);
                    break;
            }

            builders[providerName] = builder;
        }

        var providers = new SortedDictionary<string, OpenClawProviderSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (providerName, builder) in builders)
        {
            providers[providerName] = new OpenClawProviderSnapshot(
                ProviderType: Normalize(builder.ProviderType),
                Model: Normalize(builder.Model),
                Endpoint: Normalize(builder.Endpoint),
                ApiKey: Normalize(builder.ApiKey));
        }

        var defaultProvider = all.TryGetValue("LLMProviders:Default", out var configuredDefault)
            ? Normalize(configuredDefault)
            : string.Empty;

        return new OpenClawProviderSet(
            DefaultProvider: defaultProvider,
            Providers: providers);
    }

    public static OpenClawConfigDocument LoadOpenClawDocument(string configPath)
    {
        var warnings = new List<string>();
        var exists = File.Exists(configPath);
        var root = new JsonObject();

        if (exists)
        {
            try
            {
                var raw = File.ReadAllText(configPath, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var node = JsonNode.Parse(raw);
                    root = node as JsonObject ?? new JsonObject();
                    if (node is not JsonObject)
                        warnings.Add($"OpenClaw config root is not a JSON object: {configPath}");
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to parse OpenClaw config. Treating as empty JSON. Error: {ex.Message}");
            }
        }

        var providers = new SortedDictionary<string, OpenClawProviderSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in EnumerateProviderSources(root))
        {
            foreach (var (providerName, snapshot) in source)
            {
                if (!providers.ContainsKey(providerName))
                    providers[providerName] = snapshot;
            }
        }

        var llm = root["llm"] as JsonObject;
        var defaultProvider = FirstNonBlank(
            ReadString(llm, "defaultProvider"),
            ReadString(llm, "default_provider"),
            ReadString(root, "defaultProvider"),
            ReadString(root, "default_provider"));

        return new OpenClawConfigDocument(
            ConfigPath: configPath,
            Root: root,
            State: new OpenClawProviderSet(defaultProvider, providers),
            Exists: exists,
            Warnings: warnings);
    }

    public static void ApplyToAevatar(OpenClawProviderSet target, string secretsPath)
    {
        var store = new AevatarSecretsStore(secretsPath);
        foreach (var (providerName, snapshot) in target.Providers.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(snapshot.ProviderType))
                store.Set($"{AevatarProviderPrefix}{providerName}:ProviderType", snapshot.ProviderType);
            if (!string.IsNullOrWhiteSpace(snapshot.Model))
                store.Set($"{AevatarProviderPrefix}{providerName}:Model", snapshot.Model);
            if (!string.IsNullOrWhiteSpace(snapshot.Endpoint))
                store.Set($"{AevatarProviderPrefix}{providerName}:Endpoint", snapshot.Endpoint);
            if (!string.IsNullOrWhiteSpace(snapshot.ApiKey))
                store.Set($"{AevatarProviderPrefix}{providerName}:ApiKey", snapshot.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(target.DefaultProvider))
            store.Set("LLMProviders:Default", target.DefaultProvider);
    }

    public static string? ApplyToOpenClaw(OpenClawConfigDocument document, OpenClawProviderSet target, bool createBackup)
    {
        var root = document.Root;
        var llm = EnsureObject(root, "llm");
        var llmProviders = EnsureObject(llm, "providers");
        var topLevelProviders = root["providers"] as JsonObject;

        foreach (var (providerName, snapshot) in target.Providers)
        {
            UpsertProvider(llmProviders, providerName, snapshot);
            if (topLevelProviders != null)
                UpsertProvider(topLevelProviders, providerName, snapshot);
        }

        if (!string.IsNullOrWhiteSpace(target.DefaultProvider))
        {
            UpsertScalar(llm, target.DefaultProvider, "defaultProvider", "default_provider");
            if (root.ContainsKey("defaultProvider") || root.ContainsKey("default_provider"))
                UpsertScalar(root, target.DefaultProvider, "defaultProvider", "default_provider");
        }

        var backupPath = TryBackup(document.ConfigPath, createBackup);
        Directory.CreateDirectory(Path.GetDirectoryName(document.ConfigPath) ?? Environment.CurrentDirectory);
        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(document.ConfigPath, json + Environment.NewLine, Encoding.UTF8);
        return backupPath;
    }

    private static IEnumerable<IReadOnlyDictionary<string, OpenClawProviderSnapshot>> EnumerateProviderSources(JsonObject root)
    {
        var llm = root["llm"] as JsonObject;
        var llmProviders = llm?["providers"] as JsonObject;
        if (llmProviders != null)
            yield return ParseProviderDictionary(llmProviders);

        var topProviders = root["providers"] as JsonObject;
        if (topProviders != null)
            yield return ParseProviderDictionary(topProviders);
    }

    private static IReadOnlyDictionary<string, OpenClawProviderSnapshot> ParseProviderDictionary(JsonObject providersObject)
    {
        var result = new Dictionary<string, OpenClawProviderSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, node) in providersObject)
        {
            var providerName = Normalize(name);
            if (providerName.Length == 0 || node is not JsonObject obj)
                continue;

            result[providerName] = new OpenClawProviderSnapshot(
                ProviderType: FirstNonBlank(
                    ReadString(obj, "providerType"),
                    ReadString(obj, "provider_type"),
                    ReadString(obj, "type")),
                Model: FirstNonBlank(ReadString(obj, "model")),
                Endpoint: FirstNonBlank(
                    ReadString(obj, "endpoint"),
                    ReadString(obj, "baseUrl"),
                    ReadString(obj, "baseURL"),
                    ReadString(obj, "url")),
                ApiKey: FirstNonBlank(
                    ReadString(obj, "apiKey"),
                    ReadString(obj, "api_key"),
                    ReadString(obj, "token")));
        }

        return result;
    }

    private static void UpsertProvider(JsonObject providersNode, string providerName, OpenClawProviderSnapshot snapshot)
    {
        if (providersNode[providerName] is not JsonObject providerNode)
        {
            providerNode = new JsonObject();
            providersNode[providerName] = providerNode;
        }

        UpsertScalar(providerNode, snapshot.ProviderType, "providerType", "provider_type", "type");
        UpsertScalar(providerNode, snapshot.Model, "model");
        UpsertScalar(providerNode, snapshot.Endpoint, "endpoint", "baseUrl", "baseURL", "url");
        UpsertScalar(providerNode, snapshot.ApiKey, "apiKey", "api_key", "token");
    }

    private static void UpsertScalar(JsonObject node, string value, params string[] preferredKeys)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0 || preferredKeys.Length == 0)
            return;

        foreach (var key in preferredKeys)
        {
            if (node.ContainsKey(key))
            {
                node[key] = normalized;
                return;
            }
        }

        node[preferredKeys[0]] = normalized;
    }

    private static JsonObject EnsureObject(JsonObject root, string key)
    {
        if (root[key] is JsonObject obj)
            return obj;

        obj = new JsonObject();
        root[key] = obj;
        return obj;
    }

    private static string? TryBackup(string targetPath, bool createBackup)
    {
        if (!createBackup || !File.Exists(targetPath))
            return null;

        var backupPath = $"{targetPath}.bak.{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        File.Copy(targetPath, backupPath, overwrite: true);
        return backupPath;
    }

    private static string ReadString(JsonObject? node, string key)
    {
        if (node == null || !node.TryGetPropertyValue(key, out var valueNode) || valueNode == null)
            return string.Empty;
        return Normalize(valueNode.ToString());
    }

    private static string FirstNonBlank(params string[] values)
    {
        foreach (var value in values)
        {
            var normalized = Normalize(value);
            if (normalized.Length > 0)
                return normalized;
        }

        return string.Empty;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private sealed class MutableProvider
    {
        public string ProviderType { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
    }
}
