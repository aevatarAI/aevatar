namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowExecutionStateModelExtensions
{
    public static ForEachItemResult ToForEachItemResult(
        this StepCompletedEvent evt,
        int index = -1,
        WorkflowFileRef? fileRef = null) =>
        new()
        {
            Success = evt.Success,
            Output = evt.Output ?? string.Empty,
            Error = evt.Error ?? string.Empty,
            Index = index,
            FileRef = fileRef?.Clone(),
            StepId = evt.StepId ?? string.Empty,
        };

    public static MapReduceItemResult ToMapReduceItemResult(this StepCompletedEvent evt) =>
        new()
        {
            Success = evt.Success,
            Output = evt.Output ?? string.Empty,
        };

    public static ParallelItemResult ToParallelItemResult(this StepCompletedEvent evt)
    {
        var result = new ParallelItemResult
        {
            Success = evt.Success,
            Output = evt.Output ?? string.Empty,
            Error = evt.Error ?? string.Empty,
            WorkerId = evt.WorkerId ?? string.Empty,
            NextStepId = evt.NextStepId ?? string.Empty,
            BranchKey = evt.BranchKey ?? string.Empty,
            AssignedVariable = evt.AssignedVariable ?? string.Empty,
            AssignedValue = evt.AssignedValue ?? string.Empty,
        };
        foreach (var (key, value) in evt.Annotations)
            result.Annotations[key] = value;

        return result;
    }
}
