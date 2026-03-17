using Google.Protobuf;

namespace Aevatar.Scripting.Core.Compilation;

public interface IScriptProtoCompiler
{
    ScriptProtoCompilationResult Compile(ScriptBehaviorCompilationRequest request);
}
