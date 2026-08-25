using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
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
        var receipt = await _scheduledDispatches.CreateAsync(
            WorkflowScheduleConfigurationMapper.ToScheduledDispatchConfiguration(configuration),
            WorkflowScheduleConfigurationMapper.ToScheduledDispatchMutationContext(configuration),
            ct);
        return ToWorkflowMutationReceipt(receipt);
    }

    public async Task<WorkflowScheduleMutationReceipt> UpdateAsync(
        string scheduleId,
        WorkflowScheduleConfiguration configuration,
        CancellationToken ct = default)
    {
        await EnsureWorkflowScheduleAsync(scheduleId, ct);
        var receipt = await _scheduledDispatches.UpdateAsync(
            scheduleId,
            WorkflowScheduleConfigurationMapper.ToScheduledDispatchConfiguration(configuration),
            WorkflowScheduleConfigurationMapper.ToScheduledDispatchMutationContext(configuration),
            ct);
        return ToWorkflowMutationReceipt(receipt);
    }

    public async Task<WorkflowScheduleMutationReceipt> EnableAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default)
    {
        await EnsureWorkflowScheduleAsync(scheduleId, ct);
        var receipt = await _scheduledDispatches.EnableAsync(scheduleId, reason, ct: ct);
        return ToWorkflowMutationReceipt(receipt);
    }

    public async Task<WorkflowScheduleMutationReceipt> DisableAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default)
    {
        await EnsureWorkflowScheduleAsync(scheduleId, ct);
        var receipt = await _scheduledDispatches.DisableAsync(scheduleId, reason, ct: ct);
        return ToWorkflowMutationReceipt(receipt);
    }

    public async Task<WorkflowScheduleMutationReceipt> DeleteAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default)
    {
        await EnsureWorkflowScheduleAsync(scheduleId, ct);
        var receipt = await _scheduledDispatches.DeleteAsync(scheduleId, reason, ct: ct);
        return ToWorkflowMutationReceipt(receipt);
    }

    public async Task<WorkflowScheduleDetail?> GetAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        var detail = await _scheduledDispatches.GetAsync(scheduleId, ct);
        return detail == null || !IsWorkflowCompatibilitySchedule(detail.Schedule) ? null : ToWorkflowDetail(detail);
    }

    public async Task<WorkflowScheduleListResult> ListAsync(
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default)
    {
        var result = await _scheduledDispatches.ListAsync(new ScheduledDispatchListQuery(
            take,
            cursor,
            includeTotalCount,
            ScheduleKind: ScheduledDispatchScheduleKind.Workflow), ct);
        return new WorkflowScheduleListResult(
            result.Items
                .Select(ToWorkflowSummary)
                .ToArray(),
            result.NextCursor,
            result.TotalCount);
    }

    public async Task<WorkflowSchedulePreview> PreviewAsync(
        string cronExpression,
        string? timezone,
        int count,
        DateTimeOffset? fromUtc = null,
        CancellationToken ct = default)
    {
        var preview = await _scheduledDispatches.PreviewAsync(cronExpression, timezone, count, fromUtc, ct);
        return new WorkflowSchedulePreview(preview.CronExpression, preview.Timezone, preview.NextFireTimes);
    }

    public async Task<WorkflowScheduleRunNowReceipt> RunNowAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        await EnsureWorkflowScheduleAsync(scheduleId, ct);
        var receipt = await _scheduledDispatches.RunNowAsync(scheduleId, ct: ct);
        return new WorkflowScheduleRunNowReceipt(
            receipt.ScheduleId,
            receipt.ScheduleActorId,
            receipt.ScheduledFireAt,
            receipt.IdempotencyKey,
            receipt.Accepted,
            receipt.CommandId,
            receipt.CorrelationId,
            receipt.AckedAt,
            receipt.AckStage);
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
            summary.ServiceId,
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
            ResolveScopeId(summary.ServiceKey),
            summary.ScheduleActorId,
            summary.TargetActorId,
            summary.Prompt,
            ToWorkflowScheduleMode(summary.ScheduleMode),
            summary.OneShotFireAt,
            summary.Completed);

    private async Task EnsureWorkflowScheduleAsync(string scheduleId, CancellationToken ct)
    {
        var detail = await _scheduledDispatches.GetAsync(scheduleId, ct);
        if (detail == null || !IsWorkflowCompatibilitySchedule(detail.Schedule))
            throw new ScheduledDispatchNotFoundException(scheduleId);
    }

    private static WorkflowScheduleMutationReceipt ToWorkflowMutationReceipt(ScheduledDispatchMutationReceipt receipt) =>
        new(
            receipt.ScheduleId,
            receipt.ScheduleActorId,
            receipt.Accepted,
            receipt.CommandId,
            receipt.CorrelationId,
            receipt.AckedAt,
            receipt.AckStage);

    private static bool IsWorkflowCompatibilitySchedule(ScheduledDispatchSummary summary) =>
        summary.ScheduleKind == ScheduledDispatchScheduleKind.Workflow;

    private static WorkflowScheduleMode ToWorkflowScheduleMode(ScheduledDispatchScheduleMode mode) =>
        mode == ScheduledDispatchScheduleMode.OneShotAtUtc
            ? WorkflowScheduleMode.OneShotAtUtc
            : WorkflowScheduleMode.RecurringCron;

    private static string ResolveScopeId(string serviceKey)
    {
        var parts = serviceKey.Split(':', StringSplitOptions.None);
        return parts.Length > 0 ? parts[0] : string.Empty;
    }

}
