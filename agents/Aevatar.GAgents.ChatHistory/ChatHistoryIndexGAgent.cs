using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.ChatHistory;

/// <summary>
/// Per-user index actor that holds conversation list and metadata.
/// Actor ID: <c>chat-index-{scopeId}</c>.
/// </summary>
public sealed class ChatHistoryIndexGAgent : GAgentBase<ChatHistoryIndexState>
{
    [EventHandler(EndpointName = "upsertConversation")]
    public async Task HandleConversationUpserted(ConversationUpsertedEvent evt)
    {
        if (evt.Meta is null || string.IsNullOrWhiteSpace(evt.Meta.Id))
            return;

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "removeConversation")]
    public async Task HandleConversationRemoved(ConversationRemovedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.ConversationId))
            return;

        // Idempotent: skip if not present
        var existing = State.Conversations.FirstOrDefault(c =>
            string.Equals(c.Id, evt.ConversationId, StringComparison.Ordinal));
        if (existing is null)
            return;

        await PersistDomainEventAsync(evt);
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
    }

    protected override ChatHistoryIndexState TransitionState(
        ChatHistoryIndexState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<ConversationUpsertedEvent>(ApplyConversationUpserted)
            .On<ConversationRemovedEvent>(ApplyConversationRemoved)
            .OrCurrent();
    }

    private static ChatHistoryIndexState ApplyConversationUpserted(
        ChatHistoryIndexState state, ConversationUpsertedEvent evt)
    {
        var next = state.Clone();

        var existing = next.Conversations.FirstOrDefault(c =>
            string.Equals(c.Id, evt.Meta.Id, StringComparison.Ordinal));
        if (existing is not null)
            next.Conversations.Remove(existing);

        next.Conversations.Add(evt.Meta.Clone());
        return next;
    }

    private static ChatHistoryIndexState ApplyConversationRemoved(
        ChatHistoryIndexState state, ConversationRemovedEvent evt)
    {
        var next = state.Clone();

        var existing = next.Conversations.FirstOrDefault(c =>
            string.Equals(c.Id, evt.ConversationId, StringComparison.Ordinal));
        if (existing is not null)
            next.Conversations.Remove(existing);

        return next;
    }

}
