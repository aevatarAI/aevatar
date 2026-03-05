namespace Aevatar.Scripting.Core.Compilation;

public interface IScriptPackageCompiler
{
    Task<ScriptPackageCompilationResult> CompileAsync(
        ScriptPackageCompilationRequest request,
        CancellationToken ct);
}
