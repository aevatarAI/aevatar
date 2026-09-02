using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Core.AgentProfiles;

public sealed record AgentProfileTurnClassificationCandidate(
    string IntentId,
    string RoutingDescription,
    AgentProfileSideEffectClass SideEffectClass);

public sealed record AgentProfileTurnClassificationRequest(
    string UserMessage,
    IReadOnlyList<AgentProfileTurnClassificationCandidate> Candidates,
    TimeSpan Timeout);

public enum AgentProfileTurnClassificationStatus
{
    Matched = 0,
    NoMatch = 1,
    Failed = 2,
}

public sealed record AgentProfileTurnClassificationResult(
    AgentProfileTurnClassificationStatus Status,
    string? IntentId,
    string? FailureCode)
{
    public static AgentProfileTurnClassificationResult Matched(string intentId) =>
        new(AgentProfileTurnClassificationStatus.Matched, intentId, null);

    public static AgentProfileTurnClassificationResult NoMatch() =>
        new(AgentProfileTurnClassificationStatus.NoMatch, null, null);

    public static AgentProfileTurnClassificationResult Failed(string failureCode) =>
        new(AgentProfileTurnClassificationStatus.Failed, null, failureCode);
}

public interface IAgentProfileTurnClassifier
{
    Task<AgentProfileTurnClassificationResult> ClassifyAsync(
        AgentProfileTurnClassificationRequest request,
        CancellationToken ct = default);
}
