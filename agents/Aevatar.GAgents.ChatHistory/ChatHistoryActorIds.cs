namespace Aevatar.GAgents.ChatHistory;

public static class ChatHistoryActorIds
{
    public static string Conversation(string scopeId, string conversationId) =>
        $"chat-{scopeId.Trim()}-{conversationId.Trim()}";

    public static string TurnDelivery(string workflowActorId, string workflowCommandId) =>
        $"chat-history-delivery-{workflowActorId.Trim()}-{workflowCommandId.Trim()}";
}
