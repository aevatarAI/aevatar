namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowExecutionStateModelExtensions
{
    public static ForEachItemResult ToForEachItemResult(this StepCompletedEvent evt) =>
        new()
        {
            Success = evt.Success,
            Output = evt.Output ?? string.Empty,
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
        };
        foreach (var (key, value) in evt.Metadata)
            result.Metadata[key] = value;

        return result;
    }
}
