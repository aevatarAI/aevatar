using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.ConnectorCatalog;

/// <summary>
/// Singleton actor that persists the connector catalog and draft.
/// Replaces the chrono-storage backed <c>ChronoStorageConnectorCatalogStore</c>
/// for remote persistence operations.
///
/// Actor ID: <c>connector-catalog</c> (cluster-scoped singleton).
///
/// After each state change, publishes <see cref="ConnectorCatalogStateSnapshotEvent"/>
/// so readmodel subscribers can maintain an up-to-date projection without
/// reading write-model internal state.
/// </summary>
public sealed class ConnectorCatalogGAgent : GAgentBase<ConnectorCatalogState>
{
    [EventHandler(EndpointName = "saveCatalog")]
    public async Task HandleCatalogSaved(ConnectorCatalogSavedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.CatalogJson))
            return;

        await PersistDomainEventAsync(evt);
        await PublishStateSnapshotAsync();
    }

    [EventHandler(EndpointName = "saveDraft")]
    public async Task HandleDraftSaved(ConnectorDraftSavedEvent evt)
    {
        await PersistDomainEventAsync(evt);
        await PublishStateSnapshotAsync();
    }

    [EventHandler(EndpointName = "deleteDraft")]
    public async Task HandleDraftDeleted(ConnectorDraftDeletedEvent evt)
    {
        // Idempotent: skip if no draft exists
        if (string.IsNullOrEmpty(State.DraftJson))
            return;

        await PersistDomainEventAsync(evt);
        await PublishStateSnapshotAsync();
    }

    /// <summary>
    /// On activation (after event replay), publish the current state so
    /// any subscriber that activates the actor can receive the initial snapshot.
    /// </summary>
    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await PublishStateSnapshotAsync();
    }

    protected override ConnectorCatalogState TransitionState(
        ConnectorCatalogState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<ConnectorCatalogSavedEvent>(ApplyCatalogSaved)
            .On<ConnectorDraftSavedEvent>(ApplyDraftSaved)
            .On<ConnectorDraftDeletedEvent>(ApplyDraftDeleted)
            .OrCurrent();
    }

    private static ConnectorCatalogState ApplyCatalogSaved(
        ConnectorCatalogState state, ConnectorCatalogSavedEvent evt)
    {
        var next = state.Clone();
        next.CatalogJson = evt.CatalogJson;
        return next;
    }

    private static ConnectorCatalogState ApplyDraftSaved(
        ConnectorCatalogState state, ConnectorDraftSavedEvent evt)
    {
        var next = state.Clone();
        next.DraftJson = evt.DraftJson;
        next.DraftUpdatedAtUtc = evt.UpdatedAtUtc;
        return next;
    }

    private static ConnectorCatalogState ApplyDraftDeleted(
        ConnectorCatalogState state, ConnectorDraftDeletedEvent _)
    {
        var next = state.Clone();
        next.DraftJson = string.Empty;
        next.DraftUpdatedAtUtc = null;
        return next;
    }

    private async Task PublishStateSnapshotAsync()
    {
        var snapshot = new ConnectorCatalogStateSnapshotEvent { Snapshot = State.Clone() };
        await PublishAsync(snapshot, TopologyAudience.Parent);
    }
}
