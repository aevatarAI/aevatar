namespace Aevatar.GAgents.WorkOrder;

/// <summary>
/// Narrow application boundary used by the WorkOrder actor after it commits
/// dispatch authorization. Implementations revalidate current Team facts and
/// return only an accepted Run receipt or a typed terminal failure.
/// </summary>
public interface IWorkOrderExecutionPort
{
    Task<WorkOrderExecutionResult> ExecuteAsync(
        WorkOrderExecutionRequest request,
        CancellationToken ct = default);
}
