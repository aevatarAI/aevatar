using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Domain.Models;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Studio.Infrastructure.Storage;

public sealed class FileStudioWorkspaceStore : IStudioWorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _appDataDirectory;
    private readonly string _defaultWorkflowDirectory;
    private readonly string _executionsDirectory;
    private readonly string _settingsFilePath;
    private readonly string _aevatarHomeDirectory;
    private readonly string _connectorsFilePath;
    private readonly string _rolesFilePath;
    private readonly string _connectorsDraftFilePath;
    private readonly string _rolesDraftFilePath;
    private readonly string _defaultRuntimeBaseUrl;
    private readonly bool _forceLocalRuntime;

    public FileStudioWorkspaceStore(IOptions<StudioStorageOptions> options)
    {
        var storageOptions = options.Value.ResolveRootDirectory();
        _appDataDirectory = storageOptions.RootDirectory;
        _defaultRuntimeBaseUrl = string.IsNullOrWhiteSpace(storageOptions.DefaultRuntimeBaseUrl)
            ? "http://127.0.0.1:5100"
            : storageOptions.DefaultRuntimeBaseUrl.Trim().TrimEnd('/');
        _forceLocalRuntime = storageOptions.ForceLocalRuntime;
        _aevatarHomeDirectory = ResolveAevatarHomeDirectory();
        _defaultWorkflowDirectory = Path.Combine(_aevatarHomeDirectory, "workflows");
        _executionsDirectory = Path.Combine(_appDataDirectory, "executions");
        _settingsFilePath = Path.Combine(_appDataDirectory, "workspace-settings.json");
        _connectorsFilePath = Path.Combine(_aevatarHomeDirectory, "connectors.json");
        _rolesFilePath = Path.Combine(_aevatarHomeDirectory, "roles.json");
        _connectorsDraftFilePath = Path.Combine(_aevatarHomeDirectory, "connectors_draft.json");
        _rolesDraftFilePath = Path.Combine(_aevatarHomeDirectory, "roles_draft.json");

        Directory.CreateDirectory(_appDataDirectory);
        Directory.CreateDirectory(_defaultWorkflowDirectory);
        Directory.CreateDirectory(_executionsDirectory);
    }

    public async Task<StudioWorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var persisted = await ReadSettingsAsync(cancellationToken);
        var runtimeBaseUrl = _forceLocalRuntime
            ? _defaultRuntimeBaseUrl
            : string.IsNullOrWhiteSpace(persisted.RuntimeBaseUrl)
            ? _defaultRuntimeBaseUrl
            : persisted.RuntimeBaseUrl.Trim().TrimEnd('/');
        var appearanceTheme = NormalizeAppearanceTheme(persisted.AppearanceTheme);
        var colorMode = NormalizeColorMode(persisted.ColorMode);

        var directories = new List<StudioWorkspaceDirectory>
        {
            new(
                DirectoryId: CreateStableId(_defaultWorkflowDirectory),
                Label: "Aevatar",
                Path: _defaultWorkflowDirectory,
                IsBuiltIn: true),
        };

        foreach (var item in persisted.Directories)
        {
            if (string.IsNullOrWhiteSpace(item.Path))
            {
                continue;
            }

            var normalizedPath = Path.GetFullPath(item.Path);
            if (directories.Any(directory => string.Equals(directory.Path, normalizedPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            directories.Add(new StudioWorkspaceDirectory(
                DirectoryId: CreateStableId(normalizedPath),
                Label: string.IsNullOrWhiteSpace(item.Label) ? Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar)) : item.Label.Trim(),
                Path: normalizedPath,
                IsBuiltIn: false));
        }

        return new StudioWorkspaceSettings(runtimeBaseUrl, directories, appearanceTheme, colorMode);
    }

    public async Task SaveSettingsAsync(StudioWorkspaceSettings settings, CancellationToken cancellationToken = default)
    {
        var persisted = new PersistedWorkspaceSettings
        {
            RuntimeBaseUrl = _forceLocalRuntime ? _defaultRuntimeBaseUrl : settings.RuntimeBaseUrl,
            AppearanceTheme = NormalizeAppearanceTheme(settings.AppearanceTheme),
            ColorMode = NormalizeColorMode(settings.ColorMode),
            Directories = settings.Directories
                .Where(directory => !directory.IsBuiltIn)
                .Select(directory => new PersistedWorkspaceDirectory
                {
                    Label = directory.Label,
                    Path = directory.Path,
                })
                .ToList(),
        };

        await WriteJsonAsync(_settingsFilePath, persisted, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredWorkflowFile>> ListWorkflowFilesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var results = new List<StoredWorkflowFile>();

        foreach (var directory in settings.Directories)
        {
            if (!Directory.Exists(directory.Path))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(directory.Path, "*.y*ml", SearchOption.AllDirectories);
            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var yaml = await File.ReadAllTextAsync(filePath, cancellationToken);
                var updatedAtUtc = File.GetLastWriteTimeUtc(filePath);
                results.Add(new StoredWorkflowFile(
                    WorkflowId: CreateStableId(filePath),
                    Name: Path.GetFileNameWithoutExtension(filePath),
                    FileName: Path.GetFileName(filePath),
                    FilePath: filePath,
                    DirectoryId: directory.DirectoryId,
                    DirectoryLabel: directory.Label,
                    Yaml: yaml,
                    Layout: await ReadJsonAsync<WorkflowLayoutDocument>(GetLayoutFilePath(filePath), cancellationToken),
                    UpdatedAtUtc: new DateTimeOffset(updatedAtUtc, TimeSpan.Zero)));
            }
        }

        return results
            .OrderByDescending(file => file.UpdatedAtUtc)
            .ToList();
    }

    public async Task<StoredWorkflowFile?> GetWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var workflows = await ListWorkflowFilesAsync(cancellationToken);
        return workflows.FirstOrDefault(file => string.Equals(file.WorkflowId, workflowId, StringComparison.Ordinal));
    }

    public async Task<StoredWorkflowFile> SaveWorkflowFileAsync(StoredWorkflowFile workflowFile, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(workflowFile.FilePath)!);
        await File.WriteAllTextAsync(workflowFile.FilePath, workflowFile.Yaml, Encoding.UTF8, cancellationToken);
        if (workflowFile.Layout is not null)
        {
            await WriteJsonAsync(GetLayoutFilePath(workflowFile.FilePath), workflowFile.Layout, cancellationToken);
        }
        var updatedAtUtc = File.GetLastWriteTimeUtc(workflowFile.FilePath);

        return workflowFile with
        {
            WorkflowId = CreateStableId(workflowFile.FilePath),
            Layout = workflowFile.Layout ?? await ReadJsonAsync<WorkflowLayoutDocument>(GetLayoutFilePath(workflowFile.FilePath), cancellationToken),
            UpdatedAtUtc = new DateTimeOffset(updatedAtUtc, TimeSpan.Zero),
        };
    }

    public async Task<IReadOnlyList<StoredExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<StoredExecutionRecord>();
        foreach (var filePath in Directory.EnumerateFiles(_executionsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await ReadJsonAsync<StoredExecutionRecord>(filePath, cancellationToken);
            if (record is not null)
            {
                results.Add(record);
            }
        }

        return results
            .OrderByDescending(item => item.StartedAtUtc)
            .ToList();
    }

    public Task<StoredExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default)
    {
        var path = GetExecutionFilePath(executionId);
        return ReadJsonAsync<StoredExecutionRecord>(path, cancellationToken);
    }

    public async Task<StoredExecutionRecord> SaveExecutionAsync(StoredExecutionRecord execution, CancellationToken cancellationToken = default)
    {
        await WriteJsonAsync(GetExecutionFilePath(execution.ExecutionId), execution, cancellationToken);
        return execution;
    }

    public async Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_connectorsFilePath))
        {
            return new StoredConnectorCatalog(
                _aevatarHomeDirectory,
                _connectorsFilePath,
                FileExists: false,
                Connectors: []);
        }

        await using var stream = File.OpenRead(_connectorsFilePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var connectors = ParseConnectors(document.RootElement);

        return new StoredConnectorCatalog(
            _aevatarHomeDirectory,
            _connectorsFilePath,
            FileExists: true,
            Connectors: connectors);
    }

    public async Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(StoredConnectorCatalog catalog, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_aevatarHomeDirectory);

        var payload = new ConnectorJsonDocument
        {
            Connectors = catalog.Connectors
                .Select(connector => new ConnectorJsonEntry
                {
                    Name = connector.Name,
                    Type = connector.Type,
                    Enabled = connector.Enabled,
                    TimeoutMs = connector.TimeoutMs,
                    Retry = connector.Retry,
                    Http = new HttpConnectorJsonConfig
                    {
                        BaseUrl = connector.Http.BaseUrl,
                        AllowedMethods = connector.Http.AllowedMethods.ToArray(),
                        AllowedPaths = connector.Http.AllowedPaths.ToArray(),
                        AllowedInputKeys = connector.Http.AllowedInputKeys.ToArray(),
                        DefaultHeaders = connector.Http.DefaultHeaders.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
                    },
                    Cli = new CliConnectorJsonConfig
                    {
                        Command = connector.Cli.Command,
                        FixedArguments = connector.Cli.FixedArguments.ToArray(),
                        AllowedOperations = connector.Cli.AllowedOperations.ToArray(),
                        AllowedInputKeys = connector.Cli.AllowedInputKeys.ToArray(),
                        WorkingDirectory = connector.Cli.WorkingDirectory,
                        Environment = connector.Cli.Environment.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
                    },
                    Mcp = new McpConnectorJsonConfig
                    {
                        ServerName = connector.Mcp.ServerName,
                        Command = connector.Mcp.Command,
                        Arguments = connector.Mcp.Arguments.ToArray(),
                        Environment = connector.Mcp.Environment.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
                        DefaultTool = connector.Mcp.DefaultTool,
                        AllowedTools = connector.Mcp.AllowedTools.ToArray(),
                        AllowedInputKeys = connector.Mcp.AllowedInputKeys.ToArray(),
                    },
                })
                .ToList(),
        };

        await using var stream = File.Create(_connectorsFilePath);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);

        return catalog with
        {
            HomeDirectory = _aevatarHomeDirectory,
            FilePath = _connectorsFilePath,
            FileExists = true,
        };
    }

    public async Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_connectorsDraftFilePath))
        {
            return new StoredConnectorDraft(
                HomeDirectory: _aevatarHomeDirectory,
                FilePath: _connectorsDraftFilePath,
                FileExists: false,
                UpdatedAtUtc: null,
                Draft: null);
        }

        await using var stream = File.OpenRead(_connectorsDraftFilePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var updatedAtUtc = TryGetPropertyIgnoreCase(root, "updatedAtUtc", out var updatedAtNode) && updatedAtNode.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(updatedAtNode.GetString(), out var parsedUpdatedAt)
            ? parsedUpdatedAt
            : File.GetLastWriteTimeUtc(_connectorsDraftFilePath);

        var draftNode = TryGetPropertyIgnoreCase(root, "connector", out var connectorNode) ? connectorNode : root;
        var draft = draftNode.ValueKind == JsonValueKind.Object ? ParseConnector(draftNode, null) : null;

        return new StoredConnectorDraft(
            HomeDirectory: _aevatarHomeDirectory,
            FilePath: _connectorsDraftFilePath,
            FileExists: true,
            UpdatedAtUtc: updatedAtUtc,
            Draft: draft);
    }

    public async Task<StoredConnectorDraft> SaveConnectorDraftAsync(StoredConnectorDraft draft, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_aevatarHomeDirectory);
        var updatedAtUtc = draft.UpdatedAtUtc ?? DateTimeOffset.UtcNow;
        var payload = new ConnectorDraftJsonDocument
        {
            UpdatedAtUtc = updatedAtUtc,
            Connector = draft.Draft is null ? new ConnectorJsonEntry() : ToConnectorJsonEntry(draft.Draft),
        };

        await using var stream = File.Create(_connectorsDraftFilePath);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);

        return draft with
        {
            HomeDirectory = _aevatarHomeDirectory,
            FilePath = _connectorsDraftFilePath,
            FileExists = true,
            UpdatedAtUtc = updatedAtUtc,
        };
    }

    public Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_connectorsDraftFilePath))
        {
            File.Delete(_connectorsDraftFilePath);
        }

        return Task.CompletedTask;
    }

    public async Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_rolesFilePath))
        {
            return new StoredRoleCatalog(
                _aevatarHomeDirectory,
                _rolesFilePath,
                FileExists: false,
                Roles: []);
        }

        await using var stream = File.OpenRead(_rolesFilePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var roles = ParseRoles(document.RootElement);

        return new StoredRoleCatalog(
            _aevatarHomeDirectory,
            _rolesFilePath,
            FileExists: true,
            Roles: roles);
    }

    public async Task<StoredRoleCatalog> SaveRoleCatalogAsync(StoredRoleCatalog catalog, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_aevatarHomeDirectory);

        var payload = new RoleJsonDocument
        {
            Roles = catalog.Roles
                .Select(role => new RoleJsonEntry
                {
                    Id = role.Id,
                    Name = role.Name,
                    SystemPrompt = role.SystemPrompt,
                    Provider = role.Provider,
                    Model = role.Model,
                    Connectors = role.Connectors.ToArray(),
                })
                .ToList(),
        };

        await using var stream = File.Create(_rolesFilePath);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);

        return catalog with
        {
            HomeDirectory = _aevatarHomeDirectory,
            FilePath = _rolesFilePath,
            FileExists = true,
        };
    }

    public async Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_rolesDraftFilePath))
        {
            return new StoredRoleDraft(
                HomeDirectory: _aevatarHomeDirectory,
                FilePath: _rolesDraftFilePath,
                FileExists: false,
                UpdatedAtUtc: null,
                Draft: null);
        }

        await using var stream = File.OpenRead(_rolesDraftFilePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var updatedAtUtc = TryGetPropertyIgnoreCase(root, "updatedAtUtc", out var updatedAtNode) && updatedAtNode.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(updatedAtNode.GetString(), out var parsedUpdatedAt)
            ? parsedUpdatedAt
            : File.GetLastWriteTimeUtc(_rolesDraftFilePath);

        var draftNode = TryGetPropertyIgnoreCase(root, "role", out var roleNode) ? roleNode : root;
        var draft = draftNode.ValueKind == JsonValueKind.Object ? ParseRole(draftNode, null) : null;

        return new StoredRoleDraft(
            HomeDirectory: _aevatarHomeDirectory,
            FilePath: _rolesDraftFilePath,
            FileExists: true,
            UpdatedAtUtc: updatedAtUtc,
            Draft: draft);
    }

    public async Task<StoredRoleDraft> SaveRoleDraftAsync(StoredRoleDraft draft, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_aevatarHomeDirectory);
        var updatedAtUtc = draft.UpdatedAtUtc ?? DateTimeOffset.UtcNow;
        var payload = new RoleDraftJsonDocument
        {
            UpdatedAtUtc = updatedAtUtc,
            Role = draft.Draft is null ? new RoleJsonEntry() : ToRoleJsonEntry(draft.Draft),
        };

        await using var stream = File.Create(_rolesDraftFilePath);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);

        return draft with
        {
            HomeDirectory = _aevatarHomeDirectory,
            FilePath = _rolesDraftFilePath,
            FileExists = true,
            UpdatedAtUtc = updatedAtUtc,
        };
    }

    public Task DeleteRoleDraftAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_rolesDraftFilePath))
        {
            File.Delete(_rolesDraftFilePath);
        }

        return Task.CompletedTask;
    }

    private async Task<PersistedWorkspaceSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await ReadJsonAsync<PersistedWorkspaceSettings>(_settingsFilePath, cancellationToken);
        return settings ?? new PersistedWorkspaceSettings();
    }

    private async Task<T?> ReadJsonAsync<T>(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return default;
        }

        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return default;
        }
    }

    private async Task WriteJsonAsync<T>(string filePath, T payload, CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directoryPath);

        var tempFilePath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             tempFilePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(filePath))
            {
                File.Move(tempFilePath, filePath, overwrite: true);
            }
            else
            {
                File.Move(tempFilePath, filePath);
            }
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }

    private static ConnectorJsonEntry ToConnectorJsonEntry(StoredConnectorDefinition connector) =>
        new()
        {
            Name = connector.Name,
            Type = connector.Type,
            Enabled = connector.Enabled,
            TimeoutMs = connector.TimeoutMs,
            Retry = connector.Retry,
            Http = new HttpConnectorJsonConfig
            {
                BaseUrl = connector.Http.BaseUrl,
                AllowedMethods = connector.Http.AllowedMethods.ToArray(),
                AllowedPaths = connector.Http.AllowedPaths.ToArray(),
                AllowedInputKeys = connector.Http.AllowedInputKeys.ToArray(),
                DefaultHeaders = connector.Http.DefaultHeaders.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
            },
            Cli = new CliConnectorJsonConfig
            {
                Command = connector.Cli.Command,
                FixedArguments = connector.Cli.FixedArguments.ToArray(),
                AllowedOperations = connector.Cli.AllowedOperations.ToArray(),
                AllowedInputKeys = connector.Cli.AllowedInputKeys.ToArray(),
                WorkingDirectory = connector.Cli.WorkingDirectory,
                Environment = connector.Cli.Environment.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
            },
            Mcp = new McpConnectorJsonConfig
            {
                ServerName = connector.Mcp.ServerName,
                Command = connector.Mcp.Command,
                Arguments = connector.Mcp.Arguments.ToArray(),
                Environment = connector.Mcp.Environment.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
                DefaultTool = connector.Mcp.DefaultTool,
                AllowedTools = connector.Mcp.AllowedTools.ToArray(),
                AllowedInputKeys = connector.Mcp.AllowedInputKeys.ToArray(),
            },
        };

    private static RoleJsonEntry ToRoleJsonEntry(StoredRoleDefinition role) =>
        new()
        {
            Id = role.Id,
            Name = role.Name,
            SystemPrompt = role.SystemPrompt,
            Provider = role.Provider,
            Model = role.Model,
            Connectors = role.Connectors.ToArray(),
        };

    private string GetExecutionFilePath(string executionId) => Path.Combine(_executionsDirectory, $"{executionId}.json");

    private static string GetLayoutFilePath(string workflowFilePath) => $"{workflowFilePath}.layout.json";

    private static IReadOnlyList<StoredConnectorDefinition> ParseConnectors(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "connectors", out var connectorsNode))
        {
            return [];
        }

        var results = new List<StoredConnectorDefinition>();
        if (connectorsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in connectorsNode.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var connector = ParseConnector(item, null);
                    if (connector is not null)
                    {
                        results.Add(connector);
                    }
                }
            }

            return results;
        }

        if (connectorsNode.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (TryGetPropertyIgnoreCase(connectorsNode, "definitions", out var definitionsNode) &&
            definitionsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in definitionsNode.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var connector = ParseConnector(item, null);
                    if (connector is not null)
                    {
                        results.Add(connector);
                    }
                }
            }

            return results;
        }

        foreach (var property in connectorsNode.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var connector = ParseConnector(property.Value, property.Name);
            if (connector is not null)
            {
                results.Add(connector);
            }
        }

        return results;
    }

    private static IReadOnlyList<StoredRoleDefinition> ParseRoles(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "roles", out var rolesNode))
        {
            return [];
        }

        var results = new List<StoredRoleDefinition>();
        if (rolesNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rolesNode.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var role = ParseRole(item, null);
                    if (role is not null)
                    {
                        results.Add(role);
                    }
                }
            }

            return results;
        }

        if (rolesNode.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (TryGetPropertyIgnoreCase(rolesNode, "definitions", out var definitionsNode) &&
            definitionsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in definitionsNode.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var role = ParseRole(item, null);
                    if (role is not null)
                    {
                        results.Add(role);
                    }
                }
            }

            return results;
        }

        foreach (var property in rolesNode.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var role = ParseRole(property.Value, property.Name);
            if (role is not null)
            {
                results.Add(role);
            }
        }

        return results;
    }

    private static StoredConnectorDefinition? ParseConnector(JsonElement connectorNode, string? fallbackName)
    {
        var name = ReadString(connectorNode, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = fallbackName ?? string.Empty;
        }

        var type = ReadString(connectorNode, "type");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        return new StoredConnectorDefinition(
            Name: name,
            Type: type,
            Enabled: ReadBool(connectorNode, "enabled", true),
            TimeoutMs: Math.Clamp(ReadInt(connectorNode, "timeoutMs", 30_000), 100, 300_000),
            Retry: Math.Clamp(ReadInt(connectorNode, "retry", 0), 0, 5),
            Http: TryGetPropertyIgnoreCase(connectorNode, "http", out var httpNode) ? ParseHttpConfig(httpNode) : EmptyHttpConfig(),
            Cli: TryGetPropertyIgnoreCase(connectorNode, "cli", out var cliNode) ? ParseCliConfig(cliNode) : EmptyCliConfig(),
            Mcp: TryGetPropertyIgnoreCase(connectorNode, "mcp", out var mcpNode) ? ParseMcpConfig(mcpNode) : EmptyMcpConfig());
    }

    private static StoredRoleDefinition? ParseRole(JsonElement roleNode, string? fallbackId)
    {
        var id = ReadString(roleNode, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = fallbackId ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var name = ReadString(roleNode, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = id;
        }

        return new StoredRoleDefinition(
            Id: id,
            Name: name,
            SystemPrompt: ReadString(roleNode, "systemPrompt", "system_prompt"),
            Provider: ReadString(roleNode, "provider"),
            Model: ReadString(roleNode, "model"),
            Connectors: ReadStringArray(roleNode, "connectors"));
    }

    private static StoredHttpConnectorConfig ParseHttpConfig(JsonElement node) =>
        node.ValueKind != JsonValueKind.Object
            ? EmptyHttpConfig()
            : new StoredHttpConnectorConfig(
                BaseUrl: ReadString(node, "baseUrl"),
                AllowedMethods: ReadStringArray(node, "allowedMethods"),
                AllowedPaths: ReadStringArray(node, "allowedPaths"),
                AllowedInputKeys: ReadStringArray(node, "allowedInputKeys"),
                DefaultHeaders: ReadStringMap(node, "defaultHeaders"));

    private static StoredCliConnectorConfig ParseCliConfig(JsonElement node) =>
        node.ValueKind != JsonValueKind.Object
            ? EmptyCliConfig()
            : new StoredCliConnectorConfig(
                Command: ReadString(node, "command"),
                FixedArguments: ReadStringArray(node, "fixedArguments"),
                AllowedOperations: ReadStringArray(node, "allowedOperations"),
                AllowedInputKeys: ReadStringArray(node, "allowedInputKeys"),
                WorkingDirectory: ReadString(node, "workingDirectory"),
                Environment: ReadStringMap(node, "environment"));

    private static StoredMcpConnectorConfig ParseMcpConfig(JsonElement node) =>
        node.ValueKind != JsonValueKind.Object
            ? EmptyMcpConfig()
            : new StoredMcpConnectorConfig(
                ServerName: ReadString(node, "serverName"),
                Command: ReadString(node, "command"),
                Arguments: ReadStringArray(node, "arguments"),
                Environment: ReadStringMap(node, "environment"),
                DefaultTool: ReadString(node, "defaultTool"),
                AllowedTools: ReadStringArray(node, "allowedTools"),
                AllowedInputKeys: ReadStringArray(node, "allowedInputKeys"));

    private static StoredHttpConnectorConfig EmptyHttpConfig() =>
        new(string.Empty, [], [], [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static StoredCliConnectorConfig EmptyCliConfig() =>
        new(string.Empty, [], [], [], string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static StoredMcpConnectorConfig EmptyMcpConfig() =>
        new(string.Empty, string.Empty, [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), string.Empty, [], []);

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string ReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool ReadBool(JsonElement element, string propertyName, bool fallback)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)
            ? parsed
            : fallback;
    }

    private static int ReadInt(JsonElement element, string propertyName, int fallback)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numberValue))
        {
            return numberValue;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out numberValue)
            ? numberValue
            : fallback;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.ToString();
        }

        return result;
    }

    private static string CreateStableId(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string NormalizeAppearanceTheme(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "coral" => "coral",
            "forest" => "forest",
            _ => "blue",
        };
    }

    private static string NormalizeColorMode(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "dark" => "dark",
            _ => "light",
        };
    }

    private static string ResolveAevatarHomeDirectory()
    {
        var envPath = Environment.GetEnvironmentVariable("AEVATAR_HOME");
        var rawPath = string.IsNullOrWhiteSpace(envPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aevatar")
            : envPath.Trim();

        return ExpandPath(rawPath);
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

    private sealed class PersistedWorkspaceSettings
    {
        public string RuntimeBaseUrl { get; set; } = "http://127.0.0.1:5100";

        public string AppearanceTheme { get; set; } = "blue";

        public string ColorMode { get; set; } = "light";

        public List<PersistedWorkspaceDirectory> Directories { get; set; } = [];
    }

    private sealed class PersistedWorkspaceDirectory
    {
        public string Label { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;
    }

    private sealed class ConnectorJsonDocument
    {
        [JsonPropertyName("connectors")]
        public List<ConnectorJsonEntry> Connectors { get; set; } = [];
    }

    private sealed class RoleJsonDocument
    {
        [JsonPropertyName("roles")]
        public List<RoleJsonEntry> Roles { get; set; } = [];
    }

    private sealed class ConnectorDraftJsonDocument
    {
        [JsonPropertyName("updatedAtUtc")]
        public DateTimeOffset UpdatedAtUtc { get; set; }

        [JsonPropertyName("connector")]
        public ConnectorJsonEntry Connector { get; set; } = new();
    }

    private sealed class RoleDraftJsonDocument
    {
        [JsonPropertyName("updatedAtUtc")]
        public DateTimeOffset UpdatedAtUtc { get; set; }

        [JsonPropertyName("role")]
        public RoleJsonEntry Role { get; set; } = new();
    }

    private sealed class ConnectorJsonEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("timeoutMs")]
        public int TimeoutMs { get; set; } = 30_000;

        [JsonPropertyName("retry")]
        public int Retry { get; set; }

        [JsonPropertyName("http")]
        public HttpConnectorJsonConfig Http { get; set; } = new();

        [JsonPropertyName("cli")]
        public CliConnectorJsonConfig Cli { get; set; } = new();

        [JsonPropertyName("mcp")]
        public McpConnectorJsonConfig Mcp { get; set; } = new();
    }

    private sealed class RoleJsonEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("systemPrompt")]
        public string SystemPrompt { get; set; } = string.Empty;

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("connectors")]
        public string[] Connectors { get; set; } = [];
    }

    private sealed class HttpConnectorJsonConfig
    {
        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; set; } = string.Empty;

        [JsonPropertyName("allowedMethods")]
        public string[] AllowedMethods { get; set; } = [];

        [JsonPropertyName("allowedPaths")]
        public string[] AllowedPaths { get; set; } = [];

        [JsonPropertyName("allowedInputKeys")]
        public string[] AllowedInputKeys { get; set; } = [];

        [JsonPropertyName("defaultHeaders")]
        public Dictionary<string, string> DefaultHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CliConnectorJsonConfig
    {
        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;

        [JsonPropertyName("fixedArguments")]
        public string[] FixedArguments { get; set; } = [];

        [JsonPropertyName("allowedOperations")]
        public string[] AllowedOperations { get; set; } = [];

        [JsonPropertyName("allowedInputKeys")]
        public string[] AllowedInputKeys { get; set; } = [];

        [JsonPropertyName("workingDirectory")]
        public string WorkingDirectory { get; set; } = string.Empty;

        [JsonPropertyName("environment")]
        public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class McpConnectorJsonConfig
    {
        [JsonPropertyName("serverName")]
        public string ServerName { get; set; } = string.Empty;

        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public string[] Arguments { get; set; } = [];

        [JsonPropertyName("environment")]
        public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("defaultTool")]
        public string DefaultTool { get; set; } = string.Empty;

        [JsonPropertyName("allowedTools")]
        public string[] AllowedTools { get; set; } = [];

        [JsonPropertyName("allowedInputKeys")]
        public string[] AllowedInputKeys { get; set; } = [];
    }
}
