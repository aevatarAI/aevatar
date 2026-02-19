using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Appliers;

public sealed class WorkflowTextMessageEndEventApplier
    : IProjectionEventApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext, TextMessageEndEvent>
{
    public bool Apply(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        TextMessageEndEvent evt,
        DateTimeOffset now)
    {
        var publisher = string.IsNullOrWhiteSpace(envelope.PublisherId) ? "(unknown)" : envelope.PublisherId;
        if (!string.Equals(publisher, context.RootActorId, StringComparison.Ordinal))
        {
            report.AddRoleReply(new ProjectionRoleReply
            {
                Timestamp = now,
                RoleId = publisher,
                SessionId = evt.SessionId ?? string.Empty,
                Content = evt.Content ?? string.Empty,
                ContentLength = (evt.Content ?? string.Empty).Length,
            });
        }

        report.AddTimeline(new ProjectionTimelineEvent
        {
            Timestamp = now,
            Stage = "llm.end",
            Message = $"agent={publisher}, chars={(evt.Content ?? string.Empty).Length}",
            AgentId = publisher,
            EventType = envelope.Payload?.TypeUrl ?? string.Empty,
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["session_id"] = evt.SessionId ?? string.Empty,
            },
        });

        return true;
    }
}
