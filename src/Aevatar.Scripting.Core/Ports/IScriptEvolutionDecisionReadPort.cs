using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Core.Ports;

public interface IScriptEvolutionDecisionReadPort
{
    Task<ScriptPromotionDecision?> TryGetAsync(
        string proposalId,
        CancellationToken ct);
}
