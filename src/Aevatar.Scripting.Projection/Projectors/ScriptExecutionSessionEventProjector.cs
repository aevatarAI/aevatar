using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptExecutionSessionEventProjector
    : ProjectionSessionEventProjectorBase<ScriptExecutionProjectionContext, EventEnvelope>
{
    public ScriptExecutionSessionEventProjector(
        IProjectionSessionEventHub<EventEnvelope> sessionEventHub)
        : base(sessionEventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<EventEnvelope>> ResolveSessionEventEntries(
        ScriptExecutionProjectionContext context,
        EventEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(context.RootActorId) || string.IsNullOrWhiteSpace(context.SessionId))
            return EmptyEntries;

        if (!IsLegacyActorScopedSession(context) &&
            !string.Equals(envelope.Propagation?.CorrelationId, context.SessionId, StringComparison.Ordinal))
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

    private static bool IsLegacyActorScopedSession(ScriptExecutionProjectionContext context) =>
        string.Equals(context.RootActorId, context.SessionId, StringComparison.Ordinal);
}
