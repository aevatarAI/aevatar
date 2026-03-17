namespace Aevatar.Scripting.Abstractions.Behaviors;

public sealed record ScriptDispatchContext(
    string ActorId,
    string ScriptId,
    string Revision,
    string RunId,
    string MessageType,
    string MessageId,
    string CommandId,
    string CorrelationId,
    string CausationId,
    string DefinitionActorId,
    IMessage? CurrentState,
    IScriptBehaviorRuntimeCapabilities RuntimeCapabilities);
