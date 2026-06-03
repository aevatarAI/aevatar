using Aevatar.Workflow.Application.Abstractions.Schedules;

namespace Aevatar.Workflow.Application.Schedules;

public sealed class WorkflowScheduleApplicationService : IWorkflowScheduleApplicationService
{
    private readonly IScheduledDispatchApplicationService _scheduledDispatches;

    public WorkflowScheduleApplicationService(IScheduledDispatchApplicationService scheduledDispatches)
    {
        _scheduledDispatches = scheduledDispatches ?? throw new ArgumentNullException(nameof(scheduledDispatches));
    }

    public async Task<WorkflowScheduleMutationReceipt> CreateAsync(
        WorkflowScheduleConfiguration configuration,
        CancellationToken ct = default)
    {
        var receipt = await _scheduledDispatches.CreateAsync(ToScheduledDispatchConfiguration(configuration), ct);
        return new WorkflowScheduleMutationReceipt(receipt.ScheduleId, receipt.ScheduleActorId, receipt.Accepted);
    }

    public async Task<WorkflowScheduleMutationReceipt> UpdateAsync(
        string scheduleId,
        WorkflowScheduleConfiguration configuration,
        CancellationToken ct = default)
    {
        var receipt = await _scheduledDispatches.UpdateAsync(scheduleId, ToScheduledDispatchConfiguration(configuration), ct);
        return new WorkflowScheduleMutationReceipt(receipt.ScheduleId, receipt.ScheduleActorId, receipt.Accepted);
    }

    public async Task<WorkflowScheduleMutationReceipt> EnableAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default)
    {
        var receipt = await _scheduledDispatches.EnableAsync(scheduleId, reason, ct);
        return new WorkflowScheduleMutationReceipt(receipt.ScheduleId, receipt.ScheduleActorId, receipt.Accepted);
    }

    public async Task<WorkflowScheduleMutationReceipt> DisableAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default)
    {
        var receipt = await _scheduledDispatches.DisableAsync(scheduleId, reason, ct);
        return new WorkflowScheduleMutationReceipt(receipt.ScheduleId, receipt.ScheduleActorId, receipt.Accepted);
    }

    public async Task<WorkflowScheduleDetail?> GetAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        var detail = await _scheduledDispatches.GetAsync(scheduleId, ct);
        return detail == null ? null : ToWorkflowDetail(detail);
    }

    public async Task<WorkflowScheduleListResult> ListAsync(
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default)
    {
        var result = await _scheduledDispatches.ListAsync(take, cursor, includeTotalCount, ct);
        return new WorkflowScheduleListResult(
            result.Items
                .Where(static x => x.TargetKind == ScheduledDispatchTargetKind.Workflow)
                .Select(ToWorkflowSummary)
                .ToArray(),
            result.NextCursor,
            result.TotalCount);
    }

    public Task<ScheduledDispatchPreview> PreviewAsync(
        string cronExpression,
        string? timezone,
        int count,
        DateTimeOffset? fromUtc = null,
        CancellationToken ct = default) =>
        _scheduledDispatches.PreviewAsync(cronExpression, timezone, count, fromUtc, ct);

    public async Task<WorkflowScheduleRunNowReceipt> RunNowAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        var receipt = await _scheduledDispatches.RunNowAsync(scheduleId, ct);
        return new WorkflowScheduleRunNowReceipt(
            receipt.ScheduleId,
            receipt.ScheduleActorId,
            receipt.ScheduledFireAt,
            receipt.IdempotencyKey,
            receipt.Accepted);
    }

    private static ScheduledDispatchConfiguration ToScheduledDispatchConfiguration(
        WorkflowScheduleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new ScheduledDispatchConfiguration(
            configuration.ScheduleId,
            configuration.DisplayName,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.Workflow,
                Workflow: new WorkflowScheduleTargetDescriptor(
                    configuration.WorkflowName,
                    configuration.Prompt,
                    configuration.ScopeId ?? string.Empty,
                    configuration.SourceActorId ?? string.Empty)),
            configuration.CronExpression,
            configuration.Timezone,
            configuration.Enabled,
            configuration.Headers);
    }

    private static WorkflowScheduleDetail ToWorkflowDetail(ScheduledDispatchDetail detail) =>
        new(
            ToWorkflowSummary(detail.Schedule),
            detail.RecentFires.Select(static x => new WorkflowScheduleFireRecord(
                x.ScheduledFireAt,
                x.CompletedAt,
                x.IdempotencyKey,
                x.TargetActorId,
                x.CommandId,
                x.CorrelationId,
                x.Error,
                x.Manual)).ToArray());

    private static WorkflowScheduleSummary ToWorkflowSummary(ScheduledDispatchSummary summary) =>
        new(
            summary.ScheduleId,
            summary.DisplayName,
            summary.WorkflowName,
            summary.CronExpression,
            summary.Timezone,
            summary.Enabled,
            summary.CreatedAt,
            summary.UpdatedAt,
            summary.NextFireAt,
            summary.LastFireAt,
            summary.LastTargetActorId,
            summary.LastCommandId,
            summary.LastCorrelationId,
            summary.LastError,
            summary.FireCount,
            summary.FailureCount,
            summary.Headers,
            string.Empty,
            string.Empty,
            summary.ScheduleActorId,
            summary.TargetActorId);
}
