using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.ConnectorCatalog;

/// <summary>
/// Singleton actor that owns the connector catalog and draft.
/// Replaces the chrono-storage backed <c>ChronoStorageConnectorCatalogStore</c>
/// for remote persistence concerns.
///
/// Actor ID: <c>connector-catalog</c> (cluster-scoped singleton).
/// </summary>
public sealed class ConnectorCatalogGAgent : GAgentBase<ConnectorCatalogState>
{
    [EventHandler(EndpointName = "saveCatalog")]
    public async Task HandleCatalogSaved(ConnectorCatalogSavedEvent evt)
    {
        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "saveDraft")]
    public async Task HandleDraftSaved(ConnectorDraftSavedEvent evt)
    {
        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "deleteDraft")]
    public async Task HandleDraftDeleted(ConnectorDraftDeletedEvent evt)
    {
        // Idempotent: skip if no draft exists
        if (State.Draft is null)
            return;

        await PersistDomainEventAsync(evt);
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
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
        next.Connectors.Clear();
        next.Connectors.AddRange(evt.Connectors);
        return next;
    }

    private static ConnectorCatalogState ApplyDraftSaved(
        ConnectorCatalogState state, ConnectorDraftSavedEvent evt)
    {
        var next = state.Clone();
        next.Draft = new ConnectorDraftEntry
        {
            Draft = evt.Draft?.Clone(),
            UpdatedAtUtc = evt.UpdatedAtUtc,
        };
        return next;
    }

    private static ConnectorCatalogState ApplyDraftDeleted(
        ConnectorCatalogState state, ConnectorDraftDeletedEvent _)
    {
        var next = state.Clone();
        next.Draft = null;
        return next;
    }

}
