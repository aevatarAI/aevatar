using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;

namespace Aevatar.GAgentService.Application.Schedules;

public sealed class ScheduledDispatchApplicationService : IScheduledDispatchApplicationService
{
    private const string ScheduleIdAllowedCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-";
    private readonly IScheduledDispatchActorPort _actorPort;
    private readonly IScheduledDispatchQueryPort _queryPort;
    private readonly IScheduledDispatchTargetPreparationService _targetPreparationService;

    public ScheduledDispatchApplicationService(
        IScheduledDispatchActorPort actorPort,
        IScheduledDispatchQueryPort queryPort,
        IScheduledDispatchTargetPreparationService targetPreparationService)
    {
        _actorPort = actorPort ?? throw new ArgumentNullException(nameof(actorPort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _targetPreparationService = targetPreparationService ?? throw new ArgumentNullException(nameof(targetPreparationService));
    }

    public async Task<ScheduledDispatchMutationReceipt> CreateAsync(
        ScheduledDispatchConfiguration configuration,
        CancellationToken ct = default)
    {
        var normalized = NormalizeConfiguration(configuration, requireScheduleId: false);
        ValidateSchedule(normalized);
        await EnsureCreatableAsync(normalized.ScheduleId, ct);

        var dispatch = await _targetPreparationService.PrepareAsync(
            normalized,
            BuildScheduleCommandId(normalized.ScheduleId),
            BuildScheduleCorrelationId(normalized.ScheduleId),
            ct);
        var actorId = await _actorPort.EnsureScheduleActorAsync(normalized.ScheduleId, ct);
        var admission = await _actorPort.DispatchCreateAsync(actorId, normalized, dispatch, ct);
        return CreateMutationReceipt(normalized.ScheduleId, actorId, admission);
    }

    public async Task<ScheduledDispatchMutationReceipt> UpdateAsync(
        string scheduleId,
        ScheduledDispatchConfiguration configuration,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalized = NormalizeConfiguration(
            configuration with { ScheduleId = normalizedScheduleId },
            requireScheduleId: true);
        ValidateSchedule(normalized);
        await EnsureMutableAsync(normalized.ScheduleId, ct);

        var dispatch = await _targetPreparationService.PrepareAsync(
            normalized,
            BuildScheduleCommandId(normalized.ScheduleId),
            BuildScheduleCorrelationId(normalized.ScheduleId),
            ct);
        var actorId = await ResolveScheduleActorAsync(normalized.ScheduleId, ct);
        var admission = await _actorPort.DispatchUpdateAsync(actorId, normalized, dispatch, ct);
        return CreateMutationReceipt(normalized.ScheduleId, actorId, admission);
    }

    public async Task<ScheduledDispatchMutationReceipt> EnableAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        await EnsureMutableAsync(normalizedScheduleId, ct);
        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        var admission = await _actorPort.DispatchEnableAsync(actorId, NormalizeOptional(reason), ct);
        return CreateMutationReceipt(normalizedScheduleId, actorId, admission);
    }

    public async Task<ScheduledDispatchMutationReceipt> DisableAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        await EnsureMutableAsync(normalizedScheduleId, ct);
        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        var admission = await _actorPort.DispatchDisableAsync(actorId, NormalizeOptional(reason), ct);
        return CreateMutationReceipt(normalizedScheduleId, actorId, admission);
    }

    public async Task<ScheduledDispatchMutationReceipt> DeleteAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        await EnsureMutableAsync(normalizedScheduleId, ct);
        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        var admission = await _actorPort.DispatchDeleteAsync(actorId, NormalizeOptional(reason), ct);
        return CreateMutationReceipt(normalizedScheduleId, actorId, admission);
    }

    public async Task<ScheduledDispatchDetail?> GetAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var schedule = await _queryPort.GetAsync(normalizedScheduleId, ct);
        return schedule?.Schedule.Deleted == false ? schedule : null;
    }

    public Task<ScheduledDispatchListResult> ListAsync(
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default) =>
        ListAsync(new ScheduledDispatchListQuery(take, cursor, includeTotalCount), ct);

    public Task<ScheduledDispatchListResult> ListAsync(
        ScheduledDispatchListQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _queryPort.ListAsync(query with { Take = Math.Clamp(query.Take, 1, 200) }, ct);
    }

    public Task<ScheduledDispatchPreview> PreviewAsync(
        string cronExpression,
        string? timezone,
        int count,
        DateTimeOffset? fromUtc = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalizedCron = NormalizeRequired(cronExpression, nameof(cronExpression));
        var normalizedTimezone = ScheduledDispatchCalculator.NormalizeTimezone(timezone);
        var nextFireTimes = ScheduledDispatchCalculator.GetNextOccurrences(
            normalizedCron,
            normalizedTimezone,
            fromUtc ?? DateTimeOffset.UtcNow,
            Math.Clamp(count, 1, 100));
        return Task.FromResult(new ScheduledDispatchPreview(
            normalizedCron,
            normalizedTimezone,
            nextFireTimes));
    }

    public async Task<ScheduledDispatchRunNowReceipt> RunNowAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        await EnsureMutableAsync(normalizedScheduleId, ct);
        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        var scheduledFireAt = DateTimeOffset.UtcNow;
        var admission = await _actorPort.DispatchRunNowAsync(actorId, scheduledFireAt, ct);
        return new ScheduledDispatchRunNowReceipt(
            normalizedScheduleId,
            actorId,
            scheduledFireAt,
            ScheduledDispatchCalculator.BuildIdempotencyKey(normalizedScheduleId, scheduledFireAt),
            admission.Accepted,
            admission.CommandId,
            admission.CorrelationId,
            admission.AckedAt,
            "accepted");
    }

    private static ScheduledDispatchMutationReceipt CreateMutationReceipt(
        string scheduleId,
        string actorId,
        DispatchAdmission admission) =>
        new(
            scheduleId,
            actorId,
            admission.Accepted,
            admission.CommandId,
            admission.CorrelationId,
            admission.AckedAt,
            "accepted");

    private async Task EnsureCreatableAsync(string scheduleId, CancellationToken ct)
    {
        var existing = await _queryPort.GetAsync(scheduleId, ct);
        if (existing != null)
            throw new ScheduledDispatchConflictException(scheduleId, $"Scheduled dispatch '{scheduleId}' already exists.");
    }

    private async Task EnsureMutableAsync(string scheduleId, CancellationToken ct)
    {
        var existing = await _queryPort.GetAsync(scheduleId, ct);
        if (existing?.Schedule.Deleted == true)
            throw new ScheduledDispatchNotFoundException(scheduleId);
    }

    private async Task<string> ResolveScheduleActorAsync(string scheduleId, CancellationToken ct)
    {
        var actorId = await _actorPort.ResolveScheduleActorAsync(scheduleId, ct);
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ScheduledDispatchNotFoundException(scheduleId);

        return actorId;
    }

    private static ScheduledDispatchConfiguration NormalizeConfiguration(
        ScheduledDispatchConfiguration configuration,
        bool requireScheduleId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var scheduleId = string.IsNullOrWhiteSpace(configuration.ScheduleId)
            ? Guid.NewGuid().ToString("N")
            : NormalizeScheduleId(configuration.ScheduleId);
        if (requireScheduleId && string.IsNullOrWhiteSpace(scheduleId))
            throw new ArgumentException("Schedule id is required.", nameof(configuration));

        return configuration with
        {
            ScheduleId = scheduleId,
            DisplayName = NormalizeOptional(configuration.DisplayName),
            Target = NormalizeTarget(configuration.Target),
            CronExpression = NormalizeRequired(configuration.CronExpression, nameof(configuration.CronExpression)),
            Timezone = ScheduledDispatchCalculator.NormalizeTimezone(configuration.Timezone),
            Headers = NormalizeHeaders(configuration.Headers),
        };
    }

    private static ScheduledDispatchTargetDescriptor NormalizeTarget(ScheduledDispatchTargetDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind switch
        {
            ScheduledDispatchTargetKind.Envelope => NormalizeEnvelopeTarget(target),
            ScheduledDispatchTargetKind.ServiceInvocation => NormalizeServiceInvocationTarget(target),
            _ => throw new ArgumentException($"Unsupported scheduled dispatch target kind '{target.Kind}'.", nameof(target)),
        };
    }

    private static ScheduledDispatchTargetDescriptor NormalizeEnvelopeTarget(ScheduledDispatchTargetDescriptor target)
    {
        if (target.Envelope?.Payload == null)
            throw new ArgumentException("Envelope scheduled dispatch target requires an envelope payload.", nameof(target));

        var actorId = NormalizeNullable(target.ActorId);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            actorId = NormalizeNullable(target.Envelope.Route.GetTargetActorId());
            if (string.IsNullOrWhiteSpace(actorId))
                throw new ArgumentException("Envelope scheduled dispatch target requires an actor id.", nameof(target));
        }

        return target with
        {
            ActorId = actorId,
            Envelope = target.Envelope.Clone(),
            ServiceInvocation = null,
        };
    }

    private static ScheduledDispatchTargetDescriptor NormalizeServiceInvocationTarget(ScheduledDispatchTargetDescriptor target)
    {
        var invocation = target.ServiceInvocation
            ?? throw new ArgumentException("Service invocation scheduled dispatch target is required.", nameof(target));
        if (invocation.Identity == null)
            throw new ArgumentException("Service invocation identity is required.", nameof(target));
        if (string.IsNullOrWhiteSpace(invocation.Identity.ServiceId))
            throw new ArgumentException("Service invocation service id is required.", nameof(target));
        if (string.IsNullOrWhiteSpace(invocation.EndpointId))
            throw new ArgumentException("Service invocation endpoint id is required.", nameof(target));
        if (invocation.Payload == null)
            throw new ArgumentException("Service invocation payload is required.", nameof(target));

        return target with
        {
            ActorId = null,
            Envelope = null,
            ServiceInvocation = invocation with
            {
                EndpointId = NormalizeRequired(invocation.EndpointId, nameof(invocation.EndpointId)),
                RevisionId = NormalizeNullable(invocation.RevisionId),
                Identity = invocation.Identity.Clone(),
                Payload = invocation.Payload.Clone(),
                Caller = invocation.Caller?.Clone(),
                Auth = NormalizeServiceInvocationAuth(invocation.Auth),
            },
        };
    }

    private static ScheduledServiceInvocationAuth? NormalizeServiceInvocationAuth(
        ScheduledServiceInvocationAuth? auth)
    {
        if (auth == null)
            return null;
        if (auth.SenderNyxId == null)
            throw new ArgumentException("Service invocation sender NyxID credential source is required.", nameof(auth));

        var source = auth.SenderNyxId;
        if (source.Subject == null)
            throw new ArgumentException("Service invocation sender NyxID subject is required.", nameof(auth));

        return new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            new ScheduledServiceInvocationNyxIdSubjectRef(
                NormalizeRequired(source.Subject.Platform, nameof(source.Subject.Platform)),
                NormalizeOptional(source.Subject.Tenant),
                NormalizeRequired(source.Subject.ExternalUserId, nameof(source.Subject.ExternalUserId))),
            NormalizeRequired(source.Scope, nameof(source.Scope))));
    }

    private static void ValidateSchedule(ScheduledDispatchConfiguration configuration)
    {
        var validation = ScheduledDispatchCalculator.Validate(configuration.CronExpression, configuration.Timezone);
        if (!validation.Succeeded)
            throw new ArgumentException(validation.Error, nameof(configuration));
    }

    private static string BuildScheduleCommandId(string scheduleId) =>
        $"schedule-{scheduleId}-trigger";

    private static string BuildScheduleCorrelationId(string scheduleId) =>
        $"schedule-{scheduleId}";

    private static string NormalizeScheduleId(string? scheduleId)
    {
        if (string.IsNullOrWhiteSpace(scheduleId))
            throw new ArgumentException("Schedule id is required.", nameof(scheduleId));

        var normalized = scheduleId.Trim();
        if (normalized.Any(static ch => ScheduleIdAllowedCharacters.IndexOf(ch) < 0))
            throw new ArgumentException(
                "Schedule id may only contain letters, digits, '.', '_', and '-'.",
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
