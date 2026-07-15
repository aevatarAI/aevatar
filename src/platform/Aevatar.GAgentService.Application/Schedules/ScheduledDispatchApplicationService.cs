using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
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
    private readonly IScheduledDispatchCredentialRequirementPolicy _credentialRequirementPolicy;

    public ScheduledDispatchApplicationService(
        IScheduledDispatchActorPort actorPort,
        IScheduledDispatchQueryPort queryPort,
        IScheduledDispatchTargetPreparationService targetPreparationService,
        IScheduledDispatchCredentialAdmissionPort credentialAdmissionPort,
        IScheduledDispatchCredentialRequirementPolicy? credentialRequirementPolicy = null)
    {
        _actorPort = actorPort ?? throw new ArgumentNullException(nameof(actorPort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _targetPreparationService = targetPreparationService ?? throw new ArgumentNullException(nameof(targetPreparationService));
        _credentialAdmissionPort = credentialAdmissionPort ?? throw new ArgumentNullException(nameof(credentialAdmissionPort));
        _credentialRequirementPolicy = credentialRequirementPolicy ??
            DefaultScheduledDispatchCredentialRequirementPolicy.Instance;
    }

    public async Task<ScheduledDispatchMutationReceipt> CreateAsync(
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default)
    {
        var normalized = await NormalizeAndAdmitMutationAsync(configuration, context, requireScheduleId: false, ct);
        await EnsureCreatableAsync(normalized.ScheduleId, ct);
        normalized = AdmitCredentialRequirement(
            normalized,
            ScheduledDispatchCredentialRequirementOperation.Create);

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
        var existing = await GetMutableScheduleAsync(normalized.ScheduleId, ct);
        normalized = AdmitCredentialRequirement(
            normalized,
            ScheduledDispatchCredentialRequirementOperation.Ensure,
            existing?.Schedule);

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
        var existing = await GetMutableScheduleAsync(normalized.ScheduleId, ct);
        normalized = AdmitCredentialRequirement(
            normalized,
            ScheduledDispatchCredentialRequirementOperation.Update,
            existing?.Schedule);

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
        var detail = await GetMutableScheduleAsync(normalizedScheduleId, ct);
        if (detail == null)
            throw new ScheduledDispatchNotFoundException(normalizedScheduleId);

        AdmitRunNowCredentialRequirement(detail.Schedule);
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
        var scopeOwnerIdentity = serviceInvocation?.Auth?.ScopeOwnerIdentity;
        if (serviceInvocation == null || scopeOwnerIdentity == null)
            return configuration;

        var authenticatedOwnerSubject = context.AuthenticatedIdentityOwnerSubject
            ?? throw new ArgumentException(
                "Authenticated identity owner subject is required for scope owner schedule auth.",
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

        if (scopeOwnerIdentity.OwnerSubject != null &&
            !SubjectEquals(scopeOwnerIdentity.OwnerSubject, authenticatedOwnerSubject))
        {
            throw new ArgumentException(
                "Scope owner identity subject must match the authenticated owner subject.",
                nameof(configuration));
        }

        var admittedScopeOwnerIdentity = scopeOwnerIdentity with
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
                        Source = new ScheduledServiceInvocationIdentityCredentialSource(
                            admittedScopeOwnerIdentity.OwnerSubject!,
                            admittedScopeOwnerIdentity.Scope,
                            ScheduledServiceInvocationIdentityCredentialRole.ScopeOwner),
                    },
                },
            },
        };

        var result = await _credentialAdmissionPort.AdmitAsync(
            new ScheduledDispatchCredentialAdmissionRequest(
                context,
                admittedScopeOwnerIdentity,
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
                "Authenticated identity owner binding is required for scope owner schedule auth; complete or refresh identity login before creating a scope owner schedule.",
            ScheduledDispatchCredentialAdmissionStatus.ScopeMismatch =>
                "Identity binding does not grant the requested schedule scope.",
            ScheduledDispatchCredentialAdmissionStatus.Unsupported =>
                "Scheduled dispatch scope owner identity admission is not configured.",
            _ => "Scheduled dispatch scope owner identity admission failed.",
        };
    }

    private static ScheduledDispatchMutationContext NormalizeMutationContext(ScheduledDispatchMutationContext context) =>
        new(
            NormalizeNullable(context.AuthenticatedScopeId),
            context.AuthenticatedIdentityOwnerSubject == null
                ? null
                : NormalizeSubject(context.AuthenticatedIdentityOwnerSubject));

    private static bool SubjectEquals(
        ScheduledServiceInvocationIdentitySubject left,
        ScheduledServiceInvocationIdentitySubject right) =>
        string.Equals(left.Platform, right.Platform, StringComparison.Ordinal) &&
        string.Equals(left.Tenant, right.Tenant, StringComparison.Ordinal) &&
        string.Equals(left.ExternalUserId, right.ExternalUserId, StringComparison.Ordinal);

    private async Task EnsureMutableAsync(string scheduleId, CancellationToken ct)
    {
        var existing = await GetMutableScheduleAsync(scheduleId, ct);
        if (existing?.Schedule.Deleted == true)
            throw new ScheduledDispatchNotFoundException(scheduleId);
    }

    private async Task<ScheduledDispatchDetail?> GetMutableScheduleAsync(string scheduleId, CancellationToken ct)
    {
        var existing = await _queryPort.GetAsync(scheduleId, ct);
        if (existing?.Schedule.Deleted == true)
            throw new ScheduledDispatchNotFoundException(scheduleId);

        return existing;
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

    private ScheduledDispatchConfiguration AdmitCredentialRequirement(
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchCredentialRequirementOperation operation,
        ScheduledDispatchSummary? existingSchedule = null)
    {
        var request = ScheduledDispatchCredentialRequirementRequests.FromConfiguration(configuration, operation);
        if ((operation is ScheduledDispatchCredentialRequirementOperation.Ensure or
             ScheduledDispatchCredentialRequirementOperation.Update) &&
            request.CredentialSource.Kind == ScheduledDispatchCredentialSourceKind.None &&
            existingSchedule != null &&
            existingSchedule.TargetKind == configuration.Target.Kind &&
            existingSchedule.ScheduleKind == configuration.ScheduleKind)
        {
            request = request with
            {
                CredentialSource = new ScheduledDispatchCredentialSourceSummary(
                    existingSchedule.CredentialSourceKind),
            };
        }

        var decision = _credentialRequirementPolicy.Evaluate(request);
        if (!decision.Allowed)
            throw new ArgumentException(decision.Message, nameof(configuration));

        return configuration with
        {
            CredentialRequirementTargetKind = request.TargetKind,
        };
    }

    private void AdmitRunNowCredentialRequirement(ScheduledDispatchSummary schedule)
    {
        var request = new ScheduledDispatchCredentialRequirementRequest(
            schedule.ScheduleId,
            ScheduledDispatchCredentialRequirementOperation.Fire,
            schedule.ScheduleKind,
            schedule.CredentialRequirementTargetKind,
            new ScheduledDispatchCredentialSourceSummary(schedule.CredentialSourceKind),
            ScheduledDispatchPayloadCredentialSignal.None);
        var decision = _credentialRequirementPolicy.Evaluate(request);
        if (!decision.Allowed)
            throw new ArgumentException(decision.Message, nameof(schedule));
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

        var scheduleMode = NormalizeScheduleMode(configuration.ScheduleMode);
        return configuration with
        {
            ScheduleId = scheduleId,
            DisplayName = NormalizeOptional(configuration.DisplayName),
            Target = NormalizeTarget(configuration.Target),
            CronExpression = scheduleMode == ScheduledDispatchScheduleMode.RecurringCron
                ? NormalizeRequired(configuration.CronExpression, nameof(configuration.CronExpression))
                : string.Empty,
            Timezone = ScheduledDispatchCalculator.NormalizeTimezone(configuration.Timezone),
            Headers = NormalizeHeaders(configuration.Headers),
            ScheduleMode = scheduleMode,
            OneShotFireAt = NormalizeOneShotFireAt(scheduleMode, configuration.OneShotFireAt),
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
            null => throw new ArgumentException("Exactly one service invocation credential source is required.", nameof(auth)),
            ScheduledServiceInvocationIdentityCredentialSource nyxId => NormalizeNyxIdAuth(nyxId, auth),
            ScheduledServiceInvocationDurableCredentialReference durable =>
                new ScheduledServiceInvocationAuth(NormalizeDurableCredentialReference(durable)),
            ScheduledInvocationAgentKeyCredentialReference agentKey =>
                new ScheduledServiceInvocationAuth(NormalizeScheduledInvocationAgentKey(agentKey)),
            _ => throw new ArgumentException("Unsupported service invocation credential source.", nameof(auth)),
        };
    }

    private static ScheduledServiceInvocationAuth NormalizeNyxIdAuth(
        ScheduledServiceInvocationIdentityCredentialSource source,
        ScheduledServiceInvocationAuth auth)
    {
        var role = source.Role switch
        {
            ScheduledServiceInvocationIdentityCredentialRole.Sender => ScheduledServiceInvocationIdentityCredentialRole.Sender,
            ScheduledServiceInvocationIdentityCredentialRole.ScopeOwner => ScheduledServiceInvocationIdentityCredentialRole.ScopeOwner,
            _ => throw new ArgumentException("Service invocation NyxID credential role is required.", nameof(auth)),
        };

        if (source.Subject == null)
        {
            if (role == ScheduledServiceInvocationIdentityCredentialRole.Sender)
                throw new ArgumentException(ToMissingSubjectMessage(role), nameof(auth));

            return new ScheduledServiceInvocationAuth(
                new ScheduledServiceInvocationIdentityCredentialSource(
                    null!,
                    NormalizeRequired(source.Scope, nameof(source.Scope)),
                    role));
        }

        return new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationIdentityCredentialSource(
            NormalizeSubject(source.Subject),
            NormalizeRequired(source.Scope, nameof(source.Scope)),
            role));
    }

    private static ScheduledServiceInvocationDurableCredentialReference NormalizeDurableCredentialReference(
        ScheduledServiceInvocationDurableCredentialReference reference)
    {
        var credentialId = NormalizeRequired(reference.CredentialId, nameof(reference.CredentialId));
        if (reference.SecretReference == null)
            throw new ArgumentException("Durable credential secret reference is required.", nameof(reference));

        var secretReference = NormalizeSecretReference(reference.SecretReference);
        if (!string.Equals(secretReference.Purpose, CredentialSecretPurposes.ScheduledNyxApiKey, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Durable credential secret reference purpose must be '{CredentialSecretPurposes.ScheduledNyxApiKey}'.",
                nameof(reference));
        }

        return new ScheduledServiceInvocationDurableCredentialReference(credentialId, secretReference);
    }

    private static SecretReference NormalizeSecretReference(SecretReference reference) =>
        new()
        {
            Ref = NormalizeRequired(reference.Ref, nameof(reference.Ref)),
            Purpose = NormalizeRequired(reference.Purpose, nameof(reference.Purpose)),
            Fingerprint = NormalizeOptional(reference.Fingerprint),
            Version = reference.Version,
            OwnerScopeKey = NormalizeRequired(reference.OwnerScopeKey, nameof(reference.OwnerScopeKey)),
            CreatedAtUnixMs = reference.CreatedAtUnixMs,
            ExpiresAtUnixMs = reference.ExpiresAtUnixMs,
        };

    private static ScheduledInvocationAgentKeyCredentialReference NormalizeScheduledInvocationAgentKey(
        ScheduledInvocationAgentKeyCredentialReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var reference = source.SecretReference?.Clone()
            ?? throw new ArgumentException("Scheduled invocation agent key secret reference is required.", nameof(source));
        if (string.IsNullOrWhiteSpace(reference.Ref))
            throw new ArgumentException("Scheduled invocation agent key secret reference is required.", nameof(source));
        if (!string.Equals(reference.Purpose, CredentialSecretPurposes.ScheduledInvocationAgentKey, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Scheduled invocation agent key secret reference purpose must be '{CredentialSecretPurposes.ScheduledInvocationAgentKey}'.",
                nameof(source));
        }
        if (string.IsNullOrWhiteSpace(reference.OwnerScopeKey))
            throw new ArgumentException("Scheduled invocation agent key owner scope key is required.", nameof(source));

        var apiKeyId = NormalizeRequired(source.ApiKeyId, nameof(source.ApiKeyId));
        var expiresAtUnixMs = source.KeyExpiresAtUnixMs > 0
            ? source.KeyExpiresAtUnixMs
            : reference.ExpiresAtUnixMs;
        if (expiresAtUnixMs <= 0)
            throw new ArgumentException("Scheduled invocation agent key expiry is required.", nameof(source));

        reference.ExpiresAtUnixMs = expiresAtUnixMs;
        return new ScheduledInvocationAgentKeyCredentialReference(reference, apiKeyId, expiresAtUnixMs);
    }

    private static string ToMissingSubjectMessage(ScheduledServiceInvocationIdentityCredentialRole role) =>
        role == ScheduledServiceInvocationIdentityCredentialRole.ScopeOwner
            ? "Service invocation scope owner identity subject is required."
            : "Service invocation sender identity subject is required.";

    private static ScheduledServiceInvocationIdentitySubject NormalizeSubject(
        ScheduledServiceInvocationIdentitySubject subject) =>
        new(
            NormalizeRequired(subject.Platform, nameof(subject.Platform)),
            NormalizeOptional(subject.Tenant),
            NormalizeRequired(subject.ExternalUserId, nameof(subject.ExternalUserId)));

    private static void ValidateSchedule(ScheduledDispatchConfiguration configuration)
    {
        if (configuration.ScheduleMode == ScheduledDispatchScheduleMode.OneShotAtUtc)
        {
            if (!configuration.OneShotFireAt.HasValue)
                throw new ArgumentException("One-shot fire time is required.", nameof(configuration));
            if (configuration.OneShotFireAt.Value <= DateTimeOffset.UtcNow)
                throw new ArgumentException("One-shot fire time must be in the future.", nameof(configuration));
            return;
        }

        var validation = ScheduledDispatchCalculator.Validate(configuration.CronExpression, configuration.Timezone);
        if (!validation.Succeeded)
            throw new ArgumentException(validation.Error, nameof(configuration));
    }

    private static ScheduledDispatchScheduleMode NormalizeScheduleMode(ScheduledDispatchScheduleMode mode) =>
        mode == ScheduledDispatchScheduleMode.OneShotAtUtc
            ? ScheduledDispatchScheduleMode.OneShotAtUtc
            : ScheduledDispatchScheduleMode.RecurringCron;

    private static DateTimeOffset? NormalizeOneShotFireAt(
        ScheduledDispatchScheduleMode mode,
        DateTimeOffset? fireAt) =>
        mode == ScheduledDispatchScheduleMode.OneShotAtUtc
            ? fireAt?.ToUniversalTime()
            : null;

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
