using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Projection.ReadModels;

namespace Aevatar.AI.Projection.Appliers;

public sealed class AITextMessageContentProjectionApplier<TReadModel, TContext>
    : IProjectionEventApplier<TReadModel, TContext, TextMessageContentEvent>
    where TReadModel : class, IHasProjectionTimeline
{
    public bool Apply(
        TReadModel readModel,
        TContext context,
        EventEnvelope envelope,
        TextMessageContentEvent evt,
        DateTimeOffset now)
    {
        _ = context;
        var publisher = AIProjectionApplierHelpers.ResolvePublisher(envelope);
        var delta = evt.Delta ?? string.Empty;
        readModel.AddTimeline(new ProjectionTimelineEvent
        {
            Timestamp = now,
            Stage = "llm.content",
            Message = $"agent={publisher}, chars={delta.Length}",
            AgentId = publisher,
            EventType = AIProjectionApplierHelpers.ResolveEventType(envelope),
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["session_id"] = evt.SessionId ?? string.Empty,
                ["delta_length"] = delta.Length.ToString(),
            },
        });

        return true;
    }
}
