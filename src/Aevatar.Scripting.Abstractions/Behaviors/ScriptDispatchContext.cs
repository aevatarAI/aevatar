namespace Aevatar.Scripting.Abstractions.Behaviors;

public sealed partial record ScriptDispatchContext(
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
    string ScopeId,
    IMessage? CurrentState,
    IScriptBehaviorRuntimeCapabilities RuntimeCapabilities);

public sealed partial record ScriptDispatchContext
{
    public ScriptDispatchContext(
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
        IScriptBehaviorRuntimeCapabilities RuntimeCapabilities)
        : this(
            ActorId,
            ScriptId,
            Revision,
            RunId,
            MessageType,
            MessageId,
            CommandId,
            CorrelationId,
            CausationId,
            DefinitionActorId,
            ScopeId: string.Empty,
            CurrentState,
            RuntimeCapabilities)
    {
    }
}
