using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Core.Ports;

/// <summary>
/// Resolves script evolution decision from fallback channel when live session waiting timed out.
/// </summary>
public interface IScriptEvolutionDecisionFallbackPort
{
    Task<ScriptPromotionDecision?> TryResolveAsync(
        string managerActorId,
        string proposalId,
        CancellationToken ct);
}
