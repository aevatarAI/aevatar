using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Reducers;

public sealed class StepCompletedEventReducer : WorkflowExecutionEventReducerBase<StepCompletedEvent>
{
    protected override bool Reduce(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        StepCompletedEvent evt,
        DateTimeOffset now)
    {
        var step = WorkflowExecutionProjectionMutations.GetOrCreateStep(report, evt.StepId);
        step.CompletedAt = now;
        step.Success = evt.Success;
        step.Error = evt.Error ?? "";
        step.WorkerId = evt.WorkerId ?? "";
        step.OutputPreview = WorkflowExecutionProjectionMutations.Truncate(evt.Output ?? "", 240);
        step.CompletionMetadata = evt.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value);

        WorkflowExecutionProjectionMutations.AddTimeline(
            report,
            now,
            "step.completed",
            $"{evt.StepId} success={evt.Success}",
            envelope.Route?.PublisherActorId ?? string.Empty,
            evt.StepId,
            step.StepType,
            envelope.Payload?.TypeUrl ?? "",
            step.CompletionMetadata);

        return true;
    }
}
