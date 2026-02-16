using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Reducers;

public sealed class WorkflowSuspendedEventReducer : WorkflowExecutionEventReducerBase<WorkflowSuspendedEvent>
{
    public override int Order => 25;

    protected override void Reduce(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        WorkflowSuspendedEvent evt,
        DateTimeOffset now)
    {
        var step = WorkflowExecutionProjectionMutations.GetOrCreateStep(report, evt.StepId);
        step.CompletionMetadata["suspension_type"] = evt.SuspensionType;
        step.CompletionMetadata["suspension_prompt"] = evt.Prompt;
        step.CompletionMetadata["suspension_timeout"] = evt.TimeoutSeconds.ToString();

        var data = new Dictionary<string, string>
        {
            ["suspension_type"] = evt.SuspensionType,
            ["prompt"] = evt.Prompt,
            ["timeout_seconds"] = evt.TimeoutSeconds.ToString(),
        };

        WorkflowExecutionProjectionMutations.AddTimeline(
            report,
            now,
            "workflow.suspended",
            $"Workflow suspended at step {evt.StepId}: {evt.SuspensionType}",
            envelope.PublisherId,
            evt.StepId,
            evt.SuspensionType,
            envelope.Payload?.TypeUrl ?? "",
            data);
    }
}
