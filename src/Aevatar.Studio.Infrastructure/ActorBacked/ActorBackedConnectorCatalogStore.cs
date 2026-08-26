using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of connector catalog query and command ports.
/// Reads scope-owned definitions from the projection document store (CQRS read model), then
/// composes immutable Host-owned defaults supplied by the deployment Host.
/// Writes send commands to the Write GAgent through CQRS Core dispatch.
/// Local JSON is only an explicit import boundary, never a draft backup.
/// Per-scope isolation: each scope gets its own <c>connector-catalog-{scopeId}</c> actor.
/// </summary>
internal sealed class ActorBackedConnectorCatalogStore : IConnectorCatalogQueryPort, IConnectorCatalogCommandPort
{
    private const string WriteActorIdPrefix = "connector-catalog-";
    private const string ActorHomeDirectory = "actor://connector-catalog";
    private const string ActorFilePath = "actor://connector-catalog/connectors";
    private const string PublisherId = "aevatar.studio.infrastructure.connector-catalog";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioActorCommandDispatch _commandDispatch;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly IStudioLocalConnectorCatalogImportReader _localImportReader;
    private readonly IProjectionDocumentReader<ConnectorCatalogCurrentStateDocument, string> _documentReader;
    private readonly ILogger<ActorBackedConnectorCatalogStore> _logger;
    private readonly IConnectorCatalogNameAuthority _connectorCatalogNameAuthority;

    public ActorBackedConnectorCatalogStore(
        IStudioActorBootstrap bootstrap,
        StudioActorCommandDispatch commandDispatch,
        IAppScopeResolver scopeResolver,
        IStudioLocalConnectorCatalogImportReader localImportReader,
        IProjectionDocumentReader<ConnectorCatalogCurrentStateDocument, string> documentReader,
        IConnectorCatalogNameAuthority connectorCatalogNameAuthority,
        ILogger<ActorBackedConnectorCatalogStore> logger)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _localImportReader = localImportReader ?? throw new ArgumentNullException(nameof(localImportReader));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _connectorCatalogNameAuthority = connectorCatalogNameAuthority ??
            throw new ArgumentNullException(nameof(connectorCatalogNameAuthority));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StoredConnectorCatalog> GetConnectorCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await ReadProjectedStateAsync(cancellationToken);
        var version = state?.LastAppliedEventVersion ?? 0;
        var scopedConnectors = state?.Connectors
            .Select(ToStoredConnectorDefinition)
            .ToList() ?? [];

        // Implement (issue #3542):
        //   Behavior: Publish deployment-owned connector defaults in every scope while preserving scope-owned entries.
        //   Why this shape: Mainnet capabilities remain discoverable without query-time writes or a process-local scope registry.
        // Fix (review round 1, F1):
        //   GET was the only reader that composed Host-owned connector names.
        //   Delegate composition to the catalog-name authority shared with scheduled evidence.
        var connectors = _connectorCatalogNameAuthority.ComposeDefinitions(scopedConnectors);

        return new StoredConnectorCatalog(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath,
            FileExists: connectors.Count > 0,
            Connectors: connectors,
            Version: version);
    }

    public async Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(
        StoredConnectorCatalog catalog,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        // Fix (review round 1, F2):
        //   GET+PUT could persist Host-owned defaults and PUT returned a different catalog view.
        //   Persist only scope-owned entries, then return the same composed view exposed by GET.
        var scopedConnectors = _connectorCatalogNameAuthority.SelectScopeOwnedDefinitions(catalog.Connectors);
        var actor = await EnsureWriteActorAsync(cancellationToken);
        var evt = new ConnectorCatalogSavedEvent();
        evt.Connectors.AddRange(scopedConnectors.Select(ToProtoConnectorDefinition));
        if (expectedVersion is not null)
            evt.ExpectedVersion = expectedVersion.Value;
        await _commandDispatch.DispatchAsync(actor, evt, PublisherId, cancellationToken);

        return new StoredConnectorCatalog(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath,
            FileExists: true,
            Connectors: _connectorCatalogNameAuthority.ComposeDefinitions(scopedConnectors),
            Version: NextDeterministicVersion(expectedVersion));
    }

    public async Task<ImportedConnectorCatalog> ImportLocalCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var localCatalog = await _localImportReader.ReadAsync(cancellationToken);
        if (!localCatalog.FileExists)
        {
            throw new InvalidOperationException(
                $"Local connector catalog not found at '{localCatalog.FilePath}'.");
        }

        var actor = await EnsureWriteActorAsync(cancellationToken);
        var evt = new ConnectorCatalogSavedEvent();
        evt.Connectors.AddRange(localCatalog.Connectors.Select(ToProtoConnectorDefinition));
        await _commandDispatch.DispatchAsync(actor, evt, PublisherId, cancellationToken);

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
        var version = state?.LastAppliedEventVersion ?? 0;
        if (draftEntry is null)
        {
            return new StoredConnectorDraft(
                HomeDirectory: ActorHomeDirectory,
                FilePath: ActorFilePath + "/draft",
                FileExists: false,
                UpdatedAtUtc: null,
                Draft: null,
                Version: version);
        }

        return new StoredConnectorDraft(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath + "/draft",
            FileExists: true,
            UpdatedAtUtc: draftEntry.UpdatedAtUtc?.ToDateTimeOffset(),
            Draft: draftEntry.Draft is not null ? ToStoredConnectorDefinition(draftEntry.Draft) : null,
            Version: version);
    }

    public async Task<StoredConnectorDraft> SaveConnectorDraftAsync(
        StoredConnectorDraft draft,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        var updatedAtUtc = draft.UpdatedAtUtc ?? DateTimeOffset.UtcNow;
        var evt = new ConnectorDraftSavedEvent
        {
            Draft = draft.Draft is not null ? ToProtoConnectorDefinition(draft.Draft) : null,
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(updatedAtUtc),
        };
        if (expectedVersion is not null)
            evt.ExpectedVersion = expectedVersion.Value;
        await _commandDispatch.DispatchAsync(actor, evt, PublisherId, cancellationToken);

        return new StoredConnectorDraft(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath + "/draft",
            FileExists: true,
            UpdatedAtUtc: updatedAtUtc,
            Draft: draft.Draft,
            Version: NextDeterministicVersion(expectedVersion));
    }

    public async Task DeleteConnectorDraftAsync(
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        var evt = new ConnectorDraftDeletedEvent();
        if (expectedVersion is not null)
            evt.ExpectedVersion = expectedVersion.Value;
        await _commandDispatch.DispatchAsync(actor, evt, PublisherId, cancellationToken);

    }

    // Post-write version is deterministic only when caller supplied expected_version
    // (actor enforces match → Apply increments by exactly one). Without expected_version
    // the projection is eventually consistent and may still report the pre-write value,
    // so we return 0 to signal "unknown — re-GET for authoritative version".
    private static long NextDeterministicVersion(long? expectedVersion) =>
        expectedVersion is null ? 0 : expectedVersion.Value + 1;

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
            Mcp: entry.Mcp is not null ? ToStoredMcpConfig(entry.Mcp) : EmptyMcpConfig(),
            HostCallback: entry.HostCallback is not null
                ? ToStoredHostCallbackConfig(entry.HostCallback)
                : EmptyHostCallbackConfig());

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

    private static StoredHostCallbackConnectorConfig ToStoredHostCallbackConfig(
        HostCallbackConnectorConfigEntry entry) =>
        new(
            Handler: entry.Handler,
            AllowedOperations: entry.AllowedOperations.ToList().AsReadOnly(),
            AllowedInputKeys: entry.AllowedInputKeys.ToList().AsReadOnly());

    private static StoredConnectorAuthConfig ToStoredAuthConfig(ConnectorAuthEntry entry) =>
        new(
            Type: entry.Type,
            TokenUrl: entry.TokenUrl,
            ClientId: entry.ClientId,
            ClientSecret: entry.ClientSecret,
            Scope: entry.Scope,
            SecretRef: entry.SecretRef,
            HeaderName: entry.HeaderName,
            HeaderValuePrefix: entry.HeaderValuePrefix);

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
            HostCallback = ToProtoHostCallbackConfig(def.HostCallback),
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

    private static HostCallbackConnectorConfigEntry ToProtoHostCallbackConfig(
        StoredHostCallbackConnectorConfig config)
    {
        var entry = new HostCallbackConnectorConfigEntry
        {
            Handler = config.Handler,
        };
        entry.AllowedOperations.AddRange(config.AllowedOperations);
        entry.AllowedInputKeys.AddRange(config.AllowedInputKeys);
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
            SecretRef = config.SecretRef,
            HeaderName = config.HeaderName,
            HeaderValuePrefix = config.HeaderValuePrefix,
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

    private static StoredHostCallbackConnectorConfig EmptyHostCallbackConfig() =>
        new(string.Empty, [], []);

    private static StoredConnectorAuthConfig EmptyAuthConfig() =>
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
}
