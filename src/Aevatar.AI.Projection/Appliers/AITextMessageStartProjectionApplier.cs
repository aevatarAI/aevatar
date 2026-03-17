using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Projection.ReadModels;

namespace Aevatar.AI.Projection.Appliers;

public sealed class AITextMessageStartProjectionApplier<TReadModel, TContext>
    : IProjectionEventApplier<TReadModel, TContext, TextMessageStartEvent>
    where TReadModel : class, IHasProjectionTimeline
    where TContext : class, IProjectionMaterializationContext
{
    public bool Apply(
        TReadModel readModel,
        TContext context,
        EventEnvelope envelope,
        TextMessageStartEvent evt,
        DateTimeOffset now)
    {
        _ = context;
        var publisher = AIProjectionApplierHelpers.ResolvePublisher(envelope);
        readModel.AddTimeline(new ProjectionTimelineEvent
        {
            Timestamp = now,
            Stage = "llm.start",
            Message = $"agent={publisher}, session={evt.SessionId ?? string.Empty}",
            AgentId = publisher,
            EventType = AIProjectionApplierHelpers.ResolveEventType(envelope),
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["session_id"] = evt.SessionId ?? string.Empty,
            },
        });

        return true;
    }
}
