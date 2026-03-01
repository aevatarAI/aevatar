namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptDomainEventEnvelope(
    string EventType,
    string PayloadJson,
    string EventId = "",
    string CorrelationId = "",
    string CausationId = "");
