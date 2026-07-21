namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record WorkflowChatHistoryTerminalDeliveryReservationRequest(
    string DeliveryId,
    string ScopeId,
    WorkflowChatConversationIntent Conversation,
    string UserText,
    string WorkflowActorId,
    string WorkflowCommandId,
    string WorkflowCorrelationId);

public sealed record WorkflowChatHistoryTerminalDeliveryReservation(
    string DeliveryActorId,
    string DeliveryId,
    string WorkflowActorId,
    string WorkflowCommandId);

public enum WorkflowChatHistoryTerminalDeliveryReservationFailure
{
    None = 0,
    ConversationNotFound = 1,
    Unavailable = 2,
    IdempotencyConflict = 3,
}

public sealed record WorkflowChatHistoryTerminalDeliveryReservationResult(
    WorkflowChatHistoryTerminalDeliveryReservation? Reservation,
    WorkflowChatContext? ChatContext,
    WorkflowChatHistoryTerminalDeliveryReservationFailure Failure,
    bool Replayed = false)
{
    public bool Succeeded =>
        Failure == WorkflowChatHistoryTerminalDeliveryReservationFailure.None &&
        Reservation != null &&
        ChatContext != null;

    public static WorkflowChatHistoryTerminalDeliveryReservationResult Success(
        WorkflowChatHistoryTerminalDeliveryReservation reservation,
        WorkflowChatContext chatContext,
        bool replayed = false) =>
        new(reservation, chatContext, WorkflowChatHistoryTerminalDeliveryReservationFailure.None, replayed);

    public static WorkflowChatHistoryTerminalDeliveryReservationResult NotFound() =>
        new(null, null, WorkflowChatHistoryTerminalDeliveryReservationFailure.ConversationNotFound);

    public static WorkflowChatHistoryTerminalDeliveryReservationResult Unavailable() =>
        new(null, null, WorkflowChatHistoryTerminalDeliveryReservationFailure.Unavailable);

    public static WorkflowChatHistoryTerminalDeliveryReservationResult IdempotencyConflict() =>
        new(null, null, WorkflowChatHistoryTerminalDeliveryReservationFailure.IdempotencyConflict);
}

public interface IWorkflowChatHistoryTerminalDeliveryPort
{
    Task<WorkflowChatHistoryTerminalDeliveryReservationResult> ReserveAsync(
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
