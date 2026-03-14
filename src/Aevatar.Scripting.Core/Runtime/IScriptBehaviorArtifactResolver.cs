namespace Aevatar.Scripting.Core.Runtime;

public interface IScriptBehaviorArtifactResolver
{
    ScriptBehaviorArtifact Resolve(ScriptBehaviorArtifactRequest request);
}
