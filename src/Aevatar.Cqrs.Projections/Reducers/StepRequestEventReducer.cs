using Aevatar.Cqrs.Projections.Abstractions.ReadModels;
using Aevatar.Workflows.Core;

namespace Aevatar.Cqrs.Projections.Reducers;

public sealed class StepRequestEventReducer : ChatRunEventReducerBase<StepRequestEvent>
{
    public override int Order => 10;

    protected override void Reduce(
        ChatRunReport report,
        ChatProjectionContext context,
        EventEnvelope envelope,
        StepRequestEvent evt,
        DateTimeOffset now)
    {
        var step = ChatRunProjectionMutations.GetOrCreateStep(report, evt.StepId);
        step.StepType = evt.StepType;
        step.RunId = evt.RunId;
        step.TargetRole = evt.TargetRole;
        step.RequestedAt = now;
        step.RequestParameters = evt.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value);

        ChatRunProjectionMutations.AddTimeline(
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
