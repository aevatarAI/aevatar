using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;

namespace Aevatar.GAgentService.Application.Schedules;

public sealed class ScheduledDispatchApplicationService : IScheduledDispatchApplicationService
{
    private const string ScheduleIdAllowedCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-";
    private readonly IScheduledDispatchActorPort _actorPort;
    private readonly IScheduledDispatchQueryPort _queryPort;
    private readonly IScheduledDispatchTargetPreparationService _targetPreparationService;
    private readonly IScheduledDispatchCredentialAdmissionPort _credentialAdmissionPort;

    public ScheduledDispatchApplicationService(
        IScheduledDispatchActorPort actorPort,
        IScheduledDispatchQueryPort queryPort,
        IScheduledDispatchTargetPreparationService targetPreparationService,
        IScheduledDispatchCredentialAdmissionPort credentialAdmissionPort)
    {
        _actorPort = actorPort ?? throw new ArgumentNullException(nameof(actorPort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _targetPreparationService = targetPreparationService ?? throw new ArgumentNullException(nameof(targetPreparationService));
        _credentialAdmissionPort = credentialAdmissionPort ?? throw new ArgumentNullException(nameof(credentialAdmissionPort));
    }

    public async Task<ScheduledDispatchMutationReceipt> CreateAsync(
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default)
    {
        var normalized = await NormalizeAndAdmitMutationAsync(configuration, context, requireScheduleId: false, ct);
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

    public async Task<ScheduledDispatchMutationReceipt> EnsureAsync(
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default)
    {
        var normalized = await NormalizeAndAdmitMutationAsync(configuration, context, requireScheduleId: true, ct);
        // A deleted schedule is a permanent tombstone: the actor rejects any
        // reconfigure, and the admission-only dispatch would swallow that
        // rejection — an unguarded ensure would return an accepted receipt for
        // a schedule that never materializes. Surface the tombstone as the same
        // typed not-found the mutators throw, so callers can pick a fresh id.
        await EnsureMutableAsync(normalized.ScheduleId, ct);

        var dispatch = await _targetPreparationService.PrepareAsync(
            normalized,
            BuildScheduleCommandId(normalized.ScheduleId),
            BuildScheduleCorrelationId(normalized.ScheduleId),
            ct);
        var actorId = await _actorPort.EnsureScheduleActorAsync(normalized.ScheduleId, ct);
        var admission = await _actorPort.DispatchEnsureAsync(actorId, normalized, dispatch, ct);
        return CreateMutationReceipt(normalized.ScheduleId, actorId, admission);
    }

    public async Task<ScheduledDispatchMutationReceipt> UpdateAsync(
        string scheduleId,
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalized = await NormalizeAndAdmitMutationAsync(
            configuration with { ScheduleId = normalizedScheduleId },
            context,
            requireScheduleId: true,
            ct);
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

    private async Task EnsureCreatableAsync(string scheduleId, CancellationToken ct)
    {
        var existingActorId = await _actorPort.ResolveScheduleActorAsync(scheduleId, ct);
        if (!string.IsNullOrWhiteSpace(existingActorId))
            throw new ScheduledDispatchConflictException(scheduleId, $"Scheduled dispatch '{scheduleId}' already exists.");
    }

    private async Task<ScheduledDispatchConfiguration> NormalizeAndAdmitMutationAsync(
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchMutationContext? context,
        bool requireScheduleId,
        CancellationToken ct)
    {
        var normalized = NormalizeConfiguration(configuration, requireScheduleId);
        ValidateSchedule(normalized);
        return await AdmitServiceInvocationScopeOwnerAsync(
            normalized,
            NormalizeMutationContext(context ?? ScheduledDispatchMutationContext.None),
            ct);
    }

    private async Task<ScheduledDispatchConfiguration> AdmitServiceInvocationScopeOwnerAsync(
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchMutationContext context,
        CancellationToken ct)
    {
        var serviceInvocation = configuration.Target.ServiceInvocation;
        var scopeOwnerNyxId = serviceInvocation?.Auth?.ScopeOwnerNyxId;
        if (serviceInvocation == null || scopeOwnerNyxId == null)
            return configuration;

        var authenticatedOwnerSubject = context.AuthenticatedNyxIdOwnerSubject
            ?? throw new ArgumentException(
                "Authenticated NyxID owner subject is required for scope owner schedule auth.",
                nameof(context));
        var authenticatedScopeId = NormalizeRequired(
            context.AuthenticatedScopeId,
            nameof(context.AuthenticatedScopeId));
        if (!string.Equals(serviceInvocation.Identity.TenantId, authenticatedScopeId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Service invocation target scope must match the authenticated scope for scope owner schedule auth.",
                nameof(configuration));
        }

        if (scopeOwnerNyxId.OwnerSubject != null &&
            !SubjectEquals(scopeOwnerNyxId.OwnerSubject, authenticatedOwnerSubject))
        {
            throw new ArgumentException(
                "Scope owner NyxID subject must match the authenticated owner subject.",
                nameof(configuration));
        }

        var admittedScopeOwnerNyxId = scopeOwnerNyxId with
        {
            OwnerSubject = authenticatedOwnerSubject,
        };
        var admittedConfiguration = configuration with
        {
            Target = configuration.Target with
            {
                ServiceInvocation = serviceInvocation with
                {
                    Auth = serviceInvocation.Auth! with
                    {
                        ScopeOwnerNyxId = admittedScopeOwnerNyxId,
                    },
                },
            },
        };

        var result = await _credentialAdmissionPort.AdmitAsync(
            new ScheduledDispatchCredentialAdmissionRequest(
                context,
                admittedScopeOwnerNyxId,
                serviceInvocation.Identity),
            ct);
        if (result.Status == ScheduledDispatchCredentialAdmissionStatus.Allowed)
            return admittedConfiguration;

        throw new ArgumentException(
            NormalizeAdmissionError(result),
            nameof(configuration));
    }

    private static string NormalizeAdmissionError(ScheduledDispatchCredentialAdmissionResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Error))
            return result.Error.Trim();

        return result.Status switch
        {
            ScheduledDispatchCredentialAdmissionStatus.MissingBinding =>
                "Authenticated NyxID owner binding is required for scope owner schedule auth; complete or refresh NyxID login before creating a scope owner schedule.",
            ScheduledDispatchCredentialAdmissionStatus.ScopeMismatch =>
                "NyxID binding does not grant the requested schedule scope.",
            ScheduledDispatchCredentialAdmissionStatus.Unsupported =>
                "Scheduled dispatch scope owner NyxID admission is not configured.",
            _ => "Scheduled dispatch scope owner NyxID admission failed.",
        };
    }

    private static ScheduledDispatchMutationContext NormalizeMutationContext(ScheduledDispatchMutationContext context) =>
        new(
            NormalizeNullable(context.AuthenticatedScopeId),
            context.AuthenticatedNyxIdOwnerSubject == null
                ? null
                : NormalizeSubject(context.AuthenticatedNyxIdOwnerSubject));

    private static bool SubjectEquals(
        ScheduledServiceInvocationNyxIdSubjectRef left,
        ScheduledServiceInvocationNyxIdSubjectRef right) =>
        string.Equals(left.Platform, right.Platform, StringComparison.Ordinal) &&
        string.Equals(left.Tenant, right.Tenant, StringComparison.Ordinal) &&
        string.Equals(left.ExternalUserId, right.ExternalUserId, StringComparison.Ordinal);

    private async Task EnsureMutableAsync(string scheduleId, CancellationToken ct)
    {
        var existing = await _queryPort.GetAsync(scheduleId, ct);
        if (existing?.Schedule.Deleted == true)
            throw new ScheduledDispatchNotFoundException(scheduleId);
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
        var identity = NormalizeServiceInvocationIdentity(invocation.Identity);
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
                Identity = identity,
                Payload = invocation.Payload.Clone(),
                Caller = invocation.Caller?.Clone(),
                Auth = NormalizeServiceInvocationAuth(invocation.Auth),
            },
        };
    }

    private static ServiceIdentity NormalizeServiceInvocationIdentity(ServiceIdentity? identity)
    {
        if (identity == null)
            throw new ArgumentException("Service invocation identity is required.", nameof(identity));

        return new ServiceIdentity
        {
            TenantId = NormalizeRequiredServiceIdentityField(identity.TenantId, "tenant id", nameof(identity.TenantId)),
            AppId = NormalizeRequiredServiceIdentityField(identity.AppId, "app id", nameof(identity.AppId)),
            Namespace = NormalizeRequiredServiceIdentityField(identity.Namespace, "namespace", nameof(identity.Namespace)),
            ServiceId = NormalizeRequiredServiceIdentityField(identity.ServiceId, "service id", nameof(identity.ServiceId)),
        };
    }

    private static string NormalizeRequiredServiceIdentityField(
        string? value,
        string fieldDescription,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Service invocation {fieldDescription} is required.", parameterName);

        return value.Trim();
    }

    private static ScheduledServiceInvocationAuth? NormalizeServiceInvocationAuth(
        ScheduledServiceInvocationAuth? auth)
    {
        if (auth == null)
            return null;

        return auth.Source switch
        {
<<<<<<< HEAD
            throw new ArgumentException(
                "Durable sender bearer token schedule auth is no longer supported; use senderNyxId or scopeOwnerNyxId so the dispatch can mint a short-lived NyxID token at fire time.",
                nameof(auth));
        }

        if (Convert.ToInt32(hasSenderNyxId) +
            Convert.ToInt32(hasScopeOwnerNyxId) != 1)
        {
            throw new ArgumentException("Exactly one service invocation credential source is required.", nameof(auth));
        }

        if (hasScopeOwnerNyxId)
        {
            var ownerSource = auth.ScopeOwnerNyxId!;
            return new ScheduledServiceInvocationAuth(
                ScopeOwnerNyxId: new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource(
                    NormalizeRequired(ownerSource.Scope, nameof(ownerSource.Scope)),
                    ownerSource.OwnerSubject == null ? null : NormalizeSubject(ownerSource.OwnerSubject)));
        }

        var source = auth.SenderNyxId!;
        if (source.Subject == null)
            throw new ArgumentException("Service invocation sender NyxID subject is required.", nameof(auth));

        return new ScheduledServiceInvocationAuth(SenderNyxId: new ScheduledServiceInvocationNyxIdCredentialSource(
            NormalizeSubject(source.Subject),
            NormalizeRequired(source.Scope, nameof(source.Scope))));
    }

=======
            null => throw new ArgumentException("Exactly one service invocation credential source is required.", nameof(auth)),
            ScheduledServiceInvocationNyxIdCredentialSource nyxId => NormalizeNyxIdAuth(nyxId, auth),
            ScheduledServiceInvocationDurableCredentialReference durable =>
                new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationDurableCredentialReference(
                    NormalizeRequired(durable.CredentialId, nameof(durable.CredentialId)))),
            _ => throw new ArgumentException("Unsupported service invocation credential source.", nameof(auth)),
        };
    }

    private static ScheduledServiceInvocationAuth NormalizeNyxIdAuth(
        ScheduledServiceInvocationNyxIdCredentialSource source,
        ScheduledServiceInvocationAuth auth)
    {
        if (source.Subject == null)
            throw new ArgumentException(ToMissingSubjectMessage(source.Role), nameof(auth));

        var role = source.Role switch
        {
            ScheduledServiceInvocationNyxIdCredentialRole.Sender => ScheduledServiceInvocationNyxIdCredentialRole.Sender,
            ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner => ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner,
            _ => throw new ArgumentException("Service invocation NyxID credential role is required.", nameof(auth)),
        };

        return new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            NormalizeSubject(source.Subject),
            NormalizeRequired(source.Scope, nameof(source.Scope)),
            role));
    }

    private static string ToMissingSubjectMessage(ScheduledServiceInvocationNyxIdCredentialRole role) =>
        role == ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner
            ? "Service invocation scope owner NyxID subject is required."
            : "Service invocation sender NyxID subject is required.";

>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential
    private static ScheduledServiceInvocationNyxIdSubjectRef NormalizeSubject(
        ScheduledServiceInvocationNyxIdSubjectRef subject) =>
        new(
            NormalizeRequired(subject.Platform, nameof(subject.Platform)),
            NormalizeOptional(subject.Tenant),
            NormalizeRequired(subject.ExternalUserId, nameof(subject.ExternalUserId)));

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
