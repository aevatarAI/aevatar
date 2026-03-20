using Aevatar.Scripting.Core.Runtime;

namespace Aevatar.Scripting.Core.Materialization;

public interface IScriptReadModelMaterializationCompiler
{
    ScriptReadModelMaterializationPlan Compile(
        ScriptBehaviorArtifact artifact,
        string schemaHash,
        string schemaVersion);
}
