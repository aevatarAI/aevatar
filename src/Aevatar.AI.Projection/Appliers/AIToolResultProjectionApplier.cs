using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Projection.ReadModels;

namespace Aevatar.AI.Projection.Appliers;

public sealed class AIToolResultProjectionApplier<TReadModel, TContext>
    : IProjectionEventApplier<TReadModel, TContext, ToolResultEvent>
    where TReadModel : class, IHasProjectionTimeline
{
    public bool Apply(
        TReadModel readModel,
        TContext context,
        EventEnvelope envelope,
        ToolResultEvent evt,
        DateTimeOffset now)
    {
        _ = context;
        var publisher = AIProjectionApplierHelpers.ResolvePublisher(envelope);
        readModel.AddTimeline(new ProjectionTimelineEvent
        {
            Timestamp = now,
            Stage = "tool.result",
            Message = $"agent={publisher}, call={evt.CallId ?? string.Empty}, success={evt.Success}",
            AgentId = publisher,
            EventType = AIProjectionApplierHelpers.ResolveEventType(envelope),
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["call_id"] = evt.CallId ?? string.Empty,
                ["success"] = evt.Success.ToString(),
                ["error"] = evt.Error ?? string.Empty,
            },
        });

        return true;
    }
}
