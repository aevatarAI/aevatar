using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Core.Ports;

public interface IScriptValidationPipelinePort
{
    Task<ScriptEvolutionValidationReport> ValidateAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct);
}
