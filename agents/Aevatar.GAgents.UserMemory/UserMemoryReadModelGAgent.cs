using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.UserMemory;

/// <summary>
/// Persistent readmodel actor for user memory.
/// Receives state snapshots from <see cref="UserMemoryGAgent"/> via
/// <see cref="UserMemoryReadModelUpdateEvent"/> (SendToAsync) and persists them.
///
/// Actor ID convention: <c>{writeActorId}-readmodel</c>.
///
/// On activation and after each update, publishes
/// <see cref="UserMemoryStateSnapshotEvent"/> so per-request subscribers
/// (ActorBackedStore) can receive the current projected state.
/// </summary>
public sealed class UserMemoryReadModelGAgent : GAgentBase<UserMemoryState>
{
    [EventHandler(EndpointName = "updateReadModel")]
    public async Task HandleReadModelUpdate(UserMemoryReadModelUpdateEvent evt)
    {
        await PersistDomainEventAsync(evt);
        await PublishSnapshotAsync();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await PublishSnapshotAsync();
    }

    protected override UserMemoryState TransitionState(
        UserMemoryState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<UserMemoryReadModelUpdateEvent>(ApplyUpdate)
            .OrCurrent();
    }

    private static UserMemoryState ApplyUpdate(
        UserMemoryState _, UserMemoryReadModelUpdateEvent evt)
    {
        return evt.Snapshot?.Clone() ?? new UserMemoryState();
    }

    private async Task PublishSnapshotAsync()
    {
        var snapshot = new UserMemoryStateSnapshotEvent { Snapshot = State.Clone() };
        await PublishAsync(snapshot, TopologyAudience.Parent);
    }
}
