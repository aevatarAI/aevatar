using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.ChatHistory;

/// <summary>
/// Persistent readmodel actor for the chat history index.
/// Receives state snapshots from <see cref="ChatHistoryIndexGAgent"/> via
/// <see cref="ChatHistoryIndexReadModelUpdateEvent"/> (SendToAsync) and persists them.
///
/// Actor ID convention: <c>{writeActorId}-readmodel</c>.
///
/// On activation and after each update, publishes
/// <see cref="ChatHistoryIndexStateSnapshotEvent"/> so per-request subscribers
/// (ActorBackedStore) can receive the current projected state.
/// </summary>
public sealed class ChatHistoryIndexReadModelGAgent : GAgentBase<ChatHistoryIndexState>
{
    [EventHandler(EndpointName = "updateReadModel")]
    public async Task HandleReadModelUpdate(ChatHistoryIndexReadModelUpdateEvent evt)
    {
        await PersistDomainEventAsync(evt);
        await PublishSnapshotAsync();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await PublishSnapshotAsync();
    }

    protected override ChatHistoryIndexState TransitionState(
        ChatHistoryIndexState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<ChatHistoryIndexReadModelUpdateEvent>(ApplyUpdate)
            .OrCurrent();
    }

    private static ChatHistoryIndexState ApplyUpdate(
        ChatHistoryIndexState _, ChatHistoryIndexReadModelUpdateEvent evt)
    {
        return evt.Snapshot?.Clone() ?? new ChatHistoryIndexState();
    }

    private async Task PublishSnapshotAsync()
    {
        var snapshot = new ChatHistoryIndexStateSnapshotEvent { Snapshot = State.Clone() };
        await PublishAsync(snapshot, TopologyAudience.Parent);
    }
}
