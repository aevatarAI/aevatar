using Aevatar.Cqrs.Projections.Abstractions.ReadModels;
using Aevatar.Workflows.Core;

namespace Aevatar.Cqrs.Projections.Reducers;

public sealed class StepCompletedEventReducer : ChatRunEventReducerBase<StepCompletedEvent>
{
    public override int Order => 20;

    protected override void Reduce(
        ChatRunReport report,
        ChatProjectionContext context,
        EventEnvelope envelope,
        StepCompletedEvent evt,
        DateTimeOffset now)
    {
        var step = ChatRunProjectionMutations.GetOrCreateStep(report, evt.StepId);
        if (string.IsNullOrWhiteSpace(step.RunId))
            step.RunId = evt.RunId;

        step.CompletedAt = now;
        step.Success = evt.Success;
        step.Error = evt.Error ?? "";
        step.WorkerId = evt.WorkerId ?? "";
        step.OutputPreview = ChatRunProjectionMutations.Truncate(evt.Output ?? "", 240);
        step.CompletionMetadata = evt.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value);

        ChatRunProjectionMutations.AddTimeline(
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
