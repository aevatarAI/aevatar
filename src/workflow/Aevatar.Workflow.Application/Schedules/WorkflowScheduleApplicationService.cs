using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Schedules;

public sealed class WorkflowScheduleApplicationService : IWorkflowScheduleApplicationService
{
    private const string WorkflowScheduleHeader = "workflow.schedule";
    private const string WorkflowNameHeader = WorkflowScheduleHeader + ".workflow_name";
    private const string ScopeIdHeader = WorkflowScheduleHeader + ".scope_id";
    private const string SourceActorIdHeader = WorkflowScheduleHeader + ".source_actor_id";

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
                .Where(static x => IsWorkflowCompatibilitySchedule(x.Headers))
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
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    BuildWorkflowServiceIdentity(configuration),
                    "chat",
                    Any.Pack(BuildWorkflowChatRequest(configuration)),
                    configuration.RevisionId)),
            configuration.CronExpression,
            configuration.Timezone,
            configuration.Enabled,
            BuildWorkflowScheduleHeaders(configuration));
    }

    private static ServiceIdentity BuildWorkflowServiceIdentity(WorkflowScheduleConfiguration configuration)
    {
        var scopeId = NormalizeRequired(FirstNonBlank(configuration.ScopeId, configuration.TenantId), nameof(configuration.ScopeId));
        var serviceId = NormalizeRequired(FirstNonBlank(configuration.ServiceId, configuration.WorkflowName), nameof(configuration.ServiceId));
        return new ServiceIdentity
        {
            TenantId = scopeId,
            AppId = NormalizeOptional(configuration.AppId, ScopeServiceIdentityDefaults.ServiceAppId),
            Namespace = NormalizeOptional(configuration.Namespace, ScopeServiceIdentityDefaults.ServiceNamespace),
            ServiceId = serviceId,
        };
    }

    private static ChatRequestEvent BuildWorkflowChatRequest(WorkflowScheduleConfiguration configuration)
    {
        var request = new ChatRequestEvent
        {
            Prompt = NormalizeRequired(configuration.Prompt, nameof(configuration.Prompt)),
        };

        foreach (var (key, value) in BuildWorkflowScheduleHeaders(configuration))
            request.Metadata[key] = value;

        return request;
    }

    private static IReadOnlyDictionary<string, string> BuildWorkflowScheduleHeaders(
        WorkflowScheduleConfiguration configuration)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in configuration.Headers ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            var normalizedKey = key.Trim();
            if (IsWorkflowScheduleHeader(normalizedKey))
                continue;

            headers[normalizedKey] = value.Trim();
        }

        headers[WorkflowNameHeader] = NormalizeRequired(configuration.WorkflowName, nameof(configuration.WorkflowName));
        var scopeId = NormalizeOptional(FirstNonBlank(configuration.ScopeId, configuration.TenantId), string.Empty);
        if (!string.IsNullOrWhiteSpace(scopeId))
            headers[ScopeIdHeader] = scopeId;
        var sourceActorId = NormalizeOptional(configuration.SourceActorId, string.Empty);
        if (!string.IsNullOrWhiteSpace(sourceActorId))
            headers[SourceActorIdHeader] = sourceActorId;

        return headers;
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
            ResolveHeader(summary.Headers, WorkflowNameHeader, summary.ServiceId),
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
            ResolveHeader(summary.Headers, ScopeIdHeader, string.Empty),
            ResolveHeader(summary.Headers, SourceActorIdHeader, string.Empty),
            summary.ScheduleActorId,
            summary.TargetActorId);

    private static bool IsWorkflowCompatibilitySchedule(IReadOnlyDictionary<string, string> headers) =>
        headers.ContainsKey(WorkflowNameHeader);

    private static bool IsWorkflowScheduleHeader(string key) =>
        string.Equals(key, WorkflowNameHeader, StringComparison.Ordinal) ||
        string.Equals(key, ScopeIdHeader, StringComparison.Ordinal) ||
        string.Equals(key, SourceActorIdHeader, StringComparison.Ordinal);

    private static string ResolveHeader(
        IReadOnlyDictionary<string, string> headers,
        string key,
        string fallback) =>
        headers.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{fieldName} is required.", fieldName);

        return normalized;
    }

    private static string NormalizeOptional(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
}
