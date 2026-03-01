using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Core;

public interface IScriptEvolutionDecisionSource
{
    ScriptPromotionDecision? GetDecision(string proposalId);
}
