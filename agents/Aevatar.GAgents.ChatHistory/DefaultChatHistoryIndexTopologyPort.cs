namespace Aevatar.GAgents.ChatHistory;

public sealed class DefaultChatHistoryIndexTopologyPort : IChatHistoryIndexTopologyPort
{
    public string GetIndexActorId(string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        return $"chat-index-{scopeId}";
    }
}
