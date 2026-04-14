namespace Aevatar.Scripting.Core.Ports;

public sealed record ScriptingCommandAcceptedReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId,
    DateTimeOffset AcceptedAt = default);
