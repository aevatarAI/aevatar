using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Application;

public interface IScriptEvolutionApplicationService
{
    Task<ScriptEvolutionCommandAccepted> ProposeAsync(
        ProposeScriptEvolutionRequest request,
        CancellationToken ct);
}
