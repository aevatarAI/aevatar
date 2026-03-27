using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.Configuration;

namespace Aevatar.Tools.Config;

internal enum WorkflowSource
{
    Home = 0,
    Repo = 1,
    All = 2,
}

internal sealed record ConfigPathInfo(
    string Root,
    string SecretsJson,
    string ConfigJson,
    string ConnectorsJson,
    string MCPJson,
    string WorkflowsHome,
    string WorkflowsRepo,
    string? HomeEnvValue,
    string? SecretsPathEnvValue);

internal sealed record ConfigPathStatus(
    string Path,
    bool Exists,
    bool Readable,
    bool Writable,
    long? SizeBytes,
    string? Error);

internal sealed record ConfigDoctorReport(
    ConfigPathInfo Paths,
    ConfigPathStatus Secrets,
    ConfigPathStatus Config,
    ConfigPathStatus Connectors,
    ConfigPathStatus MCP,
    ConfigPathStatus WorkflowsHome,
    ConfigPathStatus WorkflowsRepo);

internal sealed record WorkflowFileItem(
    string Filename,
    string Source,
    string Path,
    long SizeBytes,
    string LastModified);

internal sealed record WorkflowFileContent(
    string Filename,
    string Source,
    string Path,
    string Content,
    long SizeBytes,
    string LastModified);

internal sealed record ValidationResult(
    bool Valid,
    string Message,
    int Count);

internal sealed class ConfigOperations
{
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ISecretsStore _secrets;

    public ConfigOperations(ISecretsStore secrets)
    {
        _secrets = secrets;
    }

    public ConfigPathInfo GetPaths() => new(
        Root: AevatarPaths.Root,
        SecretsJson: AevatarPaths.SecretsJson,
        ConfigJson: AevatarPaths.ConfigJson,
        ConnectorsJson: AevatarPaths.ConnectorsJson,
        MCPJson: AevatarPaths.MCPJson,
        WorkflowsHome: AevatarPaths.Workflows,
        WorkflowsRepo: AevatarPaths.RepoRootWorkflows,
        HomeEnvValue: Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv),
        SecretsPathEnvValue: Environment.GetEnvironmentVariable(AevatarPaths.SecretsPathEnv));

    public ConfigDoctorReport GetDoctorReport()
    {
        AevatarPaths.EnsureDirectories();
        var paths = GetPaths();
        return new ConfigDoctorReport(
            Paths: paths,
            Secrets: EvaluateFilePath(paths.SecretsJson),
            Config: EvaluateFilePath(paths.ConfigJson),
            Connectors: EvaluateFilePath(paths.ConnectorsJson),
            MCP: EvaluateFilePath(paths.MCPJson),
            WorkflowsHome: EvaluateDirectoryPath(paths.WorkflowsHome),
            WorkflowsRepo: EvaluateDirectoryPath(paths.WorkflowsRepo));
    }

    public IReadOnlyDictionary<string, string> ListSecrets() => _secrets.GetAll();

    public bool TryGetSecret(string key, out string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));
        return _secrets.TryGet(key.Trim(), out value);
    }

    public void SetSecret(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("value is required", nameof(value));
        _secrets.Set(key.Trim(), value.Trim());
    }

    public bool RemoveSecret(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));
        return _secrets.Remove(key.Trim());
    }

    public string ExportSecretsJson()
    {
        var nested = FlatToNested(_secrets.GetAll());
        return JsonSerializer.Serialize(nested, JsonWriteOptions);
    }

    public int ImportSecretsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("json is required", nameof(json));

        using var doc = JsonDocument.Parse(json);
        var newFlat = NestedToFlat(doc.RootElement);
        var oldAll = _secrets.GetAll();
        foreach (var key in oldAll.Keys)
        {
            if (!newFlat.ContainsKey(key))
                _secrets.Remove(key);
        }

        foreach (var kv in newFlat)
            _secrets.Set(kv.Key, kv.Value);

        EnsureDefaultProviderKeyBestEffort(null);
        return newFlat.Count;
    }

    public IReadOnlyDictionary<string, string> ListConfigJson()
    {
        return LoadJsonFileAsFlat(AevatarPaths.ConfigJson);
    }

    public bool TryGetConfigJson(string key, out string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));

        var all = LoadJsonFileAsFlat(AevatarPaths.ConfigJson);
        return all.TryGetValue(key.Trim(), out value);
    }

    public void SetConfigJson(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("value is required", nameof(value));

        var all = LoadJsonFileAsFlat(AevatarPaths.ConfigJson);
        all[key.Trim()] = value.Trim();
        SaveFlatAsJsonFile(AevatarPaths.ConfigJson, all);
    }

    public bool RemoveConfigJson(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));

        var all = LoadJsonFileAsFlat(AevatarPaths.ConfigJson);
        var removed = all.Remove(key.Trim());
        SaveFlatAsJsonFile(AevatarPaths.ConfigJson, all);
        return removed;
    }

    public string ExportConfigJson()
    {
        var all = LoadJsonFileAsFlat(AevatarPaths.ConfigJson);
        var nested = FlatToNested(all);
        return JsonSerializer.Serialize(nested, JsonWriteOptions);
    }

    public int ImportConfigJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("json is required", nameof(json));

        using var doc = JsonDocument.Parse(json);
        var newFlat = NestedToFlat(doc.RootElement);
        SaveFlatAsJsonFile(AevatarPaths.ConfigJson, newFlat);
        return newFlat.Count;
    }

    public IReadOnlyList<ProviderTypeItem> ListLlmProviderTypes() => ProviderCatalog.BuildProviderTypes(_secrets);

    public IReadOnlyList<ProviderInstanceItem> ListLlmInstances() => ProviderCatalog.BuildInstances(_secrets);

    public ResolvedProviderPublic GetLlmInstance(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            throw new ArgumentException("providerName is required", nameof(providerName));
        return LLMProviderResolver.Resolve(_secrets, providerName).Public;
    }

    public string GetLlmDefaultProvider()
    {
        EnsureDefaultProviderKeyBestEffort(null);
        return ResolveEffectiveDefaultProviderName();
    }

    public string SetLlmDefaultProvider(string providerName)
    {
        var name = (providerName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("providerName is required", nameof(providerName));
        if (!IsProviderRunnable(name))
            throw new InvalidOperationException("providerName has no configured apiKey");

        _secrets.Set("LLMProviders:Default", name);
        return name;
    }

    public object GetLlmApiKey(string providerName, bool reveal)
    {
        var name = (providerName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("providerName is required", nameof(providerName));

        var keyPath = $"LLMProviders:Providers:{name}:ApiKey";
        if (!_secrets.TryGet(keyPath, out var value) || string.IsNullOrWhiteSpace(value))
            return new { providerName = name, configured = false, masked = string.Empty };

        var trimmed = value.Trim();
        var masked = SecretMask.MaskMiddle(trimmed);
        if (reveal)
            return new { providerName = name, configured = true, masked, value = trimmed };

        return new { providerName = name, configured = true, masked };
    }

    public string SetLlmApiKey(string providerName, string apiKey)
    {
        var name = (providerName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("providerName is required", nameof(providerName));
        var key = (apiKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("apiKey is required", nameof(apiKey));

        _secrets.Set($"LLMProviders:Providers:{name}:ApiKey", key);
        EnsureDefaultProviderKeyBestEffort(name);
        return name;
    }

    public bool RemoveLlmApiKey(string providerName)
    {
        var name = (providerName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("providerName is required", nameof(providerName));
        var removed = _secrets.Remove($"LLMProviders:Providers:{name}:ApiKey");
        EnsureDefaultProviderKeyBestEffort(null);
        return removed;
    }

    public object UpsertLlmInstance(UpsertLLMInstanceRequest request)
    {
        var name = (request.ProviderName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("providerName is required", nameof(request.ProviderName));
        var providerType = (request.ProviderType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(providerType))
            throw new ArgumentException("providerType is required", nameof(request.ProviderType));
        var model = (request.Model ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("model is required", nameof(request.Model));

        _secrets.Set($"LLMProviders:Providers:{name}:ProviderType", providerType);
        _secrets.Set($"LLMProviders:Providers:{name}:Model", model);

        var endpointPath = $"LLMProviders:Providers:{name}:Endpoint";
        var endpoint = (request.Endpoint ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(endpoint) &&
            string.Equals(providerType, "nyxid", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = TryResolveNyxIdGatewayEndpointFromConfigJson() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(endpoint))
            _secrets.Remove(endpointPath);
        else
            _secrets.Set(endpointPath, endpoint);

        var apiKeyPath = $"LLMProviders:Providers:{name}:ApiKey";
        var apiKey = (request.ApiKey ?? string.Empty).Trim();
        var copyFrom = (request.CopyApiKeyFrom ?? string.Empty).Trim();
        var forceCopyFrom = request.ForceCopyApiKeyFrom == true;
        var hasExistingApiKey = _secrets.TryGet(apiKeyPath, out var existingApiKey) && !string.IsNullOrWhiteSpace(existingApiKey);
        var apiKeyCopiedFrom = string.Empty;
        var apiKeyCopySkipped = false;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _secrets.Set(apiKeyPath, apiKey);
        }
        else if (!string.IsNullOrWhiteSpace(copyFrom))
        {
            if (hasExistingApiKey && !forceCopyFrom)
            {
                apiKeyCopySkipped = true;
            }
            else
            {
                var fromPath = $"LLMProviders:Providers:{copyFrom}:ApiKey";
                if (!_secrets.TryGet(fromPath, out var fromKey) || string.IsNullOrWhiteSpace(fromKey))
                    throw new InvalidOperationException("copyApiKeyFrom has no configured apiKey");

                _secrets.Set(apiKeyPath, fromKey.Trim());
                apiKeyCopiedFrom = copyFrom;
            }
        }

        EnsureDefaultProviderKeyBestEffort(name);
        var resolved = LLMProviderResolver.Resolve(_secrets, name);
        return new
        {
            providerName = name,
            providerType,
            provider = resolved.Public,
            apiKeyCopiedFrom,
            apiKeyCopySkipped,
        };
    }

    private static string? TryResolveNyxIdGatewayEndpointFromConfigJson()
    {
        var all = LoadJsonFileAsFlat(AevatarPaths.ConfigJson);
        if (!all.TryGetValue("Cli:App:NyxId:Authority", out var authority) ||
            string.IsNullOrWhiteSpace(authority))
        {
            return null;
        }

        var trimmed = authority.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return null;

        var absolute = uri.ToString().TrimEnd('/');
        if (absolute.EndsWith("/api/v1/llm/gateway/v1", StringComparison.OrdinalIgnoreCase))
            return absolute;

        return $"{absolute}/api/v1/llm/gateway/v1";
    }

    public bool DeleteLlmInstance(string providerName)
    {
        var name = (providerName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("providerName is required", nameof(providerName));

        var keys = new[]
        {
            $"LLMProviders:Providers:{name}:ProviderType",
            $"LLMProviders:Providers:{name}:Model",
            $"LLMProviders:Providers:{name}:Endpoint",
            $"LLMProviders:Providers:{name}:ApiKey",
        };

        var removed = false;
        foreach (var key in keys)
            removed |= _secrets.Remove(key);

        if (_secrets.TryGet("LLMProviders:Default", out var currentDefault) &&
            string.Equals((currentDefault ?? string.Empty).Trim(), name, StringComparison.OrdinalIgnoreCase))
        {
            _secrets.Remove("LLMProviders:Default");
        }

        EnsureDefaultProviderKeyBestEffort(null);
        return removed;
    }

    public Task<object> ProbeLlmTestAsync(string providerName, CancellationToken cancellationToken)
    {
        var provider = LLMProviderResolver.Resolve(_secrets, providerName);
        return LLMProbe.TestAsync(provider, cancellationToken);
    }

    public Task<object> ProbeLlmModelsAsync(string providerName, int limit, CancellationToken cancellationToken)
    {
        var provider = LLMProviderResolver.Resolve(_secrets, providerName);
        return LLMProbe.FetchModelsAsync(provider, limit, cancellationToken);
    }

    public IReadOnlyList<WorkflowFileItem> ListWorkflows(WorkflowSource source)
    {
        var files = new List<WorkflowFileItem>();
        if (source is WorkflowSource.Home or WorkflowSource.All)
            files.AddRange(ListWorkflowsFromDirectory(AevatarPaths.Workflows, "home"));
        if (source is WorkflowSource.Repo or WorkflowSource.All)
            files.AddRange(ListWorkflowsFromDirectory(AevatarPaths.RepoRootWorkflows, "repo"));

        return files
            .OrderBy(f => f.Filename, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public WorkflowFileContent? GetWorkflow(string filename, WorkflowSource source)
    {
        var normalized = NormalizeWorkflowFilename(filename);
        var candidates = ResolveWorkflowReadPaths(normalized, source);
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate.Path))
                continue;

            var info = new FileInfo(candidate.Path);
            return new WorkflowFileContent(
                Filename: normalized,
                Source: candidate.Source,
                Path: candidate.Path,
                Content: File.ReadAllText(candidate.Path),
                SizeBytes: info.Length,
                LastModified: info.LastWriteTimeUtc.ToString("o"));
        }

        return null;
    }

    public WorkflowFileItem PutWorkflow(string filename, string content, WorkflowSource source)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("content is required", nameof(content));
        if (source == WorkflowSource.All)
            throw new ArgumentException("write source cannot be all", nameof(source));

        var normalized = NormalizeWorkflowFilename(filename);
        var directory = source == WorkflowSource.Repo ? AevatarPaths.RepoRootWorkflows : AevatarPaths.Workflows;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, normalized);
        File.WriteAllText(path, content);

        var info = new FileInfo(path);
        return new WorkflowFileItem(
            Filename: normalized,
            Source: source == WorkflowSource.Repo ? "repo" : "home",
            Path: path,
            SizeBytes: info.Length,
            LastModified: info.LastWriteTimeUtc.ToString("o"));
    }

    public bool DeleteWorkflow(string filename, WorkflowSource source)
    {
        if (source == WorkflowSource.All)
            throw new ArgumentException("delete source cannot be all", nameof(source));

        var normalized = NormalizeWorkflowFilename(filename);
        var directory = source == WorkflowSource.Repo ? AevatarPaths.RepoRootWorkflows : AevatarPaths.Workflows;
        var path = Path.Combine(directory, normalized);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    public IReadOnlyList<ConnectorConfigEntry> ListConnectors() => AevatarConnectorConfig.LoadConnectors();

    public ConnectorConfigEntry? GetConnector(string name)
    {
        var normalized = NormalizeName(name, "name");
        return ListConnectors().FirstOrDefault(x => string.Equals(x.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public ConnectorConfigEntry UpsertConnector(string name, string entryJson)
    {
        var normalized = NormalizeName(name, "name");
        var node = ParseJsonObject(entryJson, "entryJson");
        node["name"] = normalized;

        var entry = node.Deserialize<ConnectorConfigEntry>(JsonWriteOptions)
            ?? throw new InvalidOperationException("invalid connector entry json");
        if (string.IsNullOrWhiteSpace(entry.Type))
            throw new InvalidOperationException("connector type is required");

        var list = ListConnectors().ToList();
        var index = list.FindIndex(x => string.Equals(x.Name, normalized, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            list[index] = entry;
        else
            list.Add(entry);

        SaveConnectors(list);
        return entry;
    }

    public bool DeleteConnector(string name)
    {
        var normalized = NormalizeName(name, "name");
        var list = ListConnectors().ToList();
        var removed = list.RemoveAll(x => string.Equals(x.Name, normalized, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
            SaveConnectors(list);
        return removed;
    }

    public ValidationResult ValidateConnectorsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("json is required", nameof(json));
        using var doc = JsonDocument.Parse(json);
        if (!TryGetPropertyIgnoreCase(doc.RootElement, "connectors", out _))
            throw new InvalidOperationException("connectors root node is required");

        var entries = LoadConnectorsFromJson(json);
        return new ValidationResult(true, "valid connectors json", entries.Count);
    }

    public int ImportConnectorsJson(string json)
    {
        var validation = ValidateConnectorsJson(json);
        var entries = LoadConnectorsFromJson(json);
        SaveConnectors(entries);
        return validation.Count;
    }

    public string ExportConnectorsJson()
    {
        if (!File.Exists(AevatarPaths.ConnectorsJson))
            return JsonSerializer.Serialize(new { connectors = Array.Empty<object>() }, JsonWriteOptions);
        return File.ReadAllText(AevatarPaths.ConnectorsJson);
    }

    public IReadOnlyList<MCPServerEntry> ListMcpServers() => AevatarMCPConfig.LoadServers();

    public MCPServerEntry? GetMcpServer(string name)
    {
        var normalized = NormalizeName(name, "name");
        return ListMcpServers().FirstOrDefault(x => string.Equals(x.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public MCPServerEntry UpsertMcpServer(string name, string entryJson)
    {
        var normalized = NormalizeName(name, "name");
        var node = ParseJsonObject(entryJson, "entryJson");
        node["name"] = normalized;

        var entry = node.Deserialize<MCPServerEntry>(JsonWriteOptions)
            ?? throw new InvalidOperationException("invalid mcp server entry json");
        if (string.IsNullOrWhiteSpace(entry.Command))
            throw new InvalidOperationException("command is required");

        var list = ListMcpServers().ToList();
        var index = list.FindIndex(x => string.Equals(x.Name, normalized, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            list[index] = entry;
        else
            list.Add(entry);

        SaveMcpServers(list);
        return entry;
    }

    public bool DeleteMcpServer(string name)
    {
        var normalized = NormalizeName(name, "name");
        var list = ListMcpServers().ToList();
        var removed = list.RemoveAll(x => string.Equals(x.Name, normalized, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
            SaveMcpServers(list);
        return removed;
    }

    public ValidationResult ValidateMcpJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("json is required", nameof(json));
        using var doc = JsonDocument.Parse(json);
        if (!TryGetPropertyIgnoreCase(doc.RootElement, "mcpServers", out var serversNode) || serversNode.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("mcpServers object is required");

        var entries = LoadMcpServersFromJson(json);
        return new ValidationResult(true, "valid mcp json", entries.Count);
    }

    public int ImportMcpJson(string json)
    {
        var validation = ValidateMcpJson(json);
        var entries = LoadMcpServersFromJson(json);
        SaveMcpServers(entries);
        return validation.Count;
    }

    public string ExportMcpJson()
    {
        if (!File.Exists(AevatarPaths.MCPJson))
            return JsonSerializer.Serialize(new { mcpServers = new { } }, JsonWriteOptions);
        return File.ReadAllText(AevatarPaths.MCPJson);
    }

    private bool IsProviderRunnable(string providerName)
    {
        var name = (providerName ?? string.Empty).Trim();
        if (name.Length == 0)
            return false;
        return _secrets.TryGet($"LLMProviders:Providers:{name}:ApiKey", out var v) && !string.IsNullOrWhiteSpace(v);
    }

    private string ResolveEffectiveDefaultProviderName()
    {
        if (_secrets.TryGet("LLMProviders:Default", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            var value = raw.Trim();
            if (!string.Equals(value, "default", StringComparison.OrdinalIgnoreCase) || IsProviderRunnable("default"))
                return value;
        }

        const string prefix = "LLMProviders:Providers:";
        const string suffix = ":ApiKey";
        var all = _secrets.GetAll();
        var first = all.Keys
            .Where(k => k != null &&
                        k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Substring(prefix.Length, k.Length - prefix.Length - suffix.Length).Trim())
            .FirstOrDefault(n => !string.IsNullOrEmpty(n) && IsProviderRunnable(n));
        return first ?? "default";
    }

    private void EnsureDefaultProviderKeyBestEffort(string? preferredProvider)
    {
        var current = _secrets.TryGet("LLMProviders:Default", out var raw) ? (raw ?? string.Empty).Trim() : string.Empty;
        var currentBad = string.IsNullOrWhiteSpace(current) ||
                         (string.Equals(current, "default", StringComparison.OrdinalIgnoreCase) && !IsProviderRunnable("default"));
        if (!currentBad && IsProviderRunnable(current))
            return;

        var preferred = (preferredProvider ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(preferred) && IsProviderRunnable(preferred))
        {
            _secrets.Set("LLMProviders:Default", preferred);
            return;
        }

        var next = ResolveEffectiveDefaultProviderName();
        if (!string.IsNullOrEmpty(next) && IsProviderRunnable(next))
        {
            _secrets.Set("LLMProviders:Default", next);
            return;
        }

        if (!string.IsNullOrWhiteSpace(current))
            _secrets.Remove("LLMProviders:Default");
    }

    private static IReadOnlyList<WorkflowFileItem> ListWorkflowsFromDirectory(string directory, string source)
    {
        if (!Directory.Exists(directory))
            return [];

        return Directory.GetFiles(directory, "*.yaml")
            .Concat(Directory.GetFiles(directory, "*.yml"))
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new WorkflowFileItem(
                    Filename: info.Name,
                    Source: source,
                    Path: info.FullName,
                    SizeBytes: info.Length,
                    LastModified: info.LastWriteTimeUtc.ToString("o"));
            })
            .OrderBy(x => x.Filename, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<(string Source, string Path)> ResolveWorkflowReadPaths(string filename, WorkflowSource source)
    {
        return source switch
        {
            WorkflowSource.Home => [("home", Path.Combine(AevatarPaths.Workflows, filename))],
            WorkflowSource.Repo => [("repo", Path.Combine(AevatarPaths.RepoRootWorkflows, filename))],
            WorkflowSource.All =>
            [
                ("home", Path.Combine(AevatarPaths.Workflows, filename)),
                ("repo", Path.Combine(AevatarPaths.RepoRootWorkflows, filename)),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "invalid workflow source"),
        };
    }

    private static string NormalizeWorkflowFilename(string filename)
    {
        var sanitized = Path.GetFileName((filename ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(sanitized) || !string.Equals(sanitized, filename?.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("invalid workflow filename", nameof(filename));
        if (!sanitized.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) &&
            !sanitized.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("workflow filename must end with .yaml or .yml", nameof(filename));
        }

        return sanitized;
    }

    private static string NormalizeName(string name, string paramName)
    {
        var normalized = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{paramName} is required", paramName);
        return normalized;
    }

    private static JsonObject ParseJsonObject(string json, string paramName)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException($"{paramName} is required", paramName);

        var node = JsonNode.Parse(json) as JsonObject;
        if (node == null)
            throw new InvalidOperationException($"{paramName} must be a json object");
        return node;
    }

    private static IReadOnlyList<ConnectorConfigEntry> LoadConnectorsFromJson(string json)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"aevatar-connectors-validate-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, json);
            return AevatarConnectorConfig.LoadConnectors(tempPath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static IReadOnlyList<MCPServerEntry> LoadMcpServersFromJson(string json)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"aevatar-mcp-validate-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, json);
            return AevatarMCPConfig.LoadServers(tempPath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static void SaveConnectors(IReadOnlyList<ConnectorConfigEntry> entries)
    {
        var path = AevatarPaths.ConnectorsJson;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var ordered = entries
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var json = JsonSerializer.Serialize(new { connectors = ordered }, JsonWriteOptions);
        File.WriteAllText(path, json);
    }

    private static void SaveMcpServers(IReadOnlyList<MCPServerEntry> entries)
    {
        var path = AevatarPaths.MCPJson;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var ordered = entries
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mcpServers = ordered.ToDictionary(
            keySelector: x => x.Name,
            elementSelector: x => (object)new
            {
                command = x.Command,
                args = x.Args ?? [],
                env = x.Env ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                timeoutMs = x.TimeoutMs,
            },
            comparer: StringComparer.OrdinalIgnoreCase);

        var json = JsonSerializer.Serialize(new { mcpServers }, JsonWriteOptions);
        File.WriteAllText(path, json);
    }

    private static Dictionary<string, string> LoadJsonFileAsFlat(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var content = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(content))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(content);
        return NestedToFlat(doc.RootElement);
    }

    private static void SaveFlatAsJsonFile(string path, IReadOnlyDictionary<string, string> flat)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var nested = FlatToNested(flat);
        var content = JsonSerializer.Serialize(nested, JsonWriteOptions);
        File.WriteAllText(path, content);
    }

    private static ConfigPathStatus EvaluateFilePath(string path)
    {
        var exists = File.Exists(path);
        var readable = false;
        var writable = false;
        long? size = null;
        string? error = null;

        try
        {
            if (exists)
            {
                using var read = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                readable = true;
                size = read.Length;
            }
            else
            {
                readable = true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        try
        {
            if (exists)
            {
                using var write = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                writable = true;
            }
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    writable = ProbeDirectoryWritable(dir);
            }
        }
        catch (Exception ex)
        {
            error ??= ex.Message;
        }

        return new ConfigPathStatus(path, exists, readable, writable, size, error);
    }

    private static ConfigPathStatus EvaluateDirectoryPath(string path)
    {
        var exists = Directory.Exists(path);
        var readable = exists;
        var writable = exists && ProbeDirectoryWritable(path);
        string? error = null;
        if (!exists)
            error = "directory does not exist";
        return new ConfigPathStatus(path, exists, readable, writable, null, error);
    }

    private static bool ProbeDirectoryWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probePath = Path.Combine(directory, $".aevatar-write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, "probe");
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static object FlatToNested(IReadOnlyDictionary<string, string> flat)
    {
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in flat)
        {
            var parts = kv.Key.Split(':');
            var current = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                if (!current.TryGetValue(part, out var next))
                {
                    next = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    current[part] = next;
                }

                current = (Dictionary<string, object>)next;
            }

            current[parts[^1]] = kv.Value;
        }

        return root;
    }

    private static Dictionary<string, string> NestedToFlat(JsonElement element, string prefix = "")
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        NestedToFlatRecursive(element, prefix, result);
        return result;
    }

    private static void NestedToFlatRecursive(JsonElement element, string prefix, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}:{prop.Name}";
                    NestedToFlatRecursive(prop.Value, key, result);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    NestedToFlatRecursive(item, $"{prefix}:{index}", result);
                    index++;
                }

                break;
            default:
                if (!string.IsNullOrEmpty(prefix))
                    result[prefix] = element.ToString();
                break;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
