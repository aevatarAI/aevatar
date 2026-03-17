using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Projection.ReadModels;

namespace Aevatar.AI.Projection.Appliers;

public sealed class AIToolCallProjectionApplier<TReadModel, TContext>
    : IProjectionEventApplier<TReadModel, TContext, ToolCallEvent>
    where TReadModel : class, IHasProjectionTimeline
    where TContext : class, IProjectionMaterializationContext
{
    public bool Apply(
        TReadModel readModel,
        TContext context,
        EventEnvelope envelope,
        ToolCallEvent evt,
        DateTimeOffset now)
    {
        _ = context;
        var publisher = AIProjectionApplierHelpers.ResolvePublisher(envelope);
        readModel.AddTimeline(new ProjectionTimelineEvent
        {
            Timestamp = now,
            Stage = "tool.call",
            Message = $"agent={publisher}, tool={evt.ToolName ?? string.Empty}",
            AgentId = publisher,
            EventType = AIProjectionApplierHelpers.ResolveEventType(envelope),
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tool_name"] = evt.ToolName ?? string.Empty,
                ["call_id"] = evt.CallId ?? string.Empty,
            },
        });

        return true;
    }
}
