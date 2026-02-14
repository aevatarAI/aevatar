using Aevatar.CQRS.Projections.Abstractions.ReadModels;
using Aevatar.Workflows.Core;

namespace Aevatar.CQRS.Projections.Reducers;

public sealed class StepRequestEventReducer : WorkflowExecutionEventReducerBase<StepRequestEvent>
{
    public override int Order => 10;

    protected override void Reduce(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        StepRequestEvent evt,
        DateTimeOffset now)
    {
        var step = WorkflowExecutionProjectionMutations.GetOrCreateStep(report, evt.StepId);
        step.StepType = evt.StepType;
        step.RunId = evt.RunId;
        step.TargetRole = evt.TargetRole;
        step.RequestedAt = now;
        step.RequestParameters = evt.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value);

        WorkflowExecutionProjectionMutations.AddTimeline(
            report,
            now,
            "step.request",
            $"{evt.StepId} ({evt.StepType})",
            envelope.PublisherId,
            evt.StepId,
            evt.StepType,
            envelope.Payload?.TypeUrl ?? "",
            step.RequestParameters);
    }
}
