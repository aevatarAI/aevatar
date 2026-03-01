namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptExecutionContext(
    string ActorId,
    string ScriptId,
    string Revision,
    string RunId = "",
    string CorrelationId = "",
    string DefinitionActorId = "",
    string CurrentStateJson = "",
    string CurrentReadModelJson = "",
    string InputJson = "",
    IScriptRuntimeCapabilities? Capabilities = null);
