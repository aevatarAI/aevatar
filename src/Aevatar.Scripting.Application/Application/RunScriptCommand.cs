using Google.Protobuf.WellKnownTypes;
namespace Aevatar.Scripting.Application;

public sealed record RunScriptCommand(
    string RunId,
    Any? InputPayload,
    string ScriptRevision,
    string DefinitionActorId,
    string RequestedEventType = "");
