using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Persistence;
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
public sealed class ConnectorCatalogGAgent : GAgentBase<ConnectorCatalogState>, IProjectedActor
{
    public static string ProjectionKind => "connector-catalog";


    [EventHandler(EndpointName = "saveCatalog")]
    public async Task HandleCatalogSaved(ConnectorCatalogSavedEvent evt)
    {
        EnsureExpectedVersionMatches(evt.HasExpectedVersion, evt.ExpectedVersion);
        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "saveDraft")]
    public async Task HandleDraftSaved(ConnectorDraftSavedEvent evt)
    {
        EnsureExpectedVersionMatches(evt.HasExpectedVersion, evt.ExpectedVersion);
        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "deleteDraft")]
    public async Task HandleDraftDeleted(ConnectorDraftDeletedEvent evt)
    {
        EnsureExpectedVersionMatches(evt.HasExpectedVersion, evt.ExpectedVersion);

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

    private void EnsureExpectedVersionMatches(bool hasExpectedVersion, long expectedVersion)
    {
        if (!hasExpectedVersion)
            return;

        if (expectedVersion != State.LastAppliedEventVersion)
        {
            throw new EventStoreOptimisticConcurrencyException(
                Id,
                expectedVersion,
                State.LastAppliedEventVersion);
        }
    }

    private static ConnectorCatalogState ApplyCatalogSaved(
        ConnectorCatalogState state, ConnectorCatalogSavedEvent evt)
    {
        var next = state.Clone();
        next.Connectors.Clear();
        next.Connectors.AddRange(evt.Connectors);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
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
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        return next;
    }

    private static ConnectorCatalogState ApplyDraftDeleted(
        ConnectorCatalogState state, ConnectorDraftDeletedEvent _)
    {
        var next = state.Clone();
        next.Draft = null;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        return next;
    }

}
