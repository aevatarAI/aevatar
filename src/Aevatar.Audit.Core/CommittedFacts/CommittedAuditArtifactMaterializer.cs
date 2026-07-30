using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Audit.Core.CommittedFacts;

public sealed class CommittedAuditArtifactMaterializer<TContext>
    : IProjectionArtifactMaterializer<TContext>
    where TContext : class, IProjectionMaterializationContext
{
    private const string CommandIdBaggageKey = "command_id";
    private const string CommandIdCamelBaggageKey = "commandId";
    private const string RequestIdBaggageKey = "request_id";
    private const string RequestIdCamelBaggageKey = "requestId";

    private readonly AuditCommittedEventTranslatorRegistry _registry;
    private readonly IAuditTrailAppender _appender;
    private readonly IProjectionClock _clock;
    private readonly ILogger<CommittedAuditArtifactMaterializer<TContext>> _logger;

    public CommittedAuditArtifactMaterializer(
        AuditCommittedEventTranslatorRegistry registry,
        IAuditTrailAppender appender,
        IProjectionClock clock,
        ILogger<CommittedAuditArtifactMaterializer<TContext>>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _appender = appender ?? throw new ArgumentNullException(nameof(appender));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? NullLogger<CommittedAuditArtifactMaterializer<TContext>>.Instance;
    }

    public async ValueTask ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpack(envelope, out var published) ||
            published?.StateEvent?.EventData == null)
        {
            return;
        }

        var stateEvent = published.StateEvent;
        // A maintenance republish re-broadcasts an already-committed fact under a
        // synthetic event id; translating it would fabricate a duplicate governance
        // record (and repeat republishes at the same version would append-conflict).
        // The maintenance action itself is audited by the invoking admin endpoint.
        if (CommittedStateRepublish.IsRepublishEventId(stateEvent.EventId))
            return;

        var eventTypeUrl = stateEvent.EventData.TypeUrl ?? string.Empty;
        if (!_registry.TryGet(eventTypeUrl, out var translator))
            return;

        var recordedAt = _clock.UtcNow;
        var translationContext = new CommittedAuditTranslationContext(
            envelope,
            published,
            stateEvent,
            string.IsNullOrWhiteSpace(stateEvent.AgentId) ? context.RootActorId : stateEvent.AgentId,
            eventTypeUrl,
            CommittedStateEventEnvelope.ResolveTimestamp(envelope, recordedAt),
            ResolveBaggage(envelope, CommandIdBaggageKey, CommandIdCamelBaggageKey),
            ResolveBaggage(envelope, RequestIdBaggageKey, RequestIdCamelBaggageKey),
            envelope.Propagation?.CorrelationId ?? string.Empty,
            envelope.Propagation?.Trace?.TraceId ?? string.Empty,
            envelope.Propagation?.Trace?.SpanId ?? string.Empty,
            envelope.Propagation?.Trace?.TraceFlags ?? string.Empty,
            envelope.Propagation?.CausationEventId ?? string.Empty,
            RecordedAt: recordedAt);

        IReadOnlyList<Audit.AuditRecord> records;
        try
        {
            records = translator.Translate(translationContext, stateEvent.EventData);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Committed audit translator failed. eventTypeUrl={EventTypeUrl} sourceEventId={SourceEventId}",
                eventTypeUrl,
                stateEvent.EventId);
            return;
        }

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _appender.AppendAsync(record, ct);
            if (result.Status is AuditTrailAppendStatus.Conflict or AuditTrailAppendStatus.StoreUnavailable)
            {
                _logger.LogError(
                    "Committed audit append failed. status={Status} auditId={AuditId} message={Message}",
                    result.Status,
                    result.AuditId,
                    result.Message);
            }
        }
    }

    private static string ResolveBaggage(EventEnvelope envelope, string snakeKey, string camelKey)
    {
        var baggage = envelope.Propagation?.Baggage;
        if (baggage == null)
            return string.Empty;
        if (baggage.TryGetValue(snakeKey, out var snakeValue) && !string.IsNullOrWhiteSpace(snakeValue))
            return snakeValue;
        if (baggage.TryGetValue(camelKey, out var camelValue) && !string.IsNullOrWhiteSpace(camelValue))
            return camelValue;

        return string.Empty;
    }
}
