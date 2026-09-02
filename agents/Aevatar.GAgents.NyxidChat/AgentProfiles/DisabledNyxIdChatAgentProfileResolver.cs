namespace Aevatar.GAgents.NyxidChat.AgentProfiles;

public sealed class DisabledNyxIdChatAgentProfileResolver : INyxIdChatAgentProfileResolver
{
    public Task<NyxIdChatAgentProfileResolution> ResolveAsync(
        NyxIdChatAgentProfileSelectionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(NyxIdChatAgentProfileResolution.Unprofiled());
    }
}
