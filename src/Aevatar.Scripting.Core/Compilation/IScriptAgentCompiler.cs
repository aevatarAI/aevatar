namespace Aevatar.Scripting.Core.Compilation;

public interface IScriptAgentCompiler
{
    Task<ScriptCompilationResult> CompileAsync(
        ScriptCompilationRequest request,
        CancellationToken ct);
}
