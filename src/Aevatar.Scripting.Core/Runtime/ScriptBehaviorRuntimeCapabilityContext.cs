namespace Aevatar.Scripting.Core.Runtime;

public sealed partial record ScriptBehaviorRuntimeCapabilityContext(
    string ActorId,
    string ScriptId,
    string Revision,
    string DefinitionActorId,
    string ScopeId,
    string RunId,
    string CorrelationId);

public sealed partial record ScriptBehaviorRuntimeCapabilityContext
{
    public ScriptBehaviorRuntimeCapabilityContext(
        string ActorId,
        string ScriptId,
        string Revision,
        string DefinitionActorId,
        string RunId,
        string CorrelationId)
        : this(
            ActorId,
            ScriptId,
            Revision,
            DefinitionActorId,
            ScopeId: string.Empty,
            RunId,
            CorrelationId)
    {
    }
}
