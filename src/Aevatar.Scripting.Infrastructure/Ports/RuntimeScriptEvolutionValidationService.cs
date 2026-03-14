using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionValidationService : IScriptEvolutionValidationService
{
    private readonly IScriptBehaviorCompiler _compiler;

    public RuntimeScriptEvolutionValidationService(IScriptBehaviorCompiler compiler)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    }

    public async Task<ScriptEvolutionValidationReport> ValidateAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        ct.ThrowIfCancellationRequested();
        var compilation = _compiler.Compile(
            ScriptBehaviorCompilationRequest.FromPersistedSource(
                proposal.ScriptId ?? string.Empty,
                proposal.CandidateRevision ?? string.Empty,
                proposal.CandidateSource ?? string.Empty));
        try
        {
            return new ScriptEvolutionValidationReport(
                IsSuccess: compilation.IsSuccess,
                Diagnostics: compilation.Diagnostics ?? Array.Empty<string>());
        }
        finally
        {
            await DisposeCompiledArtifactAsync(compilation.Artifact);
        }
    }

    private static async Task DisposeCompiledArtifactAsync(Aevatar.Scripting.Core.Artifacts.ScriptBehaviorArtifact? artifact)
    {
        if (artifact != null)
            await artifact.DisposeAsync();
    }
}
