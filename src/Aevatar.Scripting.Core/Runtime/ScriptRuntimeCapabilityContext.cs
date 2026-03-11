namespace Aevatar.Scripting.Core.Runtime;

public sealed record ScriptRuntimeCapabilityContext(
    string RuntimeActorId,
    string ScriptId,
    string CurrentRevision,
    string DefinitionActorId,
    string RunId,
    string CorrelationId,
    ScriptExecutionMessageContext MessageContext);
