using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.AGUI.Contracts;

namespace Aevatar.GAgentService.Projection.Projectors;

/// <summary>
/// Session projector for script service AGUI frames in the unified Projection
/// Pipeline. It consumes EventEnvelope input directly and emits typed AGUI
/// session frames, including terminal run classification.
/// </summary>
public sealed class ScriptServiceAguiSessionEventProjector
    : ProjectionSessionEventProjectorBase<ScriptServiceAguiProjectionContext, AGUIEvent>
{
    public ScriptServiceAguiSessionEventProjector(
        IProjectionSessionEventHub<AGUIEvent> sessionEventHub)
        : base(sessionEventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<AGUIEvent>> ResolveSessionEventEntries(
        ScriptServiceAguiProjectionContext context,
        EventEnvelope envelope)
    {
        // Refactor (iter1/cluster-003):
        //   Old pattern: ScopeServiceEndpoints mapped script EventEnvelope payloads to AGUI and fabricated completion.
        //   New principle: The projection branch maps script envelopes to typed AGUI frames and owns terminal classification.
        if (string.IsNullOrWhiteSpace(context.RootActorId) || string.IsNullOrWhiteSpace(context.SessionId))
            return EmptyEntries;

        if (!string.Equals(envelope.Propagation?.CorrelationId, context.SessionId, StringComparison.Ordinal))
            return EmptyEntries;

        var mapped = ScopeGAgentAguiEventMapper.TryMap(envelope);
        if (mapped == null)
            return EmptyEntries;

        CompleteRunFinishedFrame(context, mapped);
        if (mapped.EventCase != AGUIEvent.EventOneofCase.TextMessageEnd)
        {
            return
            [
                new ProjectionSessionEventEntry<AGUIEvent>(
                    context.RootActorId,
                    context.SessionId,
                    mapped),
            ];
        }

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

    private static void CompleteRunFinishedFrame(
        ScriptServiceAguiProjectionContext context,
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
