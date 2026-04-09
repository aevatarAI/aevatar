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
/// Completely stateless: no fields hold snapshot or subscription state.
/// Reads use per-request temporary subscription to the ReadModel GAgent.
/// Writes send commands to the Write GAgent.
/// Local workspace operations (ImportLocalCatalogAsync) delegate to
/// <see cref="IStudioWorkspaceStore"/>.
/// </summary>
internal sealed class ActorBackedRoleCatalogStore : IRoleCatalogStore
{
    private const string WriteActorId = "role-catalog";
    private const string ReadModelActorId = "role-catalog-readmodel";
    private const string ActorHomeDirectory = "actor://role-catalog";
    private const string ActorFilePath = "actor://role-catalog/roles";

    private readonly IActorRuntime _runtime;
    private readonly IActorEventSubscriptionProvider _subscriptions;
    private readonly IStudioWorkspaceStore _localWorkspaceStore;
    private readonly ILogger<ActorBackedRoleCatalogStore> _logger;

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
        var state = await ReadFromReadModelAsync(cancellationToken);
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
        var actor = await EnsureWriteActorAsync(cancellationToken);
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

        var actor = await EnsureWriteActorAsync(cancellationToken);
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
        var state = await ReadFromReadModelAsync(cancellationToken);
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
        var actor = await EnsureWriteActorAsync(cancellationToken);
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
        var actor = await EnsureWriteActorAsync(cancellationToken);
        await SendCommandAsync(actor, new RoleDraftDeletedEvent(), cancellationToken);
    }

    // ── Per-request readmodel read (no service-level state) ──

    private async Task<RoleCatalogState?> ReadFromReadModelAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<RoleCatalogState?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await _subscriptions.SubscribeAsync<EventEnvelope>(
            ReadModelActorId,
            envelope =>
            {
                if (envelope.Payload?.Is(RoleCatalogStateSnapshotEvent.Descriptor) == true)
                {
                    var snapshot = envelope.Payload.Unpack<RoleCatalogStateSnapshotEvent>();
                    tcs.TrySetResult(snapshot.Snapshot);
                }
                return Task.CompletedTask;
            },
            ct);

        // Activate readmodel actor (triggers OnActivateAsync -> PublishAsync snapshot)
        await EnsureReadModelActorAsync(ct);

        // Wait for snapshot with timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Timeout waiting for readmodel snapshot from {ActorId}", ReadModelActorId);
            return null;
        }
    }

    // ── Actor resolution ──

    private async Task<IActor> EnsureWriteActorAsync(CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(WriteActorId);
        return actor ?? await _runtime.CreateAsync<RoleCatalogGAgent>(WriteActorId, ct);
    }

    private async Task EnsureReadModelActorAsync(CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(ReadModelActorId);
        if (actor is null)
            await _runtime.CreateAsync<RoleCatalogReadModelGAgent>(ReadModelActorId, ct);
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
