using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

internal sealed class ProjectionChatConversationContinuationAdmissionReader
    : IChatConversationContinuationAdmissionReader
{
    private const int MaxConversationContextMessages = 24;
    private readonly IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> _documentReader;

    public ProjectionChatConversationContinuationAdmissionReader(
        IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<ChatConversationContinuationAdmission> GetContinuationAsync(
        string scopeId,
        string conversationId,
        long minimumStateVersion,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || string.IsNullOrWhiteSpace(conversationId))
            return ChatConversationContinuationAdmission.NotFound();

        var normalizedScopeId = scopeId.Trim();
        var normalizedConversationId = conversationId.Trim();
        if (minimumStateVersion <= 0)
            return ChatConversationContinuationAdmission.NotReady();

        var actorIds = new[]
        {
            ChatHistoryActorIds.Conversation(normalizedScopeId, normalizedConversationId),
            ChatHistoryActorIds.LegacyConversation(normalizedScopeId, normalizedConversationId),
        };

        foreach (var actorId in actorIds)
        {
            var document = await _documentReader.GetAsync(actorId, ct).ConfigureAwait(false);
            if (document is not null &&
                !document.Deleted &&
                string.Equals(document.ScopeId, normalizedScopeId, StringComparison.Ordinal) &&
                string.Equals(document.ConversationId, normalizedConversationId, StringComparison.Ordinal))
            {
                if (document.StateVersion < minimumStateVersion)
                    return ChatConversationContinuationAdmission.NotReady();

                var context = ToExecutionContext(document);
                return context.Messages.Count > 0
                    ? ChatConversationContinuationAdmission.Found(context)
                    : ChatConversationContinuationAdmission.NotReady();
            }
        }

        return ChatConversationContinuationAdmission.NotFound();
    }

    private static WorkflowConversationExecutionContext ToExecutionContext(
        ChatConversationCurrentStateDocument document)
    {
        var allMessages = document.Turns
            .OrderBy(static turn => turn.Sequence)
            .SelectMany(ToExecutionMessages)
            .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
            .ToArray();
        var truncated = allMessages.Length > MaxConversationContextMessages;
        var retainedMessages = truncated
            ? allMessages[^MaxConversationContextMessages..]
            : allMessages;

        return new WorkflowConversationExecutionContext(
            document.ScopeId,
            document.ConversationId,
            document.StateVersion,
            retainedMessages
                .Select(static (message, index) => message with { Sequence = index + 1 })
                .ToArray(),
            truncated,
            MaxConversationContextMessages);
    }

    private static IEnumerable<WorkflowConversationExecutionMessage> ToExecutionMessages(
        ChatConversationTurnDocument turn)
    {
        yield return new WorkflowConversationExecutionMessage(
            0,
            turn.TurnId,
            WorkflowConversationExecutionRole.User,
            turn.UserText ?? string.Empty);

        yield return new WorkflowConversationExecutionMessage(
            0,
            turn.TurnId,
            WorkflowConversationExecutionRole.Assistant,
            turn.AssistantText ?? string.Empty);
    }
}
