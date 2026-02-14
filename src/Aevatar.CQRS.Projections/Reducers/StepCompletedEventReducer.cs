using Aevatar.CQRS.Projections.Abstractions.ReadModels;
using Aevatar.Workflows.Core;

namespace Aevatar.CQRS.Projections.Reducers;

public sealed class StepCompletedEventReducer : WorkflowExecutionEventReducerBase<StepCompletedEvent>
{
    public override int Order => 20;

    protected override void Reduce(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        StepCompletedEvent evt,
        DateTimeOffset now)
    {
        var step = WorkflowExecutionProjectionMutations.GetOrCreateStep(report, evt.StepId);
        if (string.IsNullOrWhiteSpace(step.RunId))
            step.RunId = evt.RunId;

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
            envelope.PublisherId,
            evt.StepId,
            step.StepType,
            envelope.Payload?.TypeUrl ?? "",
            step.CompletionMetadata);
    }
}
