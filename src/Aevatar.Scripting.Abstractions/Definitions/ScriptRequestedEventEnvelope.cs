namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptRequestedEventEnvelope(
    string EventType,
    string PayloadJson,
    string EventId = "",
    string CorrelationId = "",
    string CausationId = "");
