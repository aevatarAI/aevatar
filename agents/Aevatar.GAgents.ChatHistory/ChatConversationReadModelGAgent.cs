using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.ChatHistory;

/// <summary>
/// Persistent readmodel actor for a single conversation.
/// Receives state snapshots from <see cref="ChatConversationGAgent"/> via
/// <see cref="ChatConversationReadModelUpdateEvent"/> (SendToAsync) and persists them.
///
/// Actor ID convention: <c>{writeActorId}-readmodel</c>.
///
/// On activation and after each update, publishes
/// <see cref="ChatConversationStateSnapshotEvent"/> so per-request subscribers
/// (ActorBackedStore) can receive the current projected state.
/// </summary>
public sealed class ChatConversationReadModelGAgent : GAgentBase<ChatConversationState>
{
    [EventHandler(EndpointName = "updateReadModel")]
    public async Task HandleReadModelUpdate(ChatConversationReadModelUpdateEvent evt)
    {
        await PersistDomainEventAsync(evt);
        await PublishSnapshotAsync();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await PublishSnapshotAsync();
    }

    protected override ChatConversationState TransitionState(
        ChatConversationState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<ChatConversationReadModelUpdateEvent>(ApplyUpdate)
            .OrCurrent();
    }

    private static ChatConversationState ApplyUpdate(
        ChatConversationState _, ChatConversationReadModelUpdateEvent evt)
    {
        return evt.Snapshot?.Clone() ?? new ChatConversationState();
    }

    private async Task PublishSnapshotAsync()
    {
        var snapshot = new ChatConversationStateSnapshotEvent { Snapshot = State.Clone() };
        await PublishAsync(snapshot, TopologyAudience.Parent);
    }
}
