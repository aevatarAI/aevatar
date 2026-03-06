using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Application;

public interface IScriptEvolutionApplicationService
{
    Task<ScriptPromotionDecision> ProposeAsync(
        ProposeScriptEvolutionRequest request,
        CancellationToken ct);
}
