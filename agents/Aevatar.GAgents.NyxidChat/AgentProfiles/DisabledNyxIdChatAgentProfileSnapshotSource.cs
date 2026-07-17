using Aevatar.AI.Abstractions;

namespace Aevatar.GAgents.NyxidChat.AgentProfiles;

public sealed class DisabledNyxIdChatAgentProfileSnapshotSource : INyxIdChatAgentProfileSnapshotSource
{
    public AgentProfileSnapshot? GetSnapshotForNewConversation() => null;
}
