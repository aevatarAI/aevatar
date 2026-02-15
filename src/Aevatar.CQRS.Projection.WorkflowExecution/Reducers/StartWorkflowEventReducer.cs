using Aevatar.CQRS.Projection.WorkflowExecution.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.CQRS.Projection.WorkflowExecution.Reducers;

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
            $"run={evt.RunId}",
            envelope.PublisherId,
            null,
            null,
            envelope.Payload?.TypeUrl ?? "");
    }
}
