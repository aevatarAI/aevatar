using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Reducers;

public sealed class WorkflowSuspendedEventReducer : WorkflowExecutionReportArtifactReducerBase<WorkflowSuspendedEvent>
{
    protected override bool Reduce(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        WorkflowSuspendedEvent evt,
        DateTimeOffset now)
    {
        var step = WorkflowExecutionReportArtifactMutations.GetOrCreateStep(report, evt.StepId);
        step.SuspensionType = evt.SuspensionType ?? string.Empty;
        step.SuspensionPrompt = evt.Prompt ?? string.Empty;
        step.SuspensionTimeoutSeconds = evt.TimeoutSeconds;
        step.RequestedVariableName = evt.VariableName ?? string.Empty;

        var data = new Dictionary<string, string>
        {
            ["suspension_type"] = evt.SuspensionType ?? string.Empty,
            ["prompt"] = evt.Prompt ?? string.Empty,
            ["timeout_seconds"] = evt.TimeoutSeconds.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(evt.VariableName))
            data["variable_name"] = evt.VariableName;

        WorkflowExecutionReportArtifactMutations.AddTimeline(
            report,
            now,
            "workflow.suspended",
            $"Workflow suspended at step {evt.StepId}: {evt.SuspensionType}",
            envelope.Route?.PublisherActorId ?? string.Empty,
            evt.StepId,
            evt.SuspensionType,
            envelope.Payload?.TypeUrl ?? "",
            data);

        return true;
    }
}
