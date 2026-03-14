using Aevatar.Scripting.Core.Artifacts;

namespace Aevatar.Scripting.Core.Materialization;

public interface IScriptReadModelMaterializationCompiler
{
    ScriptReadModelMaterializationPlan GetOrCompile(
        ScriptBehaviorArtifact artifact,
        string schemaHash,
        string schemaVersion);
}
