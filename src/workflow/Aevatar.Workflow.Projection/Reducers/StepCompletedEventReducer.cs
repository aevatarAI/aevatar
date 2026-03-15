using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Reducers;

public sealed class StepCompletedEventReducer : WorkflowExecutionReportArtifactReducerBase<StepCompletedEvent>
{
    protected override bool Reduce(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        StepCompletedEvent evt,
        DateTimeOffset now)
    {
        var step = WorkflowExecutionReportArtifactMutations.GetOrCreateStep(report, evt.StepId);
        step.CompletedAt = now;
        step.Success = evt.Success;
        step.Error = evt.Error ?? "";
        step.WorkerId = evt.WorkerId ?? "";
        step.OutputPreview = WorkflowExecutionReportArtifactMutations.Truncate(evt.Output ?? "", 240);
        step.CompletionAnnotations = evt.Annotations.ToDictionary(kv => kv.Key, kv => kv.Value);
        step.NextStepId = evt.NextStepId ?? string.Empty;
        step.BranchKey = evt.BranchKey ?? string.Empty;
        step.AssignedVariable = evt.AssignedVariable ?? string.Empty;
        step.AssignedValue = evt.AssignedValue ?? string.Empty;

        WorkflowExecutionReportArtifactMutations.AddTimeline(
            report,
            now,
            "step.completed",
            $"{evt.StepId} success={evt.Success}",
            envelope.Route?.PublisherActorId ?? string.Empty,
            evt.StepId,
            step.StepType,
            envelope.Payload?.TypeUrl ?? "",
            step.CompletionAnnotations);

        return true;
    }
}
