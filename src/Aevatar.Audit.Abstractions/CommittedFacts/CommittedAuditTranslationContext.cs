using Aevatar.Foundation.Abstractions;

namespace Aevatar.Audit.Abstractions.CommittedFacts;

public sealed record CommittedAuditTranslationContext(
    EventEnvelope Envelope,
    CommittedStateEventPublished Published,
    StateEvent StateEvent,
    string OriginActorId,
    string EventTypeUrl,
    DateTimeOffset ObservedAt,
    string CommandId,
    string RequestId,
    string CorrelationId,
    string TraceId = "",
    string SpanId = "",
    string TraceFlags = "",
    string CausationId = "",
    DateTimeOffset? RecordedAt = null);
