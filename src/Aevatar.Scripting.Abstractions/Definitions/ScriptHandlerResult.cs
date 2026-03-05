using Google.Protobuf.WellKnownTypes;
namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptHandlerResult(
    IReadOnlyList<IMessage> DomainEvents,
    IReadOnlyDictionary<string, Any>? StatePayloads = null,
    IReadOnlyDictionary<string, Any>? ReadModelPayloads = null);
