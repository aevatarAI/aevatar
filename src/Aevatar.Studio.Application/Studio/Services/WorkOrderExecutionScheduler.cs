using Aevatar.GAgents.WorkOrder;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class WorkOrderExecutionScheduler(IWorkOrderExecutionQueue queue)
    : IWorkOrderExecutionScheduler
{
    private readonly IWorkOrderExecutionQueue _queue =
        queue ?? throw new ArgumentNullException(nameof(queue));

    public ValueTask ScheduleAsync(
        WorkOrderExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        _queue.Enqueue(request);
        return ValueTask.CompletedTask;
    }
}
