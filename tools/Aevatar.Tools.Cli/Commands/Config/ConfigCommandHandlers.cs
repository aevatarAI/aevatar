using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.Configuration;
using Aevatar.Tools.Config;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class ConfigCommandHandlers
{
    public static async Task<ConfigCliResult> UiEnsureAsync(
        int port,
        bool noBrowser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ready = await ConfigCommandHandler.EnsureUiAsync(port, noBrowser, cancellationToken);
        return ConfigCliExecution.Ok(
            "config ui is ready",
            new
            {
                url = ready.Url,
                port = ready.Port,
                started = ready.Started,
            });
    }

    public static Task<ConfigCliResult> PathsShowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        return Task.FromResult(ConfigCliExecution.Ok("resolved config paths", ops.GetPaths()));
    }

    public static Task<ConfigCliResult> DoctorAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        return Task.FromResult(ConfigCliExecution.Ok("config doctor completed", ops.GetDoctorReport()));
    }

    public static Task<ConfigCliResult> SecretsListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var ordered = ops.ListSecrets()
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(ConfigCliExecution.Ok("listed secrets", new { count = ordered.Count, items = ordered }));
    }

    public static Task<ConfigCliResult> SecretsGetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        return Task.FromResult(
            ops.TryGetSecret(key, out var value)
                ? ConfigCliExecution.Ok("secret found", new { key = key.Trim(), value })
                : ConfigCliExecution.NotFound($"secret key not found: {key.Trim()}", new { key = key.Trim() }));
    }

    public static async Task<ConfigCliResult> SecretsSetAsync(string key, string? value, bool fromStdin, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedValue = await ConfigCliExecution.ResolveInputValueAsync(value, fromStdin, "value");
        var ops = CreateOperations();
        ops.SetSecret(key, resolvedValue);
        return ConfigCliExecution.Ok("secret value saved", new { key = key.Trim() });
    }

    public static Task<ConfigCliResult> SecretsRemoveAsync(string key, bool yes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConfigCliExecution.ConfirmOrThrow(yes, $"remove secret key '{key.Trim()}'"))
            return Task.FromResult(ConfigCliExecution.ValidationFailed("operation cancelled by user"));

        var ops = CreateOperations();
        return Task.FromResult(
            ops.RemoveSecret(key)
                ? ConfigCliExecution.Ok("secret key removed", new { key = key.Trim(), removed = true })
                : ConfigCliExecution.NotFound($"secret key not found: {key.Trim()}", new { key = key.Trim(), removed = false }));
    }

    public static async Task<ConfigCliResult> SecretsImportAsync(string? file, bool fromStdin, bool yes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConfigCliExecution.ConfirmOrThrow(yes, "replace secrets from imported json"))
            return ConfigCliExecution.ValidationFailed("operation cancelled by user");

        var payload = await ReadJsonPayloadAsync(file, fromStdin, cancellationToken);
        var ops = CreateOperations();
        var count = ops.ImportSecretsJson(payload);
        return ConfigCliExecution.Ok("secrets imported", new { keyCount = count });
    }

    public static async Task<ConfigCliResult> SecretsExportAsync(string? file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var json = ops.ExportSecretsJson();
        if (!string.IsNullOrWhiteSpace(file))
        {
            await WriteTextFileAsync(file, json, cancellationToken);
            return ConfigCliExecution.Ok("secrets exported", new { file = Path.GetFullPath(file) });
        }

        return ConfigCliExecution.Ok("secrets exported", new { json = JsonNode.Parse(json) });
    }

    public static Task<ConfigCliResult> ConfigJsonListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var ordered = ops.ListConfigJson()
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(ConfigCliExecution.Ok("listed config.json keys", new { count = ordered.Count, items = ordered }));
    }

    public static Task<ConfigCliResult> ConfigJsonGetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        return Task.FromResult(
            ops.TryGetConfigJson(key, out var value)
                ? ConfigCliExecution.Ok("config.json key found", new { key = key.Trim(), value })
                : ConfigCliExecution.NotFound($"config.json key not found: {key.Trim()}", new { key = key.Trim() }));
    }

    public static async Task<ConfigCliResult> ConfigJsonSetAsync(string key, string? value, bool fromStdin, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedValue = await ConfigCliExecution.ResolveInputValueAsync(value, fromStdin, "value");
        var ops = CreateOperations();
        ops.SetConfigJson(key, resolvedValue);
        return ConfigCliExecution.Ok("config.json value saved", new { key = key.Trim() });
    }

    public static Task<ConfigCliResult> ConfigJsonRemoveAsync(string key, bool yes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConfigCliExecution.ConfirmOrThrow(yes, $"remove config.json key '{key.Trim()}'"))
            return Task.FromResult(ConfigCliExecution.ValidationFailed("operation cancelled by user"));

        var ops = CreateOperations();
        return Task.FromResult(
            ops.RemoveConfigJson(key)
                ? ConfigCliExecution.Ok("config.json key removed", new { key = key.Trim(), removed = true })
                : ConfigCliExecution.NotFound($"config.json key not found: {key.Trim()}", new { key = key.Trim(), removed = false }));
    }

    public static async Task<ConfigCliResult> ConfigJsonImportAsync(string? file, bool fromStdin, bool yes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConfigCliExecution.ConfirmOrThrow(yes, "replace config.json from imported json"))
            return ConfigCliExecution.ValidationFailed("operation cancelled by user");

        var payload = await ReadJsonPayloadAsync(file, fromStdin, cancellationToken);
        var ops = CreateOperations();
        var count = ops.ImportConfigJson(payload);
        return ConfigCliExecution.Ok("config.json imported", new { keyCount = count });
    }

    public static async Task<ConfigCliResult> ConfigJsonExportAsync(string? file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var json = ops.ExportConfigJson();
        if (!string.IsNullOrWhiteSpace(file))
        {
            await WriteTextFileAsync(file, json, cancellationToken);
            return ConfigCliExecution.Ok("config.json exported", new { file = Path.GetFullPath(file) });
        }

        return ConfigCliExecution.Ok("config.json exported", new { json = JsonNode.Parse(json) });
    }

    public static Task<ConfigCliResult> LlmProviderTypesListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var items = ops.ListLlmProviderTypes();
        return Task.FromResult(ConfigCliExecution.Ok("listed llm provider types", new { count = items.Count, items }));
    }

    public static Task<ConfigCliResult> LlmInstancesListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var items = ops.ListLlmInstances();
        return Task.FromResult(ConfigCliExecution.Ok("listed llm instances", new { count = items.Count, items }));
    }

    public static Task<ConfigCliResult> LlmInstancesGetAsync(string providerName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var provider = ops.GetLlmInstance(providerName);
        return Task.FromResult(ConfigCliExecution.Ok("resolved llm instance", provider));
    }

    public static async Task<ConfigCliResult> LlmInstancesUpsertAsync(
        string providerName,
        string providerType,
        string model,
        string? endpoint,
        string? apiKey,
        bool apiKeyFromStdin,
        string? copyApiKeyFrom,
        bool forceCopyApiKeyFrom,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? resolvedApiKey = null;
        if (apiKeyFromStdin || !string.IsNullOrWhiteSpace(apiKey))
            resolvedApiKey = await ConfigCliExecution.ResolveInputValueAsync(apiKey, apiKeyFromStdin, "apiKey");

        var ops = CreateOperations();
        var data = ops.UpsertLlmInstance(new UpsertLLMInstanceRequest(
            ProviderName: providerName,
            ProviderType: providerType,
            Model: model,
            Endpoint: endpoint,
            ApiKey: resolvedApiKey,
            CopyApiKeyFrom: copyApiKeyFrom,
            ForceCopyApiKeyFrom: forceCopyApiKeyFrom));
        return ConfigCliExecution.Ok("llm instance upserted", data);
    }

    public static Task<ConfigCliResult> LlmInstancesDeleteAsync(string providerName, bool yes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConfigCliExecution.ConfirmOrThrow(yes, $"delete llm instance '{providerName.Trim()}'"))
            return Task.FromResult(ConfigCliExecution.ValidationFailed("operation cancelled by user"));

        var ops = CreateOperations();
        var removed = ops.DeleteLlmInstance(providerName);
        return Task.FromResult(
            removed
                ? ConfigCliExecution.Ok("llm instance deleted", new { providerName = providerName.Trim(), removed = true })
                : ConfigCliExecution.NotFound($"llm instance not found: {providerName.Trim()}", new { providerName = providerName.Trim(), removed = false }));
    }

    public static Task<ConfigCliResult> LlmDefaultGetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var providerName = ops.GetLlmDefaultProvider();
        return Task.FromResult(ConfigCliExecution.Ok("resolved llm default provider", new { providerName }));
    }

    public static Task<ConfigCliResult> LlmDefaultSetAsync(string providerName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var value = ops.SetLlmDefaultProvider(providerName);
        return Task.FromResult(ConfigCliExecution.Ok("llm default provider updated", new { providerName = value }));
    }

    public static Task<ConfigCliResult> LlmApiKeyGetAsync(string providerName, bool reveal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var data = ops.GetLlmApiKey(providerName, reveal);
        return Task.FromResult(ConfigCliExecution.Ok("llm api key status", data));
    }

    public static async Task<ConfigCliResult> LlmApiKeySetAsync(string providerName, string? value, bool fromStdin, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = await ConfigCliExecution.ResolveInputValueAsync(value, fromStdin, "value");
        var ops = CreateOperations();
        ops.SetLlmApiKey(providerName, resolved);
        return ConfigCliExecution.Ok("llm api key updated", new { providerName = providerName.Trim() });
    }

    public static Task<ConfigCliResult> LlmApiKeyRemoveAsync(string providerName, bool yes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConfigCliExecution.ConfirmOrThrow(yes, $"remove llm api key for '{providerName.Trim()}'"))
            return Task.FromResult(ConfigCliExecution.ValidationFailed("operation cancelled by user"));

        var ops = CreateOperations();
        var removed = ops.RemoveLlmApiKey(providerName);
        return Task.FromResult(
            removed
                ? ConfigCliExecution.Ok("llm api key removed", new { providerName = providerName.Trim(), removed = true })
                : ConfigCliExecution.NotFound($"llm api key not found for provider: {providerName.Trim()}", new { providerName = providerName.Trim(), removed = false }));
    }

    public static async Task<ConfigCliResult> LlmProbeTestAsync(string providerName, CancellationToken cancellationToken)
    {
        var ops = CreateOperations();
        var payload = await ops.ProbeLlmTestAsync(providerName, cancellationToken);
        return BuildProbeResult(payload);
    }

    public static async Task<ConfigCliResult> LlmProbeModelsAsync(string providerName, int limit, CancellationToken cancellationToken)
    {
        var ops = CreateOperations();
        var payload = await ops.ProbeLlmModelsAsync(providerName, limit, cancellationToken);
        return BuildProbeResult(payload);
    }

    public static Task<ConfigCliResult> WorkflowsListAsync(string sourceText, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ParseWorkflowSource(sourceText);
        var ops = CreateOperations();
        var items = ops.ListWorkflows(source);
        return Task.FromResult(ConfigCliExecution.Ok("listed workflows", new { count = items.Count, items }));
    }

    public static Task<ConfigCliResult> WorkflowsGetAsync(string filename, string sourceText, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ParseWorkflowSource(sourceText);
        var ops = CreateOperations();
        var result = ops.GetWorkflow(filename, source);
        if (result == null)
            return Task.FromResult(ConfigCliExecution.NotFound($"workflow not found: {filename}", new { filename = filename.Trim(), source = sourceText.Trim().ToLowerInvariant() }));
        return Task.FromResult(ConfigCliExecution.Ok("workflow loaded", result));
    }

    public static async Task<ConfigCliResult> WorkflowsPutAsync(
        string filename,
        string sourceText,
        string? file,
        bool fromStdin,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ParseWorkflowSource(sourceText);
        if (source == WorkflowSource.All)
            throw new ArgumentException("--source for workflows put must be home or repo");
        var content = await ReadTextPayloadAsync(file, fromStdin, cancellationToken);
        var ops = CreateOperations();
        var saved = ops.PutWorkflow(filename, content, source);
        return ConfigCliExecution.Ok("workflow saved", saved);
    }

    public static Task<ConfigCliResult> WorkflowsDeleteAsync(string filename, string sourceText, bool yes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ParseWorkflowSource(sourceText);
        if (source == WorkflowSource.All)
            throw new ArgumentException("--source for workflows delete must be home or repo");
        if (!ConfigCliExecution.ConfirmOrThrow(yes, $"delete workflow '{filename.Trim()}' from {source.ToString().ToLowerInvariant()}"))
            return Task.FromResult(ConfigCliExecution.ValidationFailed("operation cancelled by user"));

        var ops = CreateOperations();
        var removed = ops.DeleteWorkflow(filename, source);
        return Task.FromResult(
            removed
                ? ConfigCliExecution.Ok("workflow deleted", new { filename = filename.Trim(), source = source.ToString().ToLowerInvariant(), removed = true })
                : ConfigCliExecution.NotFound($"workflow not found: {filename.Trim()}", new { filename = filename.Trim(), source = source.ToString().ToLowerInvariant(), removed = false }));
    }

    public static Task<ConfigCliResult> ConnectorsListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var items = ops.ListConnectors();
        return Task.FromResult(ConfigCliExecution.Ok("listed connectors", new { count = items.Count, items }));
    }

    public static Task<ConfigCliResult> ConnectorsGetAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var item = ops.GetConnector(name);
        if (item == null)
            return Task.FromResult(ConfigCliExecution.NotFound($"connector not found: {name.Trim()}", new { name = name.Trim() }));
        return Task.FromResult(ConfigCliExecution.Ok("connector loaded", item));
    }

    public static async Task<ConfigCliResult> ConnectorsPutAsync(string name, string? entryJson, string? file, bool fromStdin, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await ResolveEntryJsonAsync(entryJson, file, fromStdin, cancellationToken);
        var ops = CreateOperations();
        var item = ops.UpsertConnector(name, payload);
        return ConfigCliExecution.Ok("connector upserted", item);
    }

    public static Task<ConfigCliResult> ConnectorsDeleteAsync(string name, bool yes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConfigCliExecution.ConfirmOrThrow(yes, $"delete connector '{name.Trim()}'"))
            return Task.FromResult(ConfigCliExecution.ValidationFailed("operation cancelled by user"));

        var ops = CreateOperations();
        var removed = ops.DeleteConnector(name);
        return Task.FromResult(
            removed
                ? ConfigCliExecution.Ok("connector deleted", new { name = name.Trim(), removed = true })
                : ConfigCliExecution.NotFound($"connector not found: {name.Trim()}", new { name = name.Trim(), removed = false }));
    }

    public static async Task<ConfigCliResult> ConnectorsValidateAsync(string? file, bool fromStdin, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await ResolveOptionalJsonInputAsync(file, fromStdin, () => CreateOperations().ExportConnectorsJson(), cancellationToken);
        var ops = CreateOperations();
        var result = ops.ValidateConnectorsJson(payload);
        return ConfigCliExecution.Ok("connectors json valid", result);
    }

    public static async Task<ConfigCliResult> ConnectorsImportAsync(string? file, bool fromStdin, bool yes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConfigCliExecution.ConfirmOrThrow(yes, "replace connectors.json from imported json"))
            return ConfigCliExecution.ValidationFailed("operation cancelled by user");
        var payload = await ReadJsonPayloadAsync(file, fromStdin, cancellationToken);
        var ops = CreateOperations();
        var count = ops.ImportConnectorsJson(payload);
        return ConfigCliExecution.Ok("connectors imported", new { count });
    }

    public static async Task<ConfigCliResult> ConnectorsExportAsync(string? file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var json = ops.ExportConnectorsJson();
        if (!string.IsNullOrWhiteSpace(file))
        {
            await WriteTextFileAsync(file, json, cancellationToken);
            return ConfigCliExecution.Ok("connectors exported", new { file = Path.GetFullPath(file) });
        }

        return ConfigCliExecution.Ok("connectors exported", new { json = JsonNode.Parse(json) });
    }

    public static Task<ConfigCliResult> McpListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var items = ops.ListMcpServers();
        return Task.FromResult(ConfigCliExecution.Ok("listed mcp servers", new { count = items.Count, items }));
    }

    public static Task<ConfigCliResult> McpGetAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var item = ops.GetMcpServer(name);
        if (item == null)
            return Task.FromResult(ConfigCliExecution.NotFound($"mcp server not found: {name.Trim()}", new { name = name.Trim() }));
        return Task.FromResult(ConfigCliExecution.Ok("mcp server loaded", item));
    }

    public static async Task<ConfigCliResult> McpPutAsync(string name, string? entryJson, string? file, bool fromStdin, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await ResolveEntryJsonAsync(entryJson, file, fromStdin, cancellationToken);
        var ops = CreateOperations();
        var item = ops.UpsertMcpServer(name, payload);
        return ConfigCliExecution.Ok("mcp server upserted", item);
    }

    public static Task<ConfigCliResult> McpDeleteAsync(string name, bool yes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConfigCliExecution.ConfirmOrThrow(yes, $"delete mcp server '{name.Trim()}'"))
            return Task.FromResult(ConfigCliExecution.ValidationFailed("operation cancelled by user"));

        var ops = CreateOperations();
        var removed = ops.DeleteMcpServer(name);
        return Task.FromResult(
            removed
                ? ConfigCliExecution.Ok("mcp server deleted", new { name = name.Trim(), removed = true })
                : ConfigCliExecution.NotFound($"mcp server not found: {name.Trim()}", new { name = name.Trim(), removed = false }));
    }

    public static async Task<ConfigCliResult> McpValidateAsync(string? file, bool fromStdin, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await ResolveOptionalJsonInputAsync(file, fromStdin, () => CreateOperations().ExportMcpJson(), cancellationToken);
        var ops = CreateOperations();
        var result = ops.ValidateMcpJson(payload);
        return ConfigCliExecution.Ok("mcp json valid", result);
    }

    public static async Task<ConfigCliResult> McpImportAsync(string? file, bool fromStdin, bool yes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConfigCliExecution.ConfirmOrThrow(yes, "replace mcp.json from imported json"))
            return ConfigCliExecution.ValidationFailed("operation cancelled by user");
        var payload = await ReadJsonPayloadAsync(file, fromStdin, cancellationToken);
        var ops = CreateOperations();
        var count = ops.ImportMcpJson(payload);
        return ConfigCliExecution.Ok("mcp servers imported", new { count });
    }

    public static async Task<ConfigCliResult> McpExportAsync(string? file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ops = CreateOperations();
        var json = ops.ExportMcpJson();
        if (!string.IsNullOrWhiteSpace(file))
        {
            await WriteTextFileAsync(file, json, cancellationToken);
            return ConfigCliExecution.Ok("mcp json exported", new { file = Path.GetFullPath(file) });
        }

        return ConfigCliExecution.Ok("mcp json exported", new { json = JsonNode.Parse(json) });
    }

    private static ConfigOperations CreateOperations()
    {
        AevatarPaths.EnsureDirectories();
        var store = new AevatarSecretsStore(AevatarPaths.SecretsJson);
        return new ConfigOperations(new SecretsStoreAdapter(store));
    }

    private static async Task<string> ReadJsonPayloadAsync(string? file, bool fromStdin, CancellationToken cancellationToken)
    {
        var payload = await ReadTextPayloadAsync(file, fromStdin, cancellationToken);
        using var _ = JsonDocument.Parse(payload);
        return payload;
    }

    private static async Task<string> ReadTextPayloadAsync(string? file, bool fromStdin, CancellationToken cancellationToken)
    {
        if (fromStdin && !string.IsNullOrWhiteSpace(file))
            throw new ArgumentException("--file cannot be used together with --stdin");

        if (fromStdin)
        {
            var stdin = await Console.In.ReadToEndAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(stdin))
                throw new ArgumentException("stdin is empty");
            return stdin;
        }

        if (string.IsNullOrWhiteSpace(file))
            throw new ArgumentException("one of --file or --stdin is required");
        return await File.ReadAllTextAsync(file, cancellationToken);
    }

    private static async Task<string> ResolveOptionalJsonInputAsync(
        string? file,
        bool fromStdin,
        Func<string> fallbackProvider,
        CancellationToken cancellationToken)
    {
        if (fromStdin || !string.IsNullOrWhiteSpace(file))
            return await ReadJsonPayloadAsync(file, fromStdin, cancellationToken);
        return fallbackProvider();
    }

    private static async Task<string> ResolveEntryJsonAsync(
        string? entryJson,
        string? file,
        bool fromStdin,
        CancellationToken cancellationToken)
    {
        var hasInline = !string.IsNullOrWhiteSpace(entryJson);
        var inputCount = (hasInline ? 1 : 0) + (!string.IsNullOrWhiteSpace(file) ? 1 : 0) + (fromStdin ? 1 : 0);
        if (inputCount != 1)
            throw new ArgumentException("exactly one of --entry-json, --file or --stdin is required");

        if (hasInline)
        {
            using var _ = JsonDocument.Parse(entryJson!);
            return entryJson!;
        }

        return await ReadJsonPayloadAsync(file, fromStdin, cancellationToken);
    }

    private static async Task WriteTextFileAsync(string file, string content, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(file);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    private static WorkflowSource ParseWorkflowSource(string sourceText)
    {
        var normalized = (sourceText ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "home" => WorkflowSource.Home,
            "repo" => WorkflowSource.Repo,
            "all" => WorkflowSource.All,
            _ => throw new ArgumentException("workflow source must be one of: home, repo, all"),
        };
    }

    private static ConfigCliResult BuildProbeResult(object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload);
        var ok = false;
        var message = "probe completed";
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("ok", out var okNode) && okNode is JsonValue okValue)
                ok = okValue.TryGetValue<bool>(out var flag) && flag;

            if (!ok && obj.TryGetPropertyValue("error", out var errorNode) && errorNode is JsonValue errorValue && errorValue.TryGetValue<string>(out var err) && !string.IsNullOrWhiteSpace(err))
                message = err;
        }

        return ok
            ? ConfigCliExecution.Ok(message, payload)
            : ConfigCliExecution.ExternalProbeFailed(message, payload);
    }
}
