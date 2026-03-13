namespace Aevatar.Scripting.Core.Runtime;

public sealed record ScriptBehaviorRuntimeCapabilityContext(
    string ActorId,
    string ScriptId,
    string Revision,
    string DefinitionActorId,
    string RunId,
    string CorrelationId);
