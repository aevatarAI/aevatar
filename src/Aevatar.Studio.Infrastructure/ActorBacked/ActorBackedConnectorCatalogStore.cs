using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IConnectorCatalogStore"/>.
/// Completely stateless: no fields hold snapshot or subscription state.
/// Reads use per-request temporary subscription to the ReadModel GAgent.
/// Writes send commands to the Write GAgent.
/// Local workspace operations (import, draft backup) delegate to <see cref="IStudioWorkspaceStore"/>.
/// Per-scope isolation: each scope gets its own <c>connector-catalog-{scopeId}</c> actor.
/// </summary>
internal sealed class ActorBackedConnectorCatalogStore : IConnectorCatalogStore
{
    private const string WriteActorIdPrefix = "connector-catalog-";
    private const string ActorHomeDirectory = "actor://connector-catalog";
    private const string ActorFilePath = "actor://connector-catalog/connectors";

    private readonly IActorRuntime _runtime;
    private readonly IActorEventSubscriptionProvider _subscriptions;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly IStudioWorkspaceStore _workspaceStore;
    private readonly ILogger<ActorBackedConnectorCatalogStore> _logger;

    public ActorBackedConnectorCatalogStore(
        IActorRuntime runtime,
        IActorEventSubscriptionProvider subscriptions,
        IAppScopeResolver scopeResolver,
        IStudioWorkspaceStore workspaceStore,
        ILogger<ActorBackedConnectorCatalogStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StoredConnectorCatalog> GetConnectorCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await ReadFromReadModelAsync(cancellationToken);
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
        await SendCommandAsync(actor, evt, cancellationToken);

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
        await SendCommandAsync(actor, evt, cancellationToken);

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
        var state = await ReadFromReadModelAsync(cancellationToken);
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
        await SendCommandAsync(actor, evt, cancellationToken);

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
        await SendCommandAsync(actor, new ConnectorDraftDeletedEvent(), cancellationToken);

        await _workspaceStore.DeleteConnectorDraftAsync(cancellationToken);
    }

    // ── Per-request readmodel read (no service-level state) ──

    private async Task<ConnectorCatalogState?> ReadFromReadModelAsync(CancellationToken ct)
    {
        var readModelActorId = ResolveReadModelActorId();
        var tcs = new TaskCompletionSource<ConnectorCatalogState?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await _subscriptions.SubscribeAsync<EventEnvelope>(
            readModelActorId,
            envelope =>
            {
                if (envelope.Payload?.Is(ConnectorCatalogStateSnapshotEvent.Descriptor) == true)
                {
                    var snapshot = envelope.Payload.Unpack<ConnectorCatalogStateSnapshotEvent>();
                    tcs.TrySetResult(snapshot.Snapshot);
                }
                return Task.CompletedTask;
            },
            ct);

        // Activate readmodel actor (triggers OnActivateAsync -> PublishAsync snapshot)
        await EnsureReadModelActorAsync(readModelActorId, ct);

        // Wait for snapshot with timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Timeout waiting for readmodel snapshot from {ActorId}", readModelActorId);
            return null;
        }
    }

    // ── Actor resolution ──

    private string ResolveScopeId()
    {
        var scope = _scopeResolver.Resolve();
        return scope?.ScopeId ?? "default";
    }

    private string ResolveWriteActorId() => WriteActorIdPrefix + ResolveScopeId();
    private string ResolveReadModelActorId() => ResolveWriteActorId() + "-readmodel";

    private async Task<IActor> EnsureWriteActorAsync(CancellationToken ct)
    {
        var actorId = ResolveWriteActorId();
        var actor = await _runtime.GetAsync(actorId);
        return actor ?? await _runtime.CreateAsync<ConnectorCatalogGAgent>(actorId, ct);
    }

    private async Task EnsureReadModelActorAsync(string readModelActorId, CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(readModelActorId);
        if (actor is null)
            await _runtime.CreateAsync<ConnectorCatalogReadModelGAgent>(readModelActorId, ct);
    }

    private static async Task SendCommandAsync(IActor actor, IMessage command, CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };
        await actor.HandleEventAsync(envelope, ct);
    }

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
