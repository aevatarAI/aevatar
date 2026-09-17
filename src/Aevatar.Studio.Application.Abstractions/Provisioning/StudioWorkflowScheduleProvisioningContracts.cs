using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;

namespace Aevatar.Studio.Application.Provisioning;

public static class StudioWorkflowScheduleProvisioningStatusNames
{
    public const string PendingBinding = "pending_binding";
    public const string Provisioning = "provisioning";
    public const string RetryPending = "retry_pending";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>
/// Durable, secret-free intent for creating or replacing the deterministic
/// schedule owned by one provisioned workflow member. The verified binding
/// identity is sufficient to re-issue a short-lived provisioning token later;
/// raw bearer tokens must never be stored with this intent.
/// </summary>
public sealed record StudioWorkflowScheduleProvisioningIntent(
    string ProvisioningId,
    string ScopeId,
    string TeamId,
    string MemberId,
    string PublishedServiceId,
    string WorkflowId,
    string RevisionId,
    string DisplayName,
    string Prompt,
    AuthenticatedAuthorizationOwnerContext AuthenticatedOwner,
    ScheduledDispatchScheduleMode ScheduleMode,
    string CronExpression,
    string Timezone,
    int OneShotDelaySeconds,
    string? BindingRunId = null,
    string? ScheduleOperationId = null,
    string? ScheduleIdempotencyKey = null);

public sealed record StudioWorkflowScheduleProvisioningExecution(
    StudioWorkflowScheduleProvisioningIntent Intent,
    DateTimeOffset? OneShotFireAt);

public sealed record StudioWorkflowScheduleProvisioningAcceptance(
    bool Accepted,
    string ProvisioningId,
    string Status,
    string? ScheduleId = null);

public sealed record StudioWorkflowScheduleProvisioningExecutionResult(
    bool Success,
    bool Retryable,
    string? ScheduleId,
    string? OperationId,
    string FailureCode,
    string Detail)
{
    public static StudioWorkflowScheduleProvisioningExecutionResult Succeeded(
        string scheduleId,
        string? operationId) =>
        new(true, false, scheduleId, operationId, string.Empty, string.Empty);

    public static StudioWorkflowScheduleProvisioningExecutionResult Retry(
        string failureCode,
        string detail) =>
        new(false, true, null, null, failureCode, detail);

    public static StudioWorkflowScheduleProvisioningExecutionResult Failed(
        string failureCode,
        string detail) =>
        new(false, false, null, null, failureCode, detail);
}

public interface IStudioWorkflowScheduleProvisioningCommandPort
{
    Task<StudioWorkflowScheduleProvisioningAcceptance> AcceptAsync(
        StudioWorkflowScheduleProvisioningIntent intent,
        CancellationToken ct = default);
}

public interface IStudioWorkflowScheduleProvisioningExecutor
{
    Task<StudioWorkflowScheduleProvisioningExecutionResult> ExecuteAsync(
        StudioWorkflowScheduleProvisioningExecution execution,
        CancellationToken ct = default);
}
