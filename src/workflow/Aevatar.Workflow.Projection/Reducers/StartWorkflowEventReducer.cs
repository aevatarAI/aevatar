using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Reducers;

public sealed class StartWorkflowEventReducer : WorkflowExecutionEventReducerBase<StartWorkflowEvent>
{
    public override int Order => 0;

    protected override void Reduce(
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
            envelope.PublisherId,
            null,
            null,
            envelope.Payload?.TypeUrl ?? "");
    }
}
