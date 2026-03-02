using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Core.Ports;

public interface IScriptEvolutionPort
{
    Task<ScriptPromotionDecision> ProposeAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct);
}
