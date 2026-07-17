using Aevatar.AI.Abstractions;

namespace Aevatar.GAgents.NyxidChat.AgentProfiles;

public interface INyxIdChatAgentProfileSnapshotSource
{
    AgentProfileSnapshot? GetSnapshotForNewConversation();
}
