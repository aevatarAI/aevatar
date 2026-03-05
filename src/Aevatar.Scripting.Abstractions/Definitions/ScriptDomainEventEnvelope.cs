using Google.Protobuf.WellKnownTypes;
namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptDomainEventEnvelope(
    string EventType,
    Any Payload,
    string EventId = "",
    string CorrelationId = "",
    string CausationId = "");
