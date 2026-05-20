using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.Presentation.AGUI;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class GAgentDraftRunSessionEventProjector
    : ProjectionSessionEventProjectorBase<GAgentDraftRunProjectionContext, AGUIEvent>
{
    public GAgentDraftRunSessionEventProjector(
        IProjectionSessionEventHub<AGUIEvent> sessionEventHub)
        : base(sessionEventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<AGUIEvent>> ResolveSessionEventEntries(
        GAgentDraftRunProjectionContext context,
        EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.IsNullOrWhiteSpace(context.SessionId))
            return EmptyEntries;

        if (!string.Equals(envelope.Propagation?.CorrelationId, context.SessionId, StringComparison.Ordinal))
            return EmptyEntries;

        var mapped = TryMapCommittedStateEvent(envelope) ?? ScopeGAgentAguiEventMapper.TryMap(envelope);
        if (mapped == null)
            return EmptyEntries;

        CompleteRunFinishedFrame(context, mapped);
        if (mapped.EventCase == AGUIEvent.EventOneofCase.TextMessageEnd)
        {
            return
            [
                new ProjectionSessionEventEntry<AGUIEvent>(
                    context.RootActorId,
                    context.SessionId,
                    mapped),
                new ProjectionSessionEventEntry<AGUIEvent>(
                    context.RootActorId,
                    context.SessionId,
                    new AGUIEvent
                    {
                        RunFinished = new RunFinishedEvent
                        {
                            ThreadId = context.RootActorId,
                            RunId = context.SessionId,
                        },
                    }),
            ];
        }

        return
        [
            new ProjectionSessionEventEntry<AGUIEvent>(
                context.RootActorId,
                context.SessionId,
                mapped),
        ];
    }

    private static AGUIEvent? TryMapCommittedStateEvent(EventEnvelope envelope)
    {
        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) ||
            payload == null)
        {
            return null;
        }

        return ScopeGAgentAguiEventMapper.TryMap(new EventEnvelope
        {
            Id = envelope.Id,
            Timestamp = envelope.Timestamp?.Clone(),
            Payload = payload.Clone(),
            Route = envelope.Route?.Clone(),
            Propagation = envelope.Propagation?.Clone(),
        });
    }

    private static void CompleteRunFinishedFrame(
        GAgentDraftRunProjectionContext context,
        AGUIEvent aguiEvent)
    {
        if (aguiEvent.EventCase != AGUIEvent.EventOneofCase.RunFinished)
            return;

        aguiEvent.RunFinished.ThreadId = string.IsNullOrWhiteSpace(aguiEvent.RunFinished.ThreadId)
            ? context.RootActorId
            : aguiEvent.RunFinished.ThreadId;
        aguiEvent.RunFinished.RunId = string.IsNullOrWhiteSpace(aguiEvent.RunFinished.RunId)
            ? context.SessionId
            : aguiEvent.RunFinished.RunId;
    }
}
