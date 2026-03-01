namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptDecisionResult(
    IReadOnlyList<IMessage> DomainEvents,
    string StatePayloadJson = "");
