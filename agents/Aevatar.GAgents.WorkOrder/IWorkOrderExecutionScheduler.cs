namespace Aevatar.GAgents.WorkOrder;

public interface IWorkOrderExecutionScheduler
{
    ValueTask ScheduleAsync(
        WorkOrderExecutionRequest request,
        CancellationToken ct = default);
}

public sealed class WorkOrderExecutionQueueFullException(string message) : Exception(message);
