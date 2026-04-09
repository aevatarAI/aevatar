using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.StreamingProxyParticipant;

/// <summary>
/// Persistent readmodel actor for streaming proxy participants.
/// Receives state snapshots from <see cref="StreamingProxyParticipantGAgent"/> via
/// <see cref="StreamingProxyParticipantReadModelUpdateEvent"/> (SendToAsync) and persists them.
///
/// Actor ID convention: <c>{writeActorId}-readmodel</c>.
///
/// On activation and after each update, publishes
/// <see cref="StreamingProxyParticipantStateSnapshotEvent"/> so per-request subscribers
/// (ActorBackedStore) can receive the current projected state.
/// </summary>
public sealed class StreamingProxyParticipantReadModelGAgent
    : GAgentBase<StreamingProxyParticipantGAgentState>
{
    [EventHandler(EndpointName = "updateReadModel")]
    public async Task HandleReadModelUpdate(StreamingProxyParticipantReadModelUpdateEvent evt)
    {
        await PersistDomainEventAsync(evt);
        await PublishSnapshotAsync();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await PublishSnapshotAsync();
    }

    protected override StreamingProxyParticipantGAgentState TransitionState(
        StreamingProxyParticipantGAgentState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<StreamingProxyParticipantReadModelUpdateEvent>(ApplyUpdate)
            .OrCurrent();
    }

    private static StreamingProxyParticipantGAgentState ApplyUpdate(
        StreamingProxyParticipantGAgentState _, StreamingProxyParticipantReadModelUpdateEvent evt)
    {
        return evt.Snapshot?.Clone() ?? new StreamingProxyParticipantGAgentState();
    }

    private async Task PublishSnapshotAsync()
    {
        var snapshot = new StreamingProxyParticipantStateSnapshotEvent { Snapshot = State.Clone() };
        await PublishAsync(snapshot, TopologyAudience.Parent);
    }
}
