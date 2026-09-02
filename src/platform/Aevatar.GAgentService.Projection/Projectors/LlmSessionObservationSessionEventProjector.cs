using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Orchestration;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class LlmSessionObservationSessionEventProjector
    : ProjectionSessionEventProjectorBase<LlmSessionObservationProjectionContext, EventEnvelope>
{
    public LlmSessionObservationSessionEventProjector(
        LlmSessionObservationSessionEventHub sessionEventHub)
        : base(sessionEventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<EventEnvelope>> ResolveSessionEventEntries(
        LlmSessionObservationProjectionContext context,
        EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.IsNullOrWhiteSpace(context.SessionId))
            return EmptyEntries;

        if (!string.Equals(envelope.Propagation?.CorrelationId, context.SessionId, StringComparison.Ordinal))
            return EmptyEntries;

        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) || payload == null)
            return EmptyEntries;

        if (!payload.Is(LlmRunStartedEvent.Descriptor) &&
            !payload.Is(LlmStreamChunkObserved.Descriptor) &&
            !payload.Is(LlmToolCallObserved.Descriptor) &&
            !payload.Is(LlmRunCompleted.Descriptor) &&
            !payload.Is(LlmRunFailed.Descriptor) &&
            !payload.Is(LlmRunCancelled.Descriptor))
        {
            return EmptyEntries;
        }

        return
        [
            new ProjectionSessionEventEntry<EventEnvelope>(
                context.RootActorId,
                context.SessionId,
                envelope)
        ];
    }
}
