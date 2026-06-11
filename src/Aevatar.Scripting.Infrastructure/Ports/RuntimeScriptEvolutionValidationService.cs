using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionValidationService : IScriptEvolutionValidationService
{
    // Refactor (iter42/cluster-044-scripting-source-package-json-shadow):
    //   Old pattern: Scripting persists and republishes source_text as a compatibility shadow of ScriptPackageSpec; multi-file packages can be encoded as JSON text and reparsed from persisted source.
    //   New principle: ScriptPackageSpec is the sole internal source-package contract for commands/state/events/readmodels; source_text is only an external one-file adapter field at Host/Application boundary.
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
            new ScriptBehaviorCompilationRequest(
                proposal.ScriptId ?? string.Empty,
                proposal.CandidateRevision ?? string.Empty,
                ScriptPackageSpecExtensions.CreateSingleSource(proposal.CandidateSource ?? string.Empty)));
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

    private static async Task DisposeCompiledArtifactAsync(Aevatar.Scripting.Core.Runtime.ScriptBehaviorArtifact? artifact)
    {
        if (artifact != null)
            await artifact.DisposeAsync();
    }
}
