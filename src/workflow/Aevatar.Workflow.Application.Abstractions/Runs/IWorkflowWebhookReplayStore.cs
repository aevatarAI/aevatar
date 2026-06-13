namespace Aevatar.Workflow.Application.Abstractions.Runs;

public interface IWorkflowWebhookReplayStore
{
    ValueTask<WorkflowWebhookReplayAdmission> AdmitAsync(
        WorkflowWebhookReplayAdmissionRequest request,
        CancellationToken cancellationToken = default);

    ValueTask CompleteAsync(
        WorkflowWebhookReplayAdmissionRequest request,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(
        WorkflowWebhookReplayAdmissionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowWebhookReplayAdmissionRequest(
    string RouteKey,
    string SourceId,
    string DeliveryId,
    string PayloadFingerprint,
    DateTimeOffset ReceivedAt,
    string CommandId,
    string CorrelationId);

public sealed record WorkflowWebhookReplayAdmission(
    WorkflowWebhookReplayAdmissionStatus Status,
    string? ExistingCommandId = null,
    string? ExistingCorrelationId = null);

public enum WorkflowWebhookReplayAdmissionStatus
{
    Admitted = 0,
    DuplicateCompleted = 1,
    DuplicateInProgress = 2,
    PayloadConflict = 3,
    ExpiredRejected = 4,
}
