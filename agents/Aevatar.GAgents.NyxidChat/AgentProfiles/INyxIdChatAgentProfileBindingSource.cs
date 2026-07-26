using Aevatar.AI.Abstractions;

namespace Aevatar.GAgents.NyxidChat.AgentProfiles;

public enum NyxIdChatAgentProfileBindingStatus
{
    NotSelected = 0,
    Bound = 1,
    ProfileUnavailable = 2,
    AdmissionMismatch = 3,
}

public sealed record NyxIdChatAgentProfileBindingResult(
    NyxIdChatAgentProfileBindingStatus Status,
    AgentProfileExecutionBinding? Binding);

public interface INyxIdChatAgentProfileBindingSource
{
    Task<NyxIdChatAgentProfileBindingResult> ResolveForNewConversationAsync(
        string actorId,
        string routeToolSetName,
        CancellationToken ct = default);
}
