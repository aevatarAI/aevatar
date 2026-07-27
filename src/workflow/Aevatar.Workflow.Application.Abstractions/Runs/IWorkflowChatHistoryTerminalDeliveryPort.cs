namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record WorkflowChatHistoryTerminalDeliveryReservationRequest(
    string DeliveryId,
    string ScopeId,
    WorkflowChatConversationIntent Conversation,
    string UserText,
    string WorkflowActorId,
    string WorkflowCommandId,
    string WorkflowCorrelationId,
    string RequestFingerprint = "");

public sealed record WorkflowChatHistoryTerminalDeliveryReservation(
    string DeliveryActorId,
    string DeliveryId,
    string WorkflowActorId,
    string WorkflowCommandId,
    bool ExistingReservation = false);

public enum WorkflowChatHistoryTerminalDeliveryReservationFailure
{
    None = 0,
    ConversationNotFound = 1,
    Unavailable = 2,
}

public sealed record WorkflowChatHistoryTerminalDeliveryReservationResult(
    WorkflowChatHistoryTerminalDeliveryReservation? Reservation,
    WorkflowChatContext? ChatContext,
    WorkflowConversationExecutionContext? ConversationContext,
    WorkflowChatHistoryTerminalDeliveryReservationFailure Failure)
{
    public bool Succeeded =>
        Failure == WorkflowChatHistoryTerminalDeliveryReservationFailure.None &&
        Reservation != null &&
        ChatContext != null;

    public static WorkflowChatHistoryTerminalDeliveryReservationResult Success(
        WorkflowChatHistoryTerminalDeliveryReservation reservation,
        WorkflowChatContext chatContext,
        WorkflowConversationExecutionContext? conversationContext = null) =>
        new(reservation, chatContext, conversationContext, WorkflowChatHistoryTerminalDeliveryReservationFailure.None);

    public static WorkflowChatHistoryTerminalDeliveryReservationResult NotFound() =>
        new(null, null, null, WorkflowChatHistoryTerminalDeliveryReservationFailure.ConversationNotFound);

    public static WorkflowChatHistoryTerminalDeliveryReservationResult Unavailable() =>
        new(null, null, null, WorkflowChatHistoryTerminalDeliveryReservationFailure.Unavailable);
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
