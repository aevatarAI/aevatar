using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.UserConfig;

/// <summary>
/// Persistent readmodel actor for user configuration.
/// Receives state snapshots from <see cref="UserConfigGAgent"/> via
/// <see cref="UserConfigReadModelUpdateEvent"/> (SendToAsync) and persists them.
///
/// Actor ID convention: <c>{writeActorId}-readmodel</c>.
///
/// On activation and after each update, publishes
/// <see cref="UserConfigStateSnapshotEvent"/> so per-request subscribers
/// (ActorBackedStore) can receive the current projected state.
/// </summary>
public sealed class UserConfigReadModelGAgent : GAgentBase<UserConfigGAgentState>
{
    [EventHandler(EndpointName = "updateReadModel")]
    public async Task HandleReadModelUpdate(UserConfigReadModelUpdateEvent evt)
    {
        await PersistDomainEventAsync(evt);
        await PublishSnapshotAsync();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await PublishSnapshotAsync();
    }

    protected override UserConfigGAgentState TransitionState(
        UserConfigGAgentState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<UserConfigReadModelUpdateEvent>(ApplyUpdate)
            .OrCurrent();
    }

    private static UserConfigGAgentState ApplyUpdate(
        UserConfigGAgentState _, UserConfigReadModelUpdateEvent evt)
    {
        return evt.Snapshot?.Clone() ?? new UserConfigGAgentState();
    }

    private async Task PublishSnapshotAsync()
    {
        var snapshot = new UserConfigStateSnapshotEvent { Snapshot = State.Clone() };
        await PublishAsync(snapshot, TopologyAudience.Parent);
    }
}
