namespace Aevatar.GAgents.Channel.Runtime;

public sealed partial class ConversationGAgent
{
    public static string BuildActorId(string canonicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalKey);
        return $"channel-conversation:{canonicalKey.Trim()}";
    }
}
