using Google.Protobuf.WellKnownTypes;
namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptExecutionContext(
    string ActorId,
    string ScriptId,
    string Revision,
    string RunId = "",
    string CorrelationId = "",
    string DefinitionActorId = "",
    IReadOnlyDictionary<string, Any>? CurrentState = null,
    IReadOnlyDictionary<string, Any>? CurrentReadModel = null,
    Any? InputPayload = null,
    IScriptRuntimeCapabilities? Capabilities = null);
