using Aevatar.Scripting.Core.Artifacts;

namespace Aevatar.Scripting.Core.Compilation;

public interface IScriptBehaviorCompiler
{
    ScriptBehaviorCompilationResult Compile(
        ScriptBehaviorCompilationRequest request);
}
