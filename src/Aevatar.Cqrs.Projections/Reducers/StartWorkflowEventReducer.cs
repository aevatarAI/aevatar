using Aevatar.Cqrs.Projections.Abstractions.ReadModels;
using Aevatar.Workflows.Core;

namespace Aevatar.Cqrs.Projections.Reducers;

public sealed class StartWorkflowEventReducer : ChatRunEventReducerBase<StartWorkflowEvent>
{
    public override int Order => 0;

    protected override void Reduce(
        ChatRunReport report,
        ChatProjectionContext context,
        EventEnvelope envelope,
        StartWorkflowEvent evt,
        DateTimeOffset now)
    {
        report.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? report.WorkflowName : evt.WorkflowName;

        ChatRunProjectionMutations.AddTimeline(
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
