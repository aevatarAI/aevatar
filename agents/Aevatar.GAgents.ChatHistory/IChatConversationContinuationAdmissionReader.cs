namespace Aevatar.GAgents.ChatHistory;

public interface IChatConversationContinuationAdmissionReader
{
    Task<bool> CanContinueAsync(
        string scopeId,
        string conversationId,
        CancellationToken ct = default);
}
