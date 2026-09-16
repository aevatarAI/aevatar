using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.GAgents.NyxidChat.AgentProfiles;

public sealed record AgentProfileConnectedOperationSelectionCandidate(
    string CandidateId,
    string CatalogServiceSlug,
    string ConnectorDisplayName,
    string ConnectionLabel,
    string DisplayName,
    string Description,
    string HttpMethod,
    string PathTemplate,
    AgentToolOperationRisk Risk);

public sealed record AgentProfileConnectedOperationSelectionRequest(
    string UserMessage,
    IReadOnlyList<AgentProfileConnectedOperationSelectionCandidate> Candidates,
    int MaximumReadSelections,
    int MaximumWriteSelections,
    TimeSpan Timeout,
    LLMControlContext? LlmControl = null,
    string? RequestId = null);

public enum AgentProfileConnectedOperationSelectionStatus
{
    Selected = 0,
    NoMatch = 1,
    Failed = 2,
}

public sealed record AgentProfileConnectedOperationSelectionResult(
    AgentProfileConnectedOperationSelectionStatus Status,
    IReadOnlyList<string> CandidateIds,
    string? FailureCode)
{
    public static AgentProfileConnectedOperationSelectionResult Selected(
        IReadOnlyList<string> candidateIds) =>
        new(AgentProfileConnectedOperationSelectionStatus.Selected, candidateIds, null);

    public static AgentProfileConnectedOperationSelectionResult NoMatch() =>
        new(AgentProfileConnectedOperationSelectionStatus.NoMatch, [], null);

    public static AgentProfileConnectedOperationSelectionResult Failed(string failureCode) =>
        new(AgentProfileConnectedOperationSelectionStatus.Failed, [], failureCode);
}

public interface IAgentProfileConnectedOperationSelector
{
    Task<AgentProfileConnectedOperationSelectionResult> SelectAsync(
        AgentProfileConnectedOperationSelectionRequest request,
        CancellationToken ct = default);
}
