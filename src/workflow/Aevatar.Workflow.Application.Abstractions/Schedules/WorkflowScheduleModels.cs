namespace Aevatar.Workflow.Application.Abstractions.Schedules;

public sealed record WorkflowScheduleConfiguration(
    string ScheduleId,
    string DisplayName,
    string WorkflowName,
    string Prompt,
    string CronExpression,
    string Timezone,
    bool Enabled,
    IReadOnlyDictionary<string, string> Headers,
    string? ScopeId = null,
    string? TenantId = null,
    string? AppId = null,
    string? Namespace = null,
    string? ServiceId = null,
    string? RevisionId = null,
    WorkflowScheduleAuth? Auth = null);

public sealed record WorkflowScheduleNyxIdSubjectRef(
    string Platform,
    string Tenant,
    string ExternalUserId);

public sealed record WorkflowScheduleNyxIdCredentialSource(
    WorkflowScheduleNyxIdSubjectRef Subject,
    string Scope);

public sealed record WorkflowScheduleScopeOwnerNyxIdCredentialSource(string Scope);

public sealed record WorkflowScheduleAuth(
    WorkflowScheduleNyxIdCredentialSource? SenderNyxId = null,
    WorkflowScheduleScopeOwnerNyxIdCredentialSource? ScopeOwnerNyxId = null);

public sealed record WorkflowScheduleSummary(
    string ScheduleId,
    string DisplayName,
    string WorkflowName,
    string CronExpression,
    string Timezone,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextFireAt,
    DateTimeOffset? LastFireAt,
    string LastRunActorId,
    string LastCommandId,
    string LastCorrelationId,
    string LastError,
    int FireCount,
    int FailureCount,
    IReadOnlyDictionary<string, string> Headers,
    string ScopeId,
    string ScheduleActorId,
    string TargetActorId);

public sealed record WorkflowScheduleFireRecord(
    DateTimeOffset ScheduledFireAt,
    DateTimeOffset CompletedAt,
    string IdempotencyKey,
    string RunActorId,
    string CommandId,
    string CorrelationId,
    string Error,
    bool Manual);

public sealed record WorkflowScheduleDetail(
    WorkflowScheduleSummary Schedule,
    IReadOnlyList<WorkflowScheduleFireRecord> RecentFires);

public sealed record WorkflowScheduleMutationReceipt(
    string ScheduleId,
    string ScheduleActorId,
    bool Accepted,
    string CommandId,
    string CorrelationId,
    DateTimeOffset AckedAt,
    string AckStage);

public sealed record WorkflowScheduleRunNowReceipt(
    string ScheduleId,
    string ScheduleActorId,
    DateTimeOffset ScheduledFireAt,
    string IdempotencyKey,
    bool Accepted,
    string CommandId,
    string CorrelationId,
    DateTimeOffset AckedAt,
    string AckStage);

public sealed record WorkflowScheduleListResult(
    IReadOnlyList<WorkflowScheduleSummary> Items,
    string? NextCursor,
    long? TotalCount);

public sealed record WorkflowSchedulePreview(
    string CronExpression,
    string Timezone,
    IReadOnlyList<DateTimeOffset> NextFireTimes);

public interface IWorkflowScheduleApplicationService
{
    Task<WorkflowScheduleMutationReceipt> CreateAsync(
        WorkflowScheduleConfiguration configuration,
        CancellationToken ct = default);

    Task<WorkflowScheduleMutationReceipt> UpdateAsync(
        string scheduleId,
        WorkflowScheduleConfiguration configuration,
        CancellationToken ct = default);

    Task<WorkflowScheduleMutationReceipt> EnableAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default);

    Task<WorkflowScheduleMutationReceipt> DisableAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default);

    Task<WorkflowScheduleDetail?> GetAsync(
        string scheduleId,
        CancellationToken ct = default);

    Task<WorkflowScheduleListResult> ListAsync(
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default);

    Task<WorkflowSchedulePreview> PreviewAsync(
        string cronExpression,
        string? timezone,
        int count,
        DateTimeOffset? fromUtc = null,
        CancellationToken ct = default);

    Task<WorkflowScheduleRunNowReceipt> RunNowAsync(
        string scheduleId,
        CancellationToken ct = default);
}
