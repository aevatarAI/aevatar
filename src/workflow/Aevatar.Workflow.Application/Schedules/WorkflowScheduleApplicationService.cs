using Aevatar.Workflow.Application.Abstractions.Schedules;

namespace Aevatar.Workflow.Application.Schedules;

public sealed class WorkflowScheduleApplicationService : IWorkflowScheduleApplicationService
{
    private const string ScheduleIdAllowedCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._:-";
    private readonly IWorkflowScheduleActorPort _actorPort;
    private readonly IWorkflowScheduleQueryPort _queryPort;

    public WorkflowScheduleApplicationService(
        IWorkflowScheduleActorPort actorPort,
        IWorkflowScheduleQueryPort queryPort)
    {
        _actorPort = actorPort ?? throw new ArgumentNullException(nameof(actorPort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public async Task<WorkflowScheduleMutationReceipt> CreateAsync(
        WorkflowScheduleConfiguration configuration,
        CancellationToken ct = default)
    {
        var normalized = NormalizeConfiguration(configuration, requireScheduleId: false);
        ValidateSchedule(normalized);

        var actorId = await _actorPort.EnsureScheduleActorAsync(normalized.ScheduleId, ct);
        await _actorPort.DispatchConfigureAsync(actorId, normalized, ct);
        return new WorkflowScheduleMutationReceipt(normalized.ScheduleId, actorId, Accepted: true);
    }

    public async Task<WorkflowScheduleMutationReceipt> UpdateAsync(
        string scheduleId,
        WorkflowScheduleConfiguration configuration,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalized = NormalizeConfiguration(
            configuration with { ScheduleId = normalizedScheduleId },
            requireScheduleId: true);
        ValidateSchedule(normalized);

        var actorId = await _actorPort.EnsureScheduleActorAsync(normalized.ScheduleId, ct);
        await _actorPort.DispatchConfigureAsync(actorId, normalized, ct);
        return new WorkflowScheduleMutationReceipt(normalized.ScheduleId, actorId, Accepted: true);
    }

    public async Task<WorkflowScheduleMutationReceipt> EnableAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var actorId = await ResolveConfiguredScheduleActorAsync(normalizedScheduleId, ct);
        await _actorPort.DispatchEnableAsync(actorId, NormalizeOptional(reason), ct);
        return new WorkflowScheduleMutationReceipt(normalizedScheduleId, actorId, Accepted: true);
    }

    public async Task<WorkflowScheduleMutationReceipt> DisableAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var actorId = await ResolveConfiguredScheduleActorAsync(normalizedScheduleId, ct);
        await _actorPort.DispatchDisableAsync(actorId, NormalizeOptional(reason), ct);
        return new WorkflowScheduleMutationReceipt(normalizedScheduleId, actorId, Accepted: true);
    }

    public Task<WorkflowScheduleDetail?> GetAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        return _queryPort.GetAsync(normalizedScheduleId, ct);
    }

    public Task<WorkflowScheduleListResult> ListAsync(
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default) =>
        _queryPort.ListAsync(Math.Clamp(take, 1, 200), cursor, includeTotalCount, ct);

    public Task<WorkflowSchedulePreview> PreviewAsync(
        string cronExpression,
        string? timezone,
        int count,
        DateTimeOffset? fromUtc = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalizedCron = NormalizeRequired(cronExpression, nameof(cronExpression));
        var normalizedTimezone = WorkflowScheduleCalculator.NormalizeTimezone(timezone);
        var nextFireTimes = WorkflowScheduleCalculator.GetNextOccurrences(
            normalizedCron,
            normalizedTimezone,
            fromUtc ?? DateTimeOffset.UtcNow,
            Math.Clamp(count, 1, 100));
        return Task.FromResult(new WorkflowSchedulePreview(
            normalizedCron,
            normalizedTimezone,
            nextFireTimes));
    }

    public async Task<WorkflowScheduleRunNowReceipt> RunNowAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var actorId = await ResolveConfiguredScheduleActorAsync(normalizedScheduleId, ct);
        var scheduledFireAt = DateTimeOffset.UtcNow;
        await _actorPort.DispatchRunNowAsync(actorId, scheduledFireAt, ct);
        return new WorkflowScheduleRunNowReceipt(
            normalizedScheduleId,
            actorId,
            scheduledFireAt,
            WorkflowScheduleCalculator.BuildIdempotencyKey(normalizedScheduleId, scheduledFireAt),
            Accepted: true);
    }

    private async Task<string> ResolveConfiguredScheduleActorAsync(string scheduleId, CancellationToken ct)
    {
        var detail = await _queryPort.GetAsync(scheduleId, ct);
        if (detail == null)
            throw new WorkflowScheduleNotFoundException(scheduleId);

        if (string.IsNullOrWhiteSpace(detail.Schedule.WorkflowName) ||
            string.IsNullOrWhiteSpace(detail.Schedule.CronExpression))
        {
            throw new WorkflowScheduleConflictException(
                scheduleId,
                $"Workflow schedule '{scheduleId}' is not configured.");
        }

        var actorId = await _actorPort.ResolveScheduleActorAsync(scheduleId, ct);
        if (string.IsNullOrWhiteSpace(actorId))
            throw new WorkflowScheduleNotFoundException(scheduleId);

        return actorId;
    }

    private static WorkflowScheduleConfiguration NormalizeConfiguration(
        WorkflowScheduleConfiguration configuration,
        bool requireScheduleId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var scheduleId = string.IsNullOrWhiteSpace(configuration.ScheduleId)
            ? Guid.NewGuid().ToString("N")
            : NormalizeScheduleId(configuration.ScheduleId);
        if (requireScheduleId)
            scheduleId = NormalizeScheduleId(scheduleId);

        return configuration with
        {
            ScheduleId = scheduleId,
            DisplayName = NormalizeOptional(configuration.DisplayName),
            WorkflowName = NormalizeRequired(configuration.WorkflowName, nameof(configuration.WorkflowName)),
            Prompt = NormalizeRequired(configuration.Prompt, nameof(configuration.Prompt)),
            CronExpression = NormalizeRequired(configuration.CronExpression, nameof(configuration.CronExpression)),
            Timezone = WorkflowScheduleCalculator.NormalizeTimezone(configuration.Timezone),
            Headers = NormalizeHeaders(configuration.Headers),
            ScopeId = NormalizeNullable(configuration.ScopeId),
            ActorId = NormalizeNullable(configuration.ActorId),
        };
    }

    private static void ValidateSchedule(WorkflowScheduleConfiguration configuration)
    {
        var validation = WorkflowScheduleCalculator.Validate(configuration.CronExpression, configuration.Timezone);
        if (!validation.Succeeded)
            throw new ArgumentException(validation.Error, nameof(configuration));
    }

    private static string NormalizeScheduleId(string? scheduleId)
    {
        if (string.IsNullOrWhiteSpace(scheduleId))
            throw new ArgumentException("Schedule id is required.", nameof(scheduleId));

        var normalized = scheduleId.Trim();
        if (normalized.Any(static ch => ScheduleIdAllowedCharacters.IndexOf(ch) < 0))
            throw new ArgumentException(
                "Schedule id may only contain letters, digits, '.', '_', ':', and '-'.",
                nameof(scheduleId));

        return normalized;
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{parameterName} is required.", parameterName);

        return value.Trim();
    }

    private static string NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyDictionary<string, string> NormalizeHeaders(
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers == null || headers.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in headers)
        {
            var normalizedKey = NormalizeOptional(key);
            var normalizedValue = NormalizeOptional(value);
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;

            normalized[normalizedKey] = normalizedValue;
        }

        return normalized;
    }
}
