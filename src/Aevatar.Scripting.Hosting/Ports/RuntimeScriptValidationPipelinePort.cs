using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeScriptValidationPipelinePort : IScriptValidationPipelinePort
{
    private readonly IScriptPackageCompiler _compiler;

    public RuntimeScriptValidationPipelinePort(IScriptPackageCompiler compiler)
    {
        _compiler = compiler;
    }

    public async Task<ScriptEvolutionValidationReport> ValidateAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var result = await _compiler.CompileAsync(
            new ScriptPackageCompilationRequest(
                proposal.ScriptId ?? string.Empty,
                proposal.CandidateRevision ?? string.Empty,
                proposal.CandidateSource ?? string.Empty),
            ct);

        return new ScriptEvolutionValidationReport(
            IsSuccess: result.IsSuccess,
            Diagnostics: result.Diagnostics ?? Array.Empty<string>());
    }
}
