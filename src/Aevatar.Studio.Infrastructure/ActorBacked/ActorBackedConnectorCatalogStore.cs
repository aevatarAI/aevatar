using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IConnectorCatalogStore"/>.
/// Reads from the projection document store (CQRS read model).
/// Writes send commands to the Write GAgent.
/// Local workspace operations (import, draft backup) delegate to <see cref="IStudioWorkspaceStore"/>.
/// Per-scope isolation: each scope gets its own <c>connector-catalog-{scopeId}</c> actor.
/// </summary>
internal sealed class ActorBackedConnectorCatalogStore : IConnectorCatalogStore
{
    private const string WriteActorIdPrefix = "connector-catalog-";
    private const string ActorHomeDirectory = "actor://connector-catalog";
    private const string ActorFilePath = "actor://connector-catalog/connectors";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly IStudioWorkspaceStore _workspaceStore;
    private readonly IProjectionDocumentReader<ConnectorCatalogCurrentStateDocument, string> _documentReader;
    private readonly ILogger<ActorBackedConnectorCatalogStore> _logger;

    public ActorBackedConnectorCatalogStore(
        IStudioActorBootstrap bootstrap,
        IActorDispatchPort dispatchPort,
        IAppScopeResolver scopeResolver,
        IStudioWorkspaceStore workspaceStore,
        IProjectionDocumentReader<ConnectorCatalogCurrentStateDocument, string> documentReader,
        ILogger<ActorBackedConnectorCatalogStore> logger)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StoredConnectorCatalog> GetConnectorCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await ReadProjectedStateAsync(cancellationToken);
        if (state is null)
        {
            return new StoredConnectorCatalog(
                HomeDirectory: ActorHomeDirectory,
                FilePath: ActorFilePath,
                FileExists: false,
                Connectors: []);
        }

        var connectors = state.Connectors
            .Select(ToStoredConnectorDefinition)
            .ToList()
            .AsReadOnly();

        return new StoredConnectorCatalog(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath,
            FileExists: connectors.Count > 0,
            Connectors: connectors);
    }

    public async Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(
        StoredConnectorCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        var evt = new ConnectorCatalogSavedEvent();
        evt.Connectors.AddRange(catalog.Connectors.Select(ToProtoConnectorDefinition));
        await ActorCommandDispatcher.SendAsync(_dispatchPort, actor, evt, cancellationToken);

        return new StoredConnectorCatalog(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath,
            FileExists: true,
            Connectors: catalog.Connectors);
    }

    public async Task<ImportedConnectorCatalog> ImportLocalCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var localCatalog = await _workspaceStore.GetConnectorCatalogAsync(cancellationToken);
        if (!localCatalog.FileExists)
        {
            throw new InvalidOperationException(
                $"Local connector catalog not found at '{localCatalog.FilePath}'.");
        }

        var actor = await EnsureWriteActorAsync(cancellationToken);
        var evt = new ConnectorCatalogSavedEvent();
        evt.Connectors.AddRange(localCatalog.Connectors.Select(ToProtoConnectorDefinition));
        await ActorCommandDispatcher.SendAsync(_dispatchPort, actor, evt, cancellationToken);

        var importedCatalog = new StoredConnectorCatalog(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath,
            FileExists: true,
            Connectors: localCatalog.Connectors);

        return new ImportedConnectorCatalog(localCatalog.FilePath, true, importedCatalog);
    }

    public async Task<StoredConnectorDraft> GetConnectorDraftAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await ReadProjectedStateAsync(cancellationToken);
        var draftEntry = state?.Draft;
        if (draftEntry is null)
        {
            return new StoredConnectorDraft(
                HomeDirectory: ActorHomeDirectory,
                FilePath: ActorFilePath + "/draft",
                FileExists: false,
                UpdatedAtUtc: null,
                Draft: null);
        }

        return new StoredConnectorDraft(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath + "/draft",
            FileExists: true,
            UpdatedAtUtc: draftEntry.UpdatedAtUtc?.ToDateTimeOffset(),
            Draft: draftEntry.Draft is not null ? ToStoredConnectorDefinition(draftEntry.Draft) : null);
    }

    public async Task<StoredConnectorDraft> SaveConnectorDraftAsync(
        StoredConnectorDraft draft,
        CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        var updatedAtUtc = draft.UpdatedAtUtc ?? DateTimeOffset.UtcNow;
        var evt = new ConnectorDraftSavedEvent
        {
            Draft = draft.Draft is not null ? ToProtoConnectorDefinition(draft.Draft) : null,
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(updatedAtUtc),
        };
        await ActorCommandDispatcher.SendAsync(_dispatchPort, actor, evt, cancellationToken);

        // Also persist to local workspace for offline access
        await _workspaceStore.SaveConnectorDraftAsync(draft, cancellationToken);

        return new StoredConnectorDraft(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath + "/draft",
            FileExists: true,
            UpdatedAtUtc: updatedAtUtc,
            Draft: draft.Draft);
    }

    public async Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        await ActorCommandDispatcher.SendAsync(_dispatchPort, actor, new ConnectorDraftDeletedEvent(), cancellationToken);

        await _workspaceStore.DeleteConnectorDraftAsync(cancellationToken);
    }

    // ── Read from projection ──

    private async Task<ConnectorCatalogState?> ReadProjectedStateAsync(CancellationToken ct)
    {
        var actorId = ResolveWriteActorId();
        var document = await _documentReader.GetAsync(actorId, ct);
        if (document?.StateRoot == null ||
            !document.StateRoot.Is(ConnectorCatalogState.Descriptor))
            return null;

        return document.StateRoot.Unpack<ConnectorCatalogState>();
    }

    // ── Actor resolution ──

    private string ResolveWriteActorId() => WriteActorIdPrefix + _scopeResolver.ResolveScopeIdOrDefault();

    private Task<IActor> EnsureWriteActorAsync(CancellationToken ct) =>
        _bootstrap.EnsureAsync<ConnectorCatalogGAgent>(ResolveWriteActorId(), ct);

    // ── Proto <-> Domain mapping ──

    private static StoredConnectorDefinition ToStoredConnectorDefinition(ConnectorDefinitionEntry entry) =>
        new(
            Name: entry.Name,
            Type: entry.Type,
            Enabled: entry.Enabled,
            TimeoutMs: entry.TimeoutMs,
            Retry: entry.Retry,
            Http: entry.Http is not null ? ToStoredHttpConfig(entry.Http) : EmptyHttpConfig(),
            Cli: entry.Cli is not null ? ToStoredCliConfig(entry.Cli) : EmptyCliConfig(),
            Mcp: entry.Mcp is not null ? ToStoredMcpConfig(entry.Mcp) : EmptyMcpConfig());

    private static StoredHttpConnectorConfig ToStoredHttpConfig(HttpConnectorConfigEntry entry) =>
        new(
            BaseUrl: entry.BaseUrl,
            AllowedMethods: entry.AllowedMethods.ToList().AsReadOnly(),
            AllowedPaths: entry.AllowedPaths.ToList().AsReadOnly(),
            AllowedInputKeys: entry.AllowedInputKeys.ToList().AsReadOnly(),
            DefaultHeaders: new Dictionary<string, string>(entry.DefaultHeaders, StringComparer.OrdinalIgnoreCase),
            Auth: entry.Auth is not null ? ToStoredAuthConfig(entry.Auth) : EmptyAuthConfig());

    private static StoredCliConnectorConfig ToStoredCliConfig(CliConnectorConfigEntry entry) =>
        new(
            Command: entry.Command,
            FixedArguments: entry.FixedArguments.ToList().AsReadOnly(),
            AllowedOperations: entry.AllowedOperations.ToList().AsReadOnly(),
            AllowedInputKeys: entry.AllowedInputKeys.ToList().AsReadOnly(),
            WorkingDirectory: entry.WorkingDirectory,
            Environment: new Dictionary<string, string>(entry.Environment, StringComparer.OrdinalIgnoreCase));

    private static StoredMcpConnectorConfig ToStoredMcpConfig(McpConnectorConfigEntry entry) =>
        new(
            ServerName: entry.ServerName,
            Command: entry.Command,
            Url: entry.Url,
            Arguments: entry.Arguments.ToList().AsReadOnly(),
            Environment: new Dictionary<string, string>(entry.Environment, StringComparer.OrdinalIgnoreCase),
            AdditionalHeaders: new Dictionary<string, string>(entry.AdditionalHeaders, StringComparer.OrdinalIgnoreCase),
            Auth: entry.Auth is not null ? ToStoredAuthConfig(entry.Auth) : EmptyAuthConfig(),
            DefaultTool: entry.DefaultTool,
            AllowedTools: entry.AllowedTools.ToList().AsReadOnly(),
            AllowedInputKeys: entry.AllowedInputKeys.ToList().AsReadOnly());

    private static StoredConnectorAuthConfig ToStoredAuthConfig(ConnectorAuthEntry entry) =>
        new(
            Type: entry.Type,
            TokenUrl: entry.TokenUrl,
            ClientId: entry.ClientId,
            ClientSecret: entry.ClientSecret,
            Scope: entry.Scope);

    private static ConnectorDefinitionEntry ToProtoConnectorDefinition(StoredConnectorDefinition def)
    {
        var entry = new ConnectorDefinitionEntry
        {
            Name = def.Name,
            Type = def.Type,
            Enabled = def.Enabled,
            TimeoutMs = def.TimeoutMs,
            Retry = def.Retry,
            Http = ToProtoHttpConfig(def.Http),
            Cli = ToProtoCliConfig(def.Cli),
            Mcp = ToProtoMcpConfig(def.Mcp),
        };
        return entry;
    }

    private static HttpConnectorConfigEntry ToProtoHttpConfig(StoredHttpConnectorConfig config)
    {
        var entry = new HttpConnectorConfigEntry
        {
            BaseUrl = config.BaseUrl,
            Auth = ToProtoAuthConfig(config.Auth),
        };
        entry.AllowedMethods.AddRange(config.AllowedMethods);
        entry.AllowedPaths.AddRange(config.AllowedPaths);
        entry.AllowedInputKeys.AddRange(config.AllowedInputKeys);
        foreach (var kvp in config.DefaultHeaders)
            entry.DefaultHeaders[kvp.Key] = kvp.Value;
        return entry;
    }

    private static CliConnectorConfigEntry ToProtoCliConfig(StoredCliConnectorConfig config)
    {
        var entry = new CliConnectorConfigEntry
        {
            Command = config.Command,
            WorkingDirectory = config.WorkingDirectory,
        };
        entry.FixedArguments.AddRange(config.FixedArguments);
        entry.AllowedOperations.AddRange(config.AllowedOperations);
        entry.AllowedInputKeys.AddRange(config.AllowedInputKeys);
        foreach (var kvp in config.Environment)
            entry.Environment[kvp.Key] = kvp.Value;
        return entry;
    }

    private static McpConnectorConfigEntry ToProtoMcpConfig(StoredMcpConnectorConfig config)
    {
        var entry = new McpConnectorConfigEntry
        {
            ServerName = config.ServerName,
            Command = config.Command,
            Url = config.Url,
            Auth = ToProtoAuthConfig(config.Auth),
            DefaultTool = config.DefaultTool,
        };
        entry.Arguments.AddRange(config.Arguments);
        entry.AllowedTools.AddRange(config.AllowedTools);
        entry.AllowedInputKeys.AddRange(config.AllowedInputKeys);
        foreach (var kvp in config.Environment)
            entry.Environment[kvp.Key] = kvp.Value;
        foreach (var kvp in config.AdditionalHeaders)
            entry.AdditionalHeaders[kvp.Key] = kvp.Value;
        return entry;
    }

    private static ConnectorAuthEntry ToProtoAuthConfig(StoredConnectorAuthConfig config) =>
        new()
        {
            Type = config.Type,
            TokenUrl = config.TokenUrl,
            ClientId = config.ClientId,
            ClientSecret = config.ClientSecret,
            Scope = config.Scope,
        };

    private static StoredHttpConnectorConfig EmptyHttpConfig() =>
        new(string.Empty, [], [], [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), EmptyAuthConfig());

    private static StoredCliConnectorConfig EmptyCliConfig() =>
        new(string.Empty, [], [], [], string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static StoredMcpConnectorConfig EmptyMcpConfig() =>
        new(string.Empty, string.Empty, string.Empty, [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            EmptyAuthConfig(), string.Empty, [], []);

    private static StoredConnectorAuthConfig EmptyAuthConfig() =>
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
}
