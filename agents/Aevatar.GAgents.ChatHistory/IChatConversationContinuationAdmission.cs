namespace Aevatar.GAgents.ChatHistory;

public interface IChatConversationContinuationAdmission
{
    bool CanContinue(string scopeId, string conversationId);
}
