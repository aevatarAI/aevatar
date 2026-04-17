using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChatHistory;

/// <summary>
/// Per-conversation actor that holds all messages for a single conversation.
/// Actor ID: <c>chat-{scopeId}-{conversationId}</c>.
///
/// When messages are replaced or the conversation is deleted, forwards
/// the change to the <see cref="ChatHistoryIndexGAgent"/> via <c>SendToAsync</c>,
/// ensuring transactional consistency between conversation and index actors.
/// </summary>
public sealed class ChatConversationGAgent : GAgentBase<ChatConversationState>
{
    /// <summary>Maximum messages retained per conversation.</summary>
    internal const int MaxMessages = 500;

    [EventHandler(EndpointName = "replaceMessages")]
    public async Task HandleMessagesReplaced(MessagesReplacedEvent evt)
    {
        if (evt.Meta is null)
            return;

        // Trim to MaxMessages (keep newest)
        var trimmed = TrimMessages(evt);

        await PersistDomainEventAsync(trimmed);

        // Forward index upsert to the index actor
        if (!string.IsNullOrWhiteSpace(evt.ScopeId))
        {
            var indexActorId = IndexActorId(evt.ScopeId);
            await EnsureIndexActorAsync(indexActorId);
            var indexMeta = State.Meta?.Clone();
            if (indexMeta is not null)
            {
                indexMeta.MessageCount = State.Messages.Count;
                await SendToAsync(indexActorId, new ConversationUpsertedEvent { Meta = indexMeta });
            }
        }
    }

    [EventHandler(EndpointName = "deleteConversation")]
    public async Task HandleConversationDeleted(ConversationDeletedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.ConversationId))
            return;

        // Only delete if there is state to delete
        if (State.Meta is null && State.Messages.Count == 0)
            return;

        await PersistDomainEventAsync(evt);

        // Forward index removal to the index actor
        if (!string.IsNullOrWhiteSpace(evt.ScopeId))
        {
            var indexActorId = IndexActorId(evt.ScopeId);
            await EnsureIndexActorAsync(indexActorId);
            await SendToAsync(indexActorId, new ConversationRemovedEvent { ConversationId = evt.ConversationId });
        }
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
    }

    protected override ChatConversationState TransitionState(
        ChatConversationState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<MessagesReplacedEvent>(ApplyMessagesReplaced)
            .On<ConversationDeletedEvent>(ApplyConversationDeleted)
            .OrCurrent();
    }

    private static MessagesReplacedEvent TrimMessages(MessagesReplacedEvent evt)
    {
        if (evt.Messages.Count <= MaxMessages)
            return evt;

        var trimmed = evt.Clone();
        var excess = trimmed.Messages.Count - MaxMessages;
        for (var i = 0; i < excess; i++)
            trimmed.Messages.RemoveAt(0);

        if (trimmed.Meta is not null)
            trimmed.Meta.MessageCount = trimmed.Messages.Count;

        return trimmed;
    }

    private static ChatConversationState ApplyMessagesReplaced(
        ChatConversationState state, MessagesReplacedEvent evt)
    {
        var next = new ChatConversationState { Meta = evt.Meta?.Clone() };
        next.Messages.AddRange(evt.Messages);
        return next;
    }

    private static ChatConversationState ApplyConversationDeleted(
        ChatConversationState state, ConversationDeletedEvent evt)
    {
        return new ChatConversationState();
    }

    private async Task EnsureIndexActorAsync(string indexActorId)
    {
        var runtime = Services.GetRequiredService<IActorRuntime>();
        if (await runtime.GetAsync(indexActorId) is null)
            await runtime.CreateAsync<ChatHistoryIndexGAgent>(indexActorId);
    }

    private static string IndexActorId(string scopeId) => $"chat-index-{scopeId}";
}
