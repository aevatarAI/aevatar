namespace Aevatar.GAgents.ChatHistory;

public sealed class ChatConversationContinuationAdmissionNotFoundException : Exception
{
    public ChatConversationContinuationAdmissionNotFoundException(
        string scopeId,
        string conversationId)
        : base("Chat history conversation was not found.")
    {
        ScopeId = scopeId;
        ConversationId = conversationId;
    }

    public string ScopeId { get; }

    public string ConversationId { get; }
}
