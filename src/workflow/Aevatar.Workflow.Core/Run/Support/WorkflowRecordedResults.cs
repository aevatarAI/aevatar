using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core;

internal static class WorkflowRecordedResults
{
    public static WorkflowRecordedStepResult ToRecordedResult(StepCompletedEvent evt)
    {
        var recorded = new WorkflowRecordedStepResult
        {
            StepId = evt.StepId,
            Success = evt.Success,
            Output = evt.Output ?? string.Empty,
            Error = evt.Error ?? string.Empty,
            WorkerId = evt.WorkerId ?? string.Empty,
        };
        foreach (var (key, value) in evt.Metadata)
            recorded.Metadata[key] = value;
        return recorded;
    }
}
