using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IWorkOrderService
{
    Task<WorkOrderAcceptedReceipt> CreateAsync(
        string scopeId,
        CreateWorkOrderRequest request,
        WorkOrderPrincipalContract requester,
        CancellationToken ct = default);

    Task<WorkOrderListResponse> ListAsync(
        string scopeId,
        WorkOrderQueryRequest query,
        CancellationToken ct = default);

    Task<WorkOrderCurrentStateResponse> GetAsync(
        string scopeId,
        string workOrderId,
        CancellationToken ct = default);

    Task<WorkOrderAcceptedReceipt> ReassignAsync(
        string scopeId,
        string workOrderId,
        ReassignWorkOrderRequest request,
        WorkOrderPrincipalContract requester,
        CancellationToken ct = default);

    Task<WorkOrderAcceptedReceipt> DispatchAsync(
        string scopeId,
        string workOrderId,
        DispatchWorkOrderRequest request,
        WorkOrderPrincipalContract requester,
        CancellationToken ct = default);

    Task<WorkOrderAcceptedReceipt> CancelAsync(
        string scopeId,
        string workOrderId,
        CancelWorkOrderRequest request,
        WorkOrderPrincipalContract requester,
        CancellationToken ct = default);
}
