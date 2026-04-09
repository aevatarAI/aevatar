using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.RoleCatalog;

/// <summary>
/// Persistent readmodel actor for the role catalog.
/// Receives state snapshots from <see cref="RoleCatalogGAgent"/> via
/// <see cref="RoleCatalogReadModelUpdateEvent"/> (SendToAsync) and persists them.
///
/// Actor ID convention: <c>{writeActorId}-readmodel</c>.
///
/// On activation and after each update, publishes
/// <see cref="RoleCatalogStateSnapshotEvent"/> so per-request subscribers
/// (ActorBackedStore) can receive the current projected state.
/// </summary>
public sealed class RoleCatalogReadModelGAgent : GAgentBase<RoleCatalogState>
{
    [EventHandler(EndpointName = "updateReadModel")]
    public async Task HandleReadModelUpdate(RoleCatalogReadModelUpdateEvent evt)
    {
        await PersistDomainEventAsync(evt);
        await PublishSnapshotAsync();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await PublishSnapshotAsync();
    }

    protected override RoleCatalogState TransitionState(
        RoleCatalogState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<RoleCatalogReadModelUpdateEvent>(ApplyUpdate)
            .OrCurrent();
    }

    private static RoleCatalogState ApplyUpdate(
        RoleCatalogState _, RoleCatalogReadModelUpdateEvent evt)
    {
        return evt.Snapshot?.Clone() ?? new RoleCatalogState();
    }

    private async Task PublishSnapshotAsync()
    {
        var snapshot = new RoleCatalogStateSnapshotEvent { Snapshot = State.Clone() };
        await PublishAsync(snapshot, TopologyAudience.Parent);
    }
}
