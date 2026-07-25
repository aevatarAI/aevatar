using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Pure current-state WorkOrder read-model query contract.
/// </summary>
public interface IWorkOrderQueryPort
{
    Task<WorkOrderListResponse> ListAsync(
        string scopeId,
        WorkOrderQueryRequest query,
        CancellationToken ct = default);

    Task<WorkOrderCurrentStateResponse?> GetAsync(
        string scopeId,
        string workOrderId,
        CancellationToken ct = default);
}
