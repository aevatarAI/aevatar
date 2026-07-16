namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record WorkflowChatHistoryTerminalDeliveryReservationRequest(
    string DeliveryId,
    string ScopeId,
    string ConversationId,
    string TurnId,
    string UserText,
    string WorkflowActorId,
    string WorkflowCommandId,
    string WorkflowCorrelationId);

public sealed record WorkflowChatHistoryTerminalDeliveryReservation(
    string DeliveryId,
    string WorkflowActorId,
    string WorkflowCommandId);

public interface IWorkflowChatHistoryTerminalDeliveryPort
{
    Task<WorkflowChatHistoryTerminalDeliveryReservation?> ReserveAsync(
        WorkflowChatHistoryTerminalDeliveryReservationRequest request,
        CancellationToken ct = default);

    Task BindAcceptedAsync(
        WorkflowChatHistoryTerminalDeliveryReservation reservation,
        WorkflowChatRunAcceptedReceipt receipt,
        CancellationToken ct = default);

    Task AbandonAsync(
        WorkflowChatHistoryTerminalDeliveryReservation reservation,
        string reason,
        CancellationToken ct = default);
}
