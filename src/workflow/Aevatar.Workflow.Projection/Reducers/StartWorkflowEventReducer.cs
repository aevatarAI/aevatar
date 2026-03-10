using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Reducers;

public sealed class StartWorkflowEventReducer : WorkflowExecutionEventReducerBase<StartWorkflowEvent>
{
    protected override bool Reduce(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        StartWorkflowEvent evt,
        DateTimeOffset now)
    {
        report.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? report.WorkflowName : evt.WorkflowName;

        WorkflowExecutionProjectionMutations.AddTimeline(
            report,
            now,
            "workflow.start",
            $"command={report.CommandId}",
            envelope.Route?.PublisherActorId ?? string.Empty,
            null,
            null,
            envelope.Payload?.TypeUrl ?? "");

        return true;
    }
}
