using Aevatar.Scripting.Core.Runtime;

namespace Aevatar.Scripting.Core.Compilation;

public interface IScriptBehaviorCompiler
{
    ScriptBehaviorCompilationResult Compile(
        ScriptBehaviorCompilationRequest request);
}
