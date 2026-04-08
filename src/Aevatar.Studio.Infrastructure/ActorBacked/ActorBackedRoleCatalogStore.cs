using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.RoleCatalog;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IRoleCatalogStore"/>.
/// Writes go through <see cref="RoleCatalogGAgent"/> event handlers.
/// Reads come from a readmodel snapshot maintained via event subscription.
/// Local workspace operations (ImportLocalCatalogAsync) delegate to
/// <see cref="IStudioWorkspaceStore"/>.
/// </summary>
internal sealed class ActorBackedRoleCatalogStore : IRoleCatalogStore, IAsyncDisposable
{
    private const string CatalogActorId = "role-catalog";
    private const string ActorHomeDirectory = "actor://role-catalog";
    private const string ActorFilePath = "actor://role-catalog/roles";

    private readonly IActorRuntime _runtime;
    private readonly IActorEventSubscriptionProvider _subscriptions;
    private readonly IStudioWorkspaceStore _localWorkspaceStore;
    private readonly ILogger<ActorBackedRoleCatalogStore> _logger;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile RoleCatalogState? _snapshot;
    private IAsyncDisposable? _subscription;
    private bool _initialized;

    public ActorBackedRoleCatalogStore(
        IActorRuntime runtime,
        IActorEventSubscriptionProvider subscriptions,
        IStudioWorkspaceStore localWorkspaceStore,
        ILogger<ActorBackedRoleCatalogStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _localWorkspaceStore = localWorkspaceStore ?? throw new ArgumentNullException(nameof(localWorkspaceStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var state = _snapshot;
        var roles = state?.Roles
            .Select(ToStoredRoleDefinition)
            .ToList()
            .AsReadOnly()
            ?? (IReadOnlyList<StoredRoleDefinition>)[];

        return new StoredRoleCatalog(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath,
            FileExists: roles.Count > 0,
            Roles: roles);
    }

    public async Task<StoredRoleCatalog> SaveRoleCatalogAsync(
        StoredRoleCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        var actor = await EnsureActorAsync(cancellationToken);
        var evt = new RoleCatalogSavedEvent();
        evt.Roles.AddRange(catalog.Roles.Select(ToProtoRoleDefinition));
        await SendCommandAsync(actor, evt, cancellationToken);

        return new StoredRoleCatalog(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath,
            FileExists: true,
            Roles: catalog.Roles);
    }

    public async Task<ImportedRoleCatalog> ImportLocalCatalogAsync(CancellationToken cancellationToken = default)
    {
        var localCatalog = await _localWorkspaceStore.GetRoleCatalogAsync(cancellationToken);
        if (!localCatalog.FileExists)
        {
            throw new InvalidOperationException($"Local role catalog not found at '{localCatalog.FilePath}'.");
        }

        var actor = await EnsureActorAsync(cancellationToken);
        var evt = new RoleCatalogSavedEvent();
        evt.Roles.AddRange(localCatalog.Roles.Select(ToProtoRoleDefinition));
        await SendCommandAsync(actor, evt, cancellationToken);

        var importedCatalog = new StoredRoleCatalog(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath,
            FileExists: true,
            Roles: localCatalog.Roles);

        return new ImportedRoleCatalog(localCatalog.FilePath, true, importedCatalog);
    }

    public async Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var state = _snapshot;
        var draftEntry = state?.Draft;
        if (draftEntry is null)
        {
            return new StoredRoleDraft(
                HomeDirectory: ActorHomeDirectory,
                FilePath: ActorFilePath + "/draft",
                FileExists: false,
                UpdatedAtUtc: null,
                Draft: null);
        }

        return new StoredRoleDraft(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath + "/draft",
            FileExists: true,
            UpdatedAtUtc: draftEntry.UpdatedAtUtc?.ToDateTimeOffset(),
            Draft: draftEntry.Draft is not null ? ToStoredRoleDefinition(draftEntry.Draft) : null);
    }

    public async Task<StoredRoleDraft> SaveRoleDraftAsync(
        StoredRoleDraft draft,
        CancellationToken cancellationToken = default)
    {
        var actor = await EnsureActorAsync(cancellationToken);
        var updatedAtUtc = draft.UpdatedAtUtc ?? DateTimeOffset.UtcNow;
        var evt = new RoleDraftSavedEvent
        {
            Draft = draft.Draft is not null ? ToProtoRoleDefinition(draft.Draft) : null,
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(updatedAtUtc),
        };
        await SendCommandAsync(actor, evt, cancellationToken);

        return new StoredRoleDraft(
            HomeDirectory: ActorHomeDirectory,
            FilePath: ActorFilePath + "/draft",
            FileExists: true,
            UpdatedAtUtc: updatedAtUtc,
            Draft: draft.Draft);
    }

    public async Task DeleteRoleDraftAsync(CancellationToken cancellationToken = default)
    {
        var actor = await EnsureActorAsync(cancellationToken);
        await SendCommandAsync(actor, new RoleDraftDeletedEvent(), cancellationToken);
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

            // Activate the actor — this triggers event replay + OnActivateAsync
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

        if (envelope.Payload.Is(RoleCatalogStateSnapshotEvent.Descriptor))
        {
            var snapshot = envelope.Payload.Unpack<RoleCatalogStateSnapshotEvent>();
            _snapshot = snapshot.Snapshot;
            _logger.LogDebug("Role catalog readmodel updated: {RoleCount} roles, draft={HasDraft}",
                snapshot.Snapshot?.Roles.Count ?? 0,
                snapshot.Snapshot?.Draft is not null);
        }

        return Task.CompletedTask;
    }

    private async Task<IActor> EnsureActorAsync(CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(CatalogActorId);
        if (actor is not null)
            return actor;

        return await _runtime.CreateAsync<RoleCatalogGAgent>(CatalogActorId, ct);
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

    private static StoredRoleDefinition ToStoredRoleDefinition(RoleDefinitionEntry entry) =>
        new(
            Id: entry.Id,
            Name: entry.Name,
            SystemPrompt: entry.SystemPrompt,
            Provider: entry.Provider,
            Model: entry.Model,
            Connectors: entry.Connectors.ToList().AsReadOnly());

    private static RoleDefinitionEntry ToProtoRoleDefinition(StoredRoleDefinition def)
    {
        var entry = new RoleDefinitionEntry
        {
            Id = def.Id,
            Name = def.Name,
            SystemPrompt = def.SystemPrompt,
            Provider = def.Provider,
            Model = def.Model,
        };
        entry.Connectors.AddRange(def.Connectors);
        return entry;
    }
}
