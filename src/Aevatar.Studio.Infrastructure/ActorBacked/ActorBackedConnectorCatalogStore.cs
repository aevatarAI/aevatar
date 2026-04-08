using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.Storage;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IConnectorCatalogStore"/>.
/// Remote catalog persistence goes through <see cref="ConnectorCatalogGAgent"/>.
/// Local workspace operations (import, draft) delegate to <see cref="IStudioWorkspaceStore"/>.
/// </summary>
internal sealed class ActorBackedConnectorCatalogStore : IConnectorCatalogStore, IAsyncDisposable
{
    private const string CatalogActorId = "connector-catalog";

    private readonly IActorRuntime _runtime;
    private readonly IActorEventSubscriptionProvider _subscriptions;
    private readonly IStudioWorkspaceStore _workspaceStore;
    private readonly ILogger<ActorBackedConnectorCatalogStore> _logger;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile ConnectorCatalogState? _snapshot;
    private IAsyncDisposable? _subscription;
    private bool _initialized;

    public ActorBackedConnectorCatalogStore(
        IActorRuntime runtime,
        IActorEventSubscriptionProvider subscriptions,
        IStudioWorkspaceStore workspaceStore,
        ILogger<ActorBackedConnectorCatalogStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StoredConnectorCatalog> GetConnectorCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var state = _snapshot;
        if (state is null || string.IsNullOrEmpty(state.CatalogJson))
        {
            return new StoredConnectorCatalog(
                HomeDirectory: string.Empty,
                FilePath: string.Empty,
                FileExists: false,
                Connectors: []);
        }

        var connectors = await DeserializeCatalogJsonAsync(state.CatalogJson, cancellationToken);
        return new StoredConnectorCatalog(
            HomeDirectory: string.Empty,
            FilePath: string.Empty,
            FileExists: true,
            Connectors: connectors);
    }

    public async Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(
        StoredConnectorCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        var catalogJson = await SerializeCatalogJsonAsync(catalog.Connectors, cancellationToken);
        var actor = await EnsureActorAsync(cancellationToken);
        var evt = new ConnectorCatalogSavedEvent { CatalogJson = catalogJson };
        await SendCommandAsync(actor, evt, cancellationToken);

        return new StoredConnectorCatalog(
            HomeDirectory: string.Empty,
            FilePath: string.Empty,
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

        // Persist the local catalog into the actor
        var saved = await SaveConnectorCatalogAsync(
            new StoredConnectorCatalog(
                HomeDirectory: string.Empty,
                FilePath: string.Empty,
                FileExists: true,
                Connectors: localCatalog.Connectors),
            cancellationToken);

        return new ImportedConnectorCatalog(localCatalog.FilePath, true, saved);
    }

    public Task<StoredConnectorDraft> GetConnectorDraftAsync(
        CancellationToken cancellationToken = default)
    {
        return _workspaceStore.GetConnectorDraftAsync(cancellationToken);
    }

    public Task<StoredConnectorDraft> SaveConnectorDraftAsync(
        StoredConnectorDraft draft,
        CancellationToken cancellationToken = default)
    {
        return _workspaceStore.SaveConnectorDraftAsync(draft, cancellationToken);
    }

    public Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default)
    {
        return _workspaceStore.DeleteConnectorDraftAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;

            // Subscribe to the catalog actor's events to receive state snapshots
            _subscription = await _subscriptions.SubscribeAsync<EventEnvelope>(
                CatalogActorId,
                HandleCatalogEventAsync,
                ct);

            // Activate the actor — triggers event replay + OnActivateAsync
            // which publishes the initial state snapshot
            await EnsureActorAsync(ct);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private Task HandleCatalogEventAsync(EventEnvelope envelope)
    {
        if (envelope.Payload is null)
            return Task.CompletedTask;

        if (envelope.Payload.Is(ConnectorCatalogStateSnapshotEvent.Descriptor))
        {
            var snapshot = envelope.Payload.Unpack<ConnectorCatalogStateSnapshotEvent>();
            _snapshot = snapshot.Snapshot;
            _logger.LogDebug(
                "Connector catalog readmodel updated: catalog has {HasCatalog}, draft has {HasDraft}",
                !string.IsNullOrEmpty(snapshot.Snapshot?.CatalogJson),
                !string.IsNullOrEmpty(snapshot.Snapshot?.DraftJson));
        }

        return Task.CompletedTask;
    }

    private async Task<IActor> EnsureActorAsync(CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(CatalogActorId);
        if (actor is not null)
            return actor;

        return await _runtime.CreateAsync<ConnectorCatalogGAgent>(CatalogActorId, ct);
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

    private static async Task<string> SerializeCatalogJsonAsync(
        IReadOnlyList<StoredConnectorDefinition> connectors,
        CancellationToken ct)
    {
        await using var stream = new MemoryStream();
        await ConnectorCatalogJsonSerializer.WriteCatalogAsync(stream, connectors, ct);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<IReadOnlyList<StoredConnectorDefinition>> DeserializeCatalogJsonAsync(
        string json,
        CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await using var stream = new MemoryStream(bytes, writable: false);
        return await ConnectorCatalogJsonSerializer.ReadCatalogAsync(stream, ct);
    }
}
