using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.Registry;

/// <summary>
/// Persistent readmodel actor for the GAgent registry.
/// Receives state snapshots from <see cref="GAgentRegistryGAgent"/> via
/// <see cref="GAgentRegistryReadModelUpdateEvent"/> (SendToAsync) and persists them.
///
/// Actor ID convention: <c>{writeActorId}-readmodel</c>.
///
/// On activation and after each update, publishes
/// <see cref="GAgentRegistryStateSnapshotEvent"/> so per-request subscribers
/// (ActorBackedStore) can receive the current projected state.
/// </summary>
public sealed class GAgentRegistryReadModelGAgent : GAgentBase<GAgentRegistryState>
{
    [EventHandler(EndpointName = "updateReadModel")]
    public async Task HandleReadModelUpdate(GAgentRegistryReadModelUpdateEvent evt)
    {
        await PersistDomainEventAsync(evt);
        await PublishSnapshotAsync();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await PublishSnapshotAsync();
    }

    protected override GAgentRegistryState TransitionState(
        GAgentRegistryState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<GAgentRegistryReadModelUpdateEvent>(ApplyUpdate)
            .OrCurrent();
    }

    private static GAgentRegistryState ApplyUpdate(
        GAgentRegistryState _, GAgentRegistryReadModelUpdateEvent evt)
    {
        return evt.Snapshot?.Clone() ?? new GAgentRegistryState();
    }

    private async Task PublishSnapshotAsync()
    {
        var snapshot = new GAgentRegistryStateSnapshotEvent { Snapshot = State.Clone() };
        await PublishAsync(snapshot, TopologyAudience.Parent);
    }
}
