using Aevatar.Scripting.Core.Runtime;

namespace Aevatar.Scripting.Core.Materialization;

public interface IScriptReadModelMaterializationCompiler
{
    ScriptReadModelMaterializationPlan GetOrCompile(
        ScriptBehaviorArtifact artifact,
        string schemaHash,
        string schemaVersion);
}
