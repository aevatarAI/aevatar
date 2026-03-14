using Aevatar.Scripting.Core;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Runtime;

public sealed record ScriptRuntimeExecutionRequest(
    string RuntimeActorId,
    IReadOnlyDictionary<string, Any>? CurrentState,
    IReadOnlyDictionary<string, Any>? CurrentReadModel,
    RunScriptRequestedEvent RunEvent,
    string ScriptId,
    string ScriptRevision,
    string SourceText,
    string ReadModelSchemaVersion,
    string ReadModelSchemaHash,
    ScriptExecutionMessageContext MessageContext);
