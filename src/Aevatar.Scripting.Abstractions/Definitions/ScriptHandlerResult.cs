namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptHandlerResult(
    IReadOnlyList<IMessage> DomainEvents,
    string StatePayloadJson = "",
    string ReadModelPayloadJson = "");
