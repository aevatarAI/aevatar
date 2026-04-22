using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.Presentation.AGUI;

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

        var mapped = ScopeGAgentAguiEventMapper.TryMap(envelope);
        if (mapped == null)
            return EmptyEntries;

        return
        [
            new ProjectionSessionEventEntry<AGUIEvent>(
                context.RootActorId,
                context.SessionId,
                mapped),
        ];
    }
}
