using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionValidationService : IScriptEvolutionValidationService
{
    private readonly IScriptPackageCompiler _compiler;

    public RuntimeScriptEvolutionValidationService(IScriptPackageCompiler compiler)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    }

    public async Task<ScriptEvolutionValidationReport> ValidateAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var compilation = await _compiler.CompileAsync(
            new ScriptPackageCompilationRequest(
                proposal.ScriptId ?? string.Empty,
                proposal.CandidateRevision ?? string.Empty,
                proposal.CandidateSource ?? string.Empty),
            ct);
        try
        {
            return new ScriptEvolutionValidationReport(
                IsSuccess: compilation.IsSuccess,
                Diagnostics: compilation.Diagnostics ?? Array.Empty<string>());
        }
        finally
        {
            await DisposeCompiledDefinitionAsync(compilation.CompiledDefinition);
        }
    }

    private static async Task DisposeCompiledDefinitionAsync(IScriptPackageDefinition? definition)
    {
        if (definition is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
            return;
        }

        if (definition is IDisposable disposable)
            disposable.Dispose();
    }
}
