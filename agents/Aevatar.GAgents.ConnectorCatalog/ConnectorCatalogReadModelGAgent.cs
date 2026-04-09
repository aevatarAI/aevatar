using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.ConnectorCatalog;

/// <summary>
/// Persistent readmodel actor for the connector catalog.
/// Receives state snapshots from <see cref="ConnectorCatalogGAgent"/> via
/// <see cref="ConnectorCatalogReadModelUpdateEvent"/> (SendToAsync) and persists them.
///
/// Actor ID convention: <c>{writeActorId}-readmodel</c>.
///
/// On activation and after each update, publishes
/// <see cref="ConnectorCatalogStateSnapshotEvent"/> so per-request subscribers
/// (ActorBackedStore) can receive the current projected state.
/// </summary>
public sealed class ConnectorCatalogReadModelGAgent : GAgentBase<ConnectorCatalogState>
{
    [EventHandler(EndpointName = "updateReadModel")]
    public async Task HandleReadModelUpdate(ConnectorCatalogReadModelUpdateEvent evt)
    {
        await PersistDomainEventAsync(evt);
        await PublishSnapshotAsync();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await PublishSnapshotAsync();
    }

    protected override ConnectorCatalogState TransitionState(
        ConnectorCatalogState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<ConnectorCatalogReadModelUpdateEvent>(ApplyUpdate)
            .OrCurrent();
    }

    private static ConnectorCatalogState ApplyUpdate(
        ConnectorCatalogState _, ConnectorCatalogReadModelUpdateEvent evt)
    {
        return evt.Snapshot?.Clone() ?? new ConnectorCatalogState();
    }

    private async Task PublishSnapshotAsync()
    {
        var snapshot = new ConnectorCatalogStateSnapshotEvent { Snapshot = State.Clone() };
        await PublishAsync(snapshot, TopologyAudience.Parent);
    }
}
