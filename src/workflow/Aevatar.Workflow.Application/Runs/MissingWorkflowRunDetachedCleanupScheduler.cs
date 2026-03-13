using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class MissingWorkflowRunDetachedCleanupScheduler
    : IWorkflowRunDetachedCleanupScheduler
{
    public Task ScheduleAsync(
        WorkflowRunDetachedCleanupRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            "Workflow detached cleanup scheduler is not registered. Register workflow projection services before using detached workflow dispatch.");
    }
}
