namespace Aevatar.Workflow.Application.Runs;

internal static class WorkflowRunTaskAwaiter
{
    public static async Task AwaitIgnoringCancellationAsync(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
