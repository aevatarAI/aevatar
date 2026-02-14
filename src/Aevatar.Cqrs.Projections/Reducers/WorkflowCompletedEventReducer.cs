using Aevatar.Cqrs.Projections.Abstractions.ReadModels;
using Aevatar.Workflows.Core;

namespace Aevatar.Cqrs.Projections.Reducers;

public sealed class WorkflowCompletedEventReducer : ChatRunEventReducerBase<WorkflowCompletedEvent>
{
    public override int Order => 40;

    protected override void Reduce(
        ChatRunReport report,
        ChatProjectionContext context,
        EventEnvelope envelope,
        WorkflowCompletedEvent evt,
        DateTimeOffset now)
    {
        report.Success = evt.Success;
        report.FinalOutput = evt.Output ?? "";
        report.FinalError = evt.Error ?? "";
        report.EndedAt = now;

        ChatRunProjectionMutations.AddTimeline(
            report,
            now,
            "workflow.completed",
            $"success={evt.Success}",
            envelope.PublisherId,
            null,
            null,
            envelope.Payload?.TypeUrl ?? "",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflow_name"] = evt.WorkflowName,
                ["run_id"] = evt.RunId,
            });
    }
}
