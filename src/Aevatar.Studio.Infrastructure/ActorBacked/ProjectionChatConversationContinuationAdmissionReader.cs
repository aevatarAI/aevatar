using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

internal sealed class ProjectionChatConversationContinuationAdmissionReader
    : IChatConversationContinuationAdmissionReader
{
    private readonly IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> _documentReader;

    public ProjectionChatConversationContinuationAdmissionReader(
        IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<bool> CanContinueAsync(
        string scopeId,
        string conversationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || string.IsNullOrWhiteSpace(conversationId))
            return false;

        var normalizedScopeId = scopeId.Trim();
        var normalizedConversationId = conversationId.Trim();
        var actorId = ChatHistoryActorIds.Conversation(normalizedScopeId, normalizedConversationId);
        var document = await _documentReader.GetAsync(actorId, ct).ConfigureAwait(false);

        return document is not null &&
               !document.Deleted &&
               string.Equals(document.ScopeId, normalizedScopeId, StringComparison.Ordinal) &&
               string.Equals(document.ConversationId, normalizedConversationId, StringComparison.Ordinal);
    }
}
