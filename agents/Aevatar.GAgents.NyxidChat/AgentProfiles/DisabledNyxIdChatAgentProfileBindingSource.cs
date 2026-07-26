namespace Aevatar.GAgents.NyxidChat.AgentProfiles;

public sealed class DisabledNyxIdChatAgentProfileBindingSource : INyxIdChatAgentProfileBindingSource
{
    public Task<NyxIdChatAgentProfileBindingResult> ResolveForNewConversationAsync(
        string actorId,
        string routeToolSetName,
        CancellationToken ct = default) =>
        Task.FromResult(new NyxIdChatAgentProfileBindingResult(
            NyxIdChatAgentProfileBindingStatus.NotSelected,
            null));
}
