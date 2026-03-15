using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Reducers;

public sealed class StepRequestEventReducer : WorkflowExecutionReportArtifactReducerBase<StepRequestEvent>
{
    protected override bool Reduce(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        StepRequestEvent evt,
        DateTimeOffset now)
    {
        var step = WorkflowExecutionReportArtifactMutations.GetOrCreateStep(report, evt.StepId);
        step.StepType = evt.StepType;
        step.TargetRole = evt.TargetRole;
        step.RequestedAt = now;
        step.RequestParameters = evt.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value);

        WorkflowExecutionReportArtifactMutations.AddTimeline(
            report,
            now,
            "step.request",
            $"{evt.StepId} ({evt.StepType})",
            envelope.Route?.PublisherActorId ?? string.Empty,
            evt.StepId,
            evt.StepType,
            envelope.Payload?.TypeUrl ?? "",
            step.RequestParameters);

        return true;
    }
}
