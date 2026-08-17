using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Application.Schedules;

public sealed class ScheduledDispatchApplicationService : IScheduledDispatchApplicationService
{
    private const string ScheduleIdAllowedCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-";
    private const string TeamAutomationCommitObservationUnavailableCode =
        "team_automation_commit_observation_unavailable";
    private const string TeamAutomationCommitObservationEndedCode =
        "team_automation_commit_observation_ended";
    private const string TeamAutomationDispatchRejectedCode =
        "team_automation_dispatch_rejected";
    private readonly IScheduledDispatchActorPort _actorPort;
    private readonly IScheduledDispatchQueryPort _queryPort;
    private readonly IScheduledDispatchTargetPreparationService _targetPreparationService;
    private readonly IScheduledDispatchCredentialAdmissionPort _credentialAdmissionPort;
    private readonly IScheduledDispatchCredentialRequirementPolicy _credentialRequirementPolicy;
    private readonly ITeamAutomationOperationObservationScopeLeasePreparationPort? _teamOperationObservationPreparation;
    private readonly ITeamAutomationOperationObservationProjectionPort? _teamOperationObservationProjection;
    private readonly ILogger<ScheduledDispatchApplicationService> _logger;

    public ScheduledDispatchApplicationService(
        IScheduledDispatchActorPort actorPort,
        IScheduledDispatchQueryPort queryPort,
        IScheduledDispatchTargetPreparationService targetPreparationService,
        IScheduledDispatchCredentialAdmissionPort credentialAdmissionPort,
        IScheduledDispatchCredentialRequirementPolicy? credentialRequirementPolicy = null,
        ITeamAutomationOperationObservationScopeLeasePreparationPort? teamOperationObservationPreparation = null,
        ITeamAutomationOperationObservationProjectionPort? teamOperationObservationProjection = null,
        ILogger<ScheduledDispatchApplicationService>? logger = null)
    {
        _actorPort = actorPort ?? throw new ArgumentNullException(nameof(actorPort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _targetPreparationService = targetPreparationService ?? throw new ArgumentNullException(nameof(targetPreparationService));
        _credentialAdmissionPort = credentialAdmissionPort ?? throw new ArgumentNullException(nameof(credentialAdmissionPort));
        _credentialRequirementPolicy = credentialRequirementPolicy ??
            DefaultScheduledDispatchCredentialRequirementPolicy.Instance;
        _teamOperationObservationPreparation = teamOperationObservationPreparation;
        _teamOperationObservationProjection = teamOperationObservationProjection;
        _logger = logger ?? NullLogger<ScheduledDispatchApplicationService>.Instance;
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
        var existing = await GetMutableScheduleAsync(normalized.ScheduleId, normalized.TeamAutomationOwner, ct);
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
        var normalizedContext = NormalizeMutationContext(context ?? ScheduledDispatchMutationContext.None);
        var normalized = await NormalizeAndAdmitMutationAsync(
            configuration with { ScheduleId = normalizedScheduleId },
            normalizedContext,
            requireScheduleId: true,
            ct);
        var existing = await GetMutableScheduleAsync(normalized.ScheduleId, normalized.TeamAutomationOwner, ct);
        EnsureExpectedServiceTarget(normalized.ScheduleId, existing?.Schedule, normalizedContext.ExpectedServiceTarget);
        if (existing?.Schedule is
            {
                TeamOwned: true,
                TeamAutomationLifecycleStatus: TeamAutomationLifecycleStatus.ReplacementPending,
            })
        {
            throw new ScheduledDispatchConflictException(
                normalized.ScheduleId,
                "team_automation_replacement_pending");
        }

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
        var admission = await _actorPort.DispatchUpdateAsync(
            actorId,
            normalized,
            dispatch,
            normalizedContext.ExpectedServiceTarget,
            ct);
        return CreateMutationReceipt(normalized.ScheduleId, actorId, admission);
    }

    public async Task<ScheduledDispatchMutationReceipt> EnableAsync(
        string scheduleId,
        string reason,
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var existing = await EnsureMutableAsync(normalizedScheduleId, context, ct);
        var actorId = await ResolveScheduleActorAsync(existing.Schedule.ScheduleId, ct);
        var expectedTarget = NormalizeMutationContext(context ?? ScheduledDispatchMutationContext.None)
            .ExpectedServiceTarget;
        var admission = await _actorPort.DispatchEnableAsync(
            actorId,
            NormalizeOptional(reason),
            expectedTarget,
            ct);
        return CreateMutationReceipt(normalizedScheduleId, actorId, admission);
    }

    public async Task<ScheduledDispatchMutationReceipt> DisableAsync(
        string scheduleId,
        string reason,
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var existing = await EnsureMutableAsync(normalizedScheduleId, context, ct);
        var actorId = await ResolveScheduleActorAsync(existing.Schedule.ScheduleId, ct);
        var expectedTarget = NormalizeMutationContext(context ?? ScheduledDispatchMutationContext.None)
            .ExpectedServiceTarget;
        var admission = await _actorPort.DispatchDisableAsync(
            actorId,
            NormalizeOptional(reason),
            expectedTarget,
            ct);
        return CreateMutationReceipt(normalizedScheduleId, actorId, admission);
    }

    public async Task<ScheduledDispatchMutationReceipt> DeleteAsync(
        string scheduleId,
        string reason,
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var existing = await EnsureMutableAsync(normalizedScheduleId, context, ct);
        var actorId = await ResolveScheduleActorAsync(existing.Schedule.ScheduleId, ct);
        var expectedTarget = NormalizeMutationContext(context ?? ScheduledDispatchMutationContext.None)
            .ExpectedServiceTarget;
        var admission = await _actorPort.DispatchDeleteAsync(
            actorId,
            NormalizeOptional(reason),
            expectedTarget,
            ct);
        return CreateMutationReceipt(normalizedScheduleId, actorId, admission);
    }

    public async Task<ScheduledDispatchDetail?> GetAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var schedule = await _queryPort.GetAsync(normalizedScheduleId, ct);
        return schedule?.Schedule is
        {
            Deleted: false,
            TeamOwned: false,
            TargetKind: ScheduledDispatchTargetKind.ServiceInvocation,
        }
            ? schedule
            : null;
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
        if (query.TeamAutomationOwner != null)
        {
            return _queryPort.ListAsync(query with
            {
                Take = Math.Clamp(query.Take, 1, 200),
                TargetKind = ScheduledDispatchTargetKind.ServiceInvocation,
                TeamAutomationOwner = NormalizeTeamOwner(query.TeamAutomationOwner),
                TeamAutomationScopeId = null,
                TeamAutomationTeamId = null,
                TeamAutomationMemberId = null,
                ExcludeTeamOwned = false,
                IncludeDeleted = false,
                ExcludeCompletedTeamAutomationDeletions = true,
            }, ct);
        }

        var teamAutomationScopeId = NormalizeNullable(query.TeamAutomationScopeId);
        if (teamAutomationScopeId is not null)
        {
            return _queryPort.ListAsync(query with
            {
                Take = Math.Clamp(query.Take, 1, 200),
                TargetKind = ScheduledDispatchTargetKind.ServiceInvocation,
                TeamAutomationOwner = null,
                TeamAutomationScopeId = teamAutomationScopeId,
                TeamAutomationTeamId = NormalizeNullable(query.TeamAutomationTeamId),
                TeamAutomationMemberId = NormalizeNullable(query.TeamAutomationMemberId),
                IncludeDeleted = false,
            }, ct);
        }

        return _queryPort.ListAsync(query with
        {
            Take = Math.Clamp(query.Take, 1, 200),
            TargetKind = ScheduledDispatchTargetKind.ServiceInvocation,
            TeamAutomationOwner = null,
            ExcludeTeamOwned = true,
            IncludeDeleted = false,
        }, ct);
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
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalizedContext = NormalizeMutationContext(context ?? ScheduledDispatchMutationContext.None);
        var detail = await GetMutableScheduleAsync(normalizedScheduleId, owner: null, ct);
        if (detail == null)
            throw new ScheduledDispatchNotFoundException(normalizedScheduleId);
        EnsureExpectedServiceTarget(normalizedScheduleId, detail.Schedule, normalizedContext.ExpectedServiceTarget);

        AdmitRunNowCredentialRequirement(detail.Schedule);
        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        var scheduledFireAt = DateTimeOffset.UtcNow;
        var admission = await _actorPort.DispatchRunNowAsync(
            actorId,
            scheduledFireAt,
            normalizedContext.ExpectedServiceTarget,
            ct);
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

    public async Task<TeamAutomationCommittedMutationReceipt> BeginTeamAutomationCredentialOperationAsync(
        TeamAutomationCredentialOperation operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var normalized = NormalizeTeamOperation(operation);
        var existing = await _queryPort.GetAsync(normalized.ScheduleId, ct);
        if (existing != null &&
            (!existing.Schedule.TeamOwned || !TeamOwnerEquals(existing.Schedule, normalized.Owner)))
        {
            throw new ScheduledDispatchConflictException(
                normalized.ScheduleId,
                $"Scheduled dispatch '{normalized.ScheduleId}' already has a different owner.");
        }

        var actorId = await _actorPort.EnsureScheduleActorAsync(normalized.ScheduleId, ct);
        return await DispatchObservedTeamOperationAsync(
            normalized.ScheduleId,
            actorId,
            normalized.OperationId,
            normalized.IdempotencyKey,
            TeamAutomationOperationObservationStages.Begin,
            (requestId, token) => _actorPort.DispatchBeginTeamAutomationCredentialOperationAsync(
                actorId, normalized, requestId, token),
            ct);
    }

    public async Task<TeamAutomationCommittedMutationReceipt> RetryTeamAutomationCredentialOperationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalizedOwner = NormalizeTeamOwner(owner);
        var normalizedOperationId = NormalizeRequired(operationId, nameof(operationId));
        var normalizedIdempotencyKey = NormalizeRequired(idempotencyKey, nameof(idempotencyKey));
        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        return await DispatchObservedTeamOperationAsync(
            normalizedScheduleId,
            actorId,
            normalizedOperationId,
            normalizedIdempotencyKey,
            TeamAutomationOperationObservationStages.Begin,
            (requestId, token) => _actorPort.DispatchRetryTeamAutomationCredentialOperationAsync(
                actorId,
                normalizedOwner,
                normalizedOperationId,
                normalizedIdempotencyKey,
                requestId,
                token),
            ct);
    }

    public async Task<TeamAutomationCommittedMutationReceipt> RecordTeamAutomationCredentialCandidateAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        ScheduledInvocationAgentKeyCredentialReference credential,
        ScheduledInvocationAuthorizationOwner credentialOwner,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalizedOwner = NormalizeTeamOwner(owner);
        var normalizedOperationId = NormalizeRequired(operationId, nameof(operationId));
        var normalizedIdempotencyKey = NormalizeRequired(idempotencyKey, nameof(idempotencyKey));
        var normalizedEffectAttemptId = NormalizeRequired(effectAttemptId, nameof(effectAttemptId));
        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        return await DispatchObservedTeamOperationAsync(
            normalizedScheduleId,
            actorId,
            normalizedOperationId,
            normalizedIdempotencyKey,
            TeamAutomationOperationObservationStages.Candidate,
            (requestId, token) => _actorPort.DispatchRecordTeamAutomationCredentialCandidateAsync(
                actorId,
                normalizedOwner,
                normalizedOperationId,
                normalizedIdempotencyKey,
                normalizedEffectAttemptId,
                NormalizeScheduledInvocationAgentKey(credential),
                NormalizeAuthorizationOwner(credentialOwner),
                requestId,
                token),
            ct);
    }

    public async Task<TeamAutomationCommittedMutationReceipt> CompleteTeamAutomationCredentialOperationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        ScheduledInvocationAgentKeyCredentialReference credential,
        ScheduledDispatchConfiguration configuration,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalizedOwner = NormalizeTeamOwner(owner);
        var normalizedOperationId = NormalizeRequired(operationId, nameof(operationId));
        var normalizedIdempotencyKey = NormalizeRequired(idempotencyKey, nameof(idempotencyKey));
        var normalizedEffectAttemptId = NormalizeRequired(effectAttemptId, nameof(effectAttemptId));
        var normalizedConfiguration = await NormalizeAndAdmitMutationAsync(
            configuration with
            {
                ScheduleId = normalizedScheduleId,
                TeamAutomationOwner = normalizedOwner,
            },
            new ScheduledDispatchMutationContext(TeamAutomationOwner: normalizedOwner),
            requireScheduleId: true,
            ct);
        var existing = await _queryPort.GetAsync(normalizedScheduleId, ct);
        normalizedConfiguration = AdmitCredentialRequirement(
            normalizedConfiguration,
            existing == null
                ? ScheduledDispatchCredentialRequirementOperation.Create
                : ScheduledDispatchCredentialRequirementOperation.Update,
            existing?.Schedule);
        var dispatch = await _targetPreparationService.PrepareAsync(
            normalizedConfiguration,
            BuildScheduleCommandId(normalizedScheduleId),
            BuildScheduleCorrelationId(normalizedScheduleId),
            ct);
        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        return await DispatchObservedTeamOperationAsync(
            normalizedScheduleId,
            actorId,
            normalizedOperationId,
            normalizedIdempotencyKey,
            TeamAutomationOperationObservationStages.Complete,
            (requestId, token) => _actorPort.DispatchCompleteTeamAutomationCredentialOperationAsync(
                actorId,
                normalizedOwner,
                normalizedOperationId,
                normalizedIdempotencyKey,
                normalizedEffectAttemptId,
                NormalizeScheduledInvocationAgentKey(credential),
                normalizedConfiguration,
                dispatch,
                requestId,
                token),
            ct);
    }

    public async Task<TeamAutomationCommittedMutationReceipt> FailTeamAutomationCredentialOperationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        string errorCode,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        var normalizedOperationId = NormalizeRequired(operationId, nameof(operationId));
        var normalizedIdempotencyKey = NormalizeRequired(idempotencyKey, nameof(idempotencyKey));
        var normalizedEffectAttemptId = NormalizeRequired(effectAttemptId, nameof(effectAttemptId));
        var normalizedOwner = NormalizeTeamOwner(owner);
        var normalizedErrorCode = NormalizeStableErrorCode(errorCode);
        return await DispatchObservedTeamOperationAsync(
            normalizedScheduleId,
            actorId,
            normalizedOperationId,
            normalizedIdempotencyKey,
            TeamAutomationOperationObservationStages.Fail,
            (requestId, token) => _actorPort.DispatchFailTeamAutomationCredentialOperationAsync(
                actorId,
                normalizedOwner,
                normalizedOperationId,
                normalizedIdempotencyKey,
                normalizedEffectAttemptId,
                normalizedErrorCode,
                requestId,
                token),
            ct);
    }

    public Task<ScheduledDispatchMutationReceipt> EnableTeamAutomationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string reason,
        CancellationToken ct = default) =>
        SetTeamAutomationEnabledAsync(scheduleId, owner, reason, enabled: true, ct);

    public Task<ScheduledDispatchMutationReceipt> DisableTeamAutomationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string reason,
        CancellationToken ct = default) =>
        SetTeamAutomationEnabledAsync(scheduleId, owner, reason, enabled: false, ct);

    public async Task<TeamAutomationCommittedMutationReceipt> DeleteTeamAutomationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string reason,
        ScheduledInvocationAuthorizationOwner authenticatedCredentialOwner,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalizedOwner = NormalizeTeamOwner(owner);
        var normalizedOperationId = NormalizeRequired(operationId, nameof(operationId));
        var normalizedIdempotencyKey = NormalizeRequired(idempotencyKey, nameof(idempotencyKey));
        var existing = await GetTeamOwnedScheduleIncludingDeletedAsync(
            normalizedScheduleId,
            normalizedOwner,
            ct);
        if (existing.Schedule.Deleted &&
            (!string.Equals(existing.Schedule.TeamAutomationOperationId, normalizedOperationId, StringComparison.Ordinal) ||
             !string.Equals(existing.Schedule.TeamAutomationIdempotencyKey, normalizedIdempotencyKey, StringComparison.Ordinal)))
        {
            throw new ScheduledDispatchConflictException(
                normalizedScheduleId,
                $"Scheduled dispatch '{normalizedScheduleId}' was deleted by another operation.");
        }

        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        return await DispatchObservedTeamOperationAsync(
            normalizedScheduleId,
            actorId,
            normalizedOperationId,
            normalizedIdempotencyKey,
            TeamAutomationOperationObservationStages.Delete,
            (requestId, token) => _actorPort.DispatchDeleteTeamAutomationAsync(
                actorId,
                normalizedOwner,
                normalizedOperationId,
                normalizedIdempotencyKey,
                NormalizeOptional(reason),
                NormalizeAuthorizationOwner(authenticatedCredentialOwner),
                requestId,
                token),
            ct);
    }

    public async Task<ScheduledDispatchMutationReceipt> DeleteTeamAutomationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string reason,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalizedOwner = NormalizeTeamOwner(owner);
        var existing = await GetTeamMutableScheduleAsync(normalizedScheduleId, normalizedOwner, ct);
        if (HasTeamCredentialLifecycle(existing.Schedule))
        {
            throw new InvalidOperationException("team_automation_delete_requires_revocation_context");
        }

        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        var admission = await _actorPort.DispatchDeleteTeamAutomationAsync(
            actorId,
            normalizedOwner,
            NormalizeOptional(reason),
            ct);
        return CreateMutationReceipt(normalizedScheduleId, actorId, admission);
    }

    public async Task<TeamAutomationCommittedMutationReceipt> RetryTeamAutomationRevocationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        ScheduledInvocationAuthorizationOwner authenticatedCredentialOwner,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalizedOwner = NormalizeTeamOwner(owner);
        var existing = await GetTeamOwnedScheduleIncludingDeletedAsync(
            normalizedScheduleId,
            normalizedOwner,
            ct);
        if (!existing.Schedule.RevocationPending)
            throw new InvalidOperationException("team_automation_revocation_not_pending");

        var normalizedOperationId = NormalizeRequired(
            existing.Schedule.TeamAutomationOperationId,
            nameof(existing.Schedule.TeamAutomationOperationId));
        var normalizedIdempotencyKey = NormalizeRequired(
            existing.Schedule.TeamAutomationIdempotencyKey,
            nameof(existing.Schedule.TeamAutomationIdempotencyKey));

        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        return await DispatchObservedTeamOperationAsync(
            normalizedScheduleId,
            actorId,
            normalizedOperationId,
            normalizedIdempotencyKey,
            TeamAutomationOperationObservationStages.Delete,
            (requestId, token) => _actorPort.DispatchRetryTeamAutomationRevocationAsync(
                actorId,
                normalizedOwner,
                normalizedOperationId,
                normalizedIdempotencyKey,
                NormalizeAuthorizationOwner(authenticatedCredentialOwner),
                requestId,
                token),
            ct);
    }

    public async Task<TeamAutomationCommittedMutationReceipt> CompleteTeamAutomationRevocationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        bool nyxIdRevoked,
        bool vaultRevoked,
        string errorCode,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalizedOwner = NormalizeTeamOwner(owner);
        var normalizedOperationId = NormalizeRequired(operationId, nameof(operationId));
        var normalizedIdempotencyKey = NormalizeRequired(idempotencyKey, nameof(idempotencyKey));
        var normalizedEffectAttemptId = NormalizeRequired(effectAttemptId, nameof(effectAttemptId));
        var normalizedErrorCode = string.IsNullOrWhiteSpace(errorCode)
            ? string.Empty
            : NormalizeStableErrorCode(errorCode);
        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        return await DispatchObservedTeamOperationAsync(
            normalizedScheduleId,
            actorId,
            normalizedOperationId,
            normalizedIdempotencyKey,
            TeamAutomationOperationObservationStages.Revocation,
            (requestId, token) => _actorPort.DispatchCompleteTeamAutomationRevocationAsync(
                actorId,
                normalizedOwner,
                normalizedOperationId,
                normalizedIdempotencyKey,
                normalizedEffectAttemptId,
                nyxIdRevoked,
                vaultRevoked,
                normalizedErrorCode,
                requestId,
                token),
            ct);
    }

    public async Task<ScheduledDispatchRunNowReceipt> RunTeamAutomationNowAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var operationId = BuildBackendOperationId(normalizedScheduleId, "run-now");
        var idempotencyKey = BuildBackendIdempotencyKey(normalizedScheduleId, operationId);
        return await RunTeamAutomationNowAsync(normalizedScheduleId, owner, operationId, idempotencyKey, ct);
    }

    public async Task<ScheduledDispatchRunNowReceipt> RunTeamAutomationNowAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalizedOwner = NormalizeTeamOwner(owner);
        var detail = await GetTeamMutableScheduleAsync(normalizedScheduleId, normalizedOwner, ct);
        if (HasTeamCredentialLifecycle(detail.Schedule) &&
            detail.Schedule.TeamAutomationLifecycleStatus != TeamAutomationLifecycleStatus.Active)
        {
            throw new InvalidOperationException("team_automation_credential_not_active");
        }

        AdmitRunNowCredentialRequirement(detail.Schedule);
        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        var scheduledFireAt = DateTimeOffset.UtcNow;
        var normalizedOperationId = NormalizeRequired(operationId, nameof(operationId));
        var normalizedIdempotencyKey = NormalizeRequired(idempotencyKey, nameof(idempotencyKey));
        var admission = await _actorPort.DispatchRunTeamAutomationNowAsync(
            actorId,
            normalizedOwner,
            scheduledFireAt,
            normalizedOperationId,
            normalizedIdempotencyKey,
            ct);
        return new ScheduledDispatchRunNowReceipt(
            normalizedScheduleId,
            actorId,
            scheduledFireAt,
            normalizedIdempotencyKey,
            admission.Accepted,
            admission.CommandId,
            admission.CorrelationId,
            admission.AckedAt,
            "accepted");
    }

    public async Task<ScheduledDispatchDetail?> GetTeamScheduleAsync(
        string scheduleId,
        string scopeId,
        string? teamId = null,
        string? memberId = null,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedTeamId = NormalizeNullable(teamId);
        var normalizedMemberId = NormalizeNullable(memberId);
        var detail = await _queryPort.GetAsync(normalizedScheduleId, ct);
        return detail?.Schedule is
               {
                   Deleted: false,
                   TargetKind: ScheduledDispatchTargetKind.ServiceInvocation,
               } &&
               TeamScopeEquals(detail.Schedule, normalizedScopeId) &&
               (normalizedTeamId is null || TeamEquals(detail.Schedule, normalizedTeamId)) &&
               (normalizedMemberId is null || TeamMemberEquals(detail.Schedule, normalizedMemberId))
            ? detail
            : null;
    }

    public async Task<ScheduledDispatchDetail?> GetTeamAutomationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        CancellationToken ct = default)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalizedOwner = NormalizeTeamOwner(owner);
        var detail = await _queryPort.GetAsync(normalizedScheduleId, ct);
        return detail?.Schedule is { TeamOwned: true } &&
               IsVisibleTeamAutomationTarget(detail.Schedule) &&
               (!detail.Schedule.Deleted || detail.Schedule.RevocationPending) &&
               TeamOwnerEquals(detail.Schedule, normalizedOwner)
            ? detail
            : null;
    }

    private static bool IsVisibleTeamAutomationTarget(ScheduledDispatchSummary schedule) =>
        schedule.TargetKind == ScheduledDispatchTargetKind.ServiceInvocation ||
        schedule.TeamAutomationLifecycleStatus is
            TeamAutomationLifecycleStatus.ProvisioningPending or
            TeamAutomationLifecycleStatus.ReplacementPending;

    public async Task<ScheduledDispatchListResult> ListTeamAutomationsAsync(
        TeamMemberAutomationOwner owner,
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default)
    {
        var result = await _queryPort.ListAsync(new ScheduledDispatchListQuery(
            Take: Math.Clamp(take, 1, 200),
            Cursor: cursor,
            IncludeTotalCount: includeTotalCount,
            TargetKind: ScheduledDispatchTargetKind.ServiceInvocation,
            TeamAutomationOwner: NormalizeTeamOwner(owner),
            ExcludeCompletedTeamAutomationDeletions: true), ct);
        return result;
    }

    private async Task<ScheduledDispatchMutationReceipt> SetTeamAutomationEnabledAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string reason,
        bool enabled,
        CancellationToken ct)
    {
        var normalizedScheduleId = NormalizeScheduleId(scheduleId);
        var normalizedOwner = NormalizeTeamOwner(owner);
        _ = await GetTeamMutableScheduleAsync(normalizedScheduleId, normalizedOwner, ct);
        var actorId = await ResolveScheduleActorAsync(normalizedScheduleId, ct);
        var admission = enabled
            ? await _actorPort.DispatchEnableTeamAutomationAsync(
                actorId, normalizedOwner, NormalizeOptional(reason), ct)
            : await _actorPort.DispatchDisableTeamAutomationAsync(
                actorId, normalizedOwner, NormalizeOptional(reason), ct);
        return CreateMutationReceipt(normalizedScheduleId, actorId, admission);
    }

    private async Task<ScheduledDispatchDetail> GetTeamMutableScheduleAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        CancellationToken ct) =>
        await GetMutableScheduleAsync(scheduleId, owner, ct)
        ?? throw new ScheduledDispatchNotFoundException(scheduleId);

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
        var normalizedContext = NormalizeMutationContext(context ?? ScheduledDispatchMutationContext.None);
        var normalized = NormalizeConfiguration(configuration, requireScheduleId);
        if (normalized.TeamAutomationOwner != null && normalizedContext.TeamAutomationOwner == null)
            throw new ArgumentException("Team automation owner context is required.", nameof(context));
        if (normalized.TeamAutomationOwner != null &&
            !TeamOwnerEquals(normalized.TeamAutomationOwner, normalizedContext.TeamAutomationOwner))
        {
            throw new ArgumentException("Team automation owner context does not match the configuration.", nameof(context));
        }
        normalized = normalized with { TeamAutomationOwner = normalizedContext.TeamAutomationOwner };
        ValidateSchedule(normalized);
        return await AdmitServiceInvocationScopeOwnerAsync(
            normalized,
            normalizedContext,
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
                        Source = new ScheduledServiceInvocationNyxIdCredentialSource(
                            admittedScopeOwnerNyxId.OwnerSubject!,
                            admittedScopeOwnerNyxId.Scope,
                            ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner),
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
                : NormalizeSubject(context.AuthenticatedNyxIdOwnerSubject),
            context.TeamAutomationOwner == null
                ? null
                : NormalizeTeamOwner(context.TeamAutomationOwner),
            NormalizeExpectedServiceTarget(context.ExpectedServiceTarget));

    private static bool SubjectEquals(
        ScheduledServiceInvocationNyxIdSubjectRef left,
        ScheduledServiceInvocationNyxIdSubjectRef right) =>
        string.Equals(left.Platform, right.Platform, StringComparison.Ordinal) &&
        string.Equals(left.Tenant, right.Tenant, StringComparison.Ordinal) &&
        string.Equals(left.ExternalUserId, right.ExternalUserId, StringComparison.Ordinal);

    private static TeamMemberAutomationOwner NormalizeTeamOwner(TeamMemberAutomationOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new TeamMemberAutomationOwner(
            NormalizeRequired(owner.ScopeId, nameof(owner.ScopeId)),
            NormalizeRequired(owner.MemberId, nameof(owner.MemberId)),
            NormalizeRequired(owner.TeamId, nameof(owner.TeamId)));
    }

    private static ScheduledDispatchExpectedServiceTarget? NormalizeExpectedServiceTarget(
        ScheduledDispatchExpectedServiceTarget? target)
    {
        if (target == null)
            return null;

        return new ScheduledDispatchExpectedServiceTarget(
            target.ScheduleKind,
            target.TargetKind,
            NormalizeServiceInvocationIdentity(target.ServiceIdentity),
            NormalizeRequired(target.ServiceEndpointId, nameof(target.ServiceEndpointId)));
    }

    private static ScheduledInvocationAuthorizationOwner NormalizeAuthorizationOwner(
        ScheduledInvocationAuthorizationOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new ScheduledInvocationAuthorizationOwner(
            NormalizeRequired(owner.Authority, nameof(owner.Authority)),
            NormalizeRequired(owner.OwnerKind, nameof(owner.OwnerKind)),
            NormalizeRequired(owner.OwnerSubject, nameof(owner.OwnerSubject)));
    }

    private static TeamAutomationCredentialOperation NormalizeTeamOperation(
        TeamAutomationCredentialOperation operation)
    {
        var kind = operation.Kind is TeamAutomationOperationKind.Create or TeamAutomationOperationKind.Reauthorize
            ? operation.Kind
            : throw new ArgumentException("Team automation credential operation kind is invalid.", nameof(operation));
        return new TeamAutomationCredentialOperation(
            NormalizeScheduleId(operation.ScheduleId),
            NormalizeTeamOwner(operation.Owner),
            NormalizeRequired(operation.OperationId, nameof(operation.OperationId)),
            NormalizeRequired(operation.IdempotencyKey, nameof(operation.IdempotencyKey)),
            NormalizeRequired(operation.PermissionDigest, nameof(operation.PermissionDigest)),
            NormalizeRequired(operation.PolicyVersion, nameof(operation.PolicyVersion)),
            kind,
            NormalizeCredentialEffectLocator(operation.CredentialEffectLocator),
            NormalizeTeamActivationDecision(operation.ActivationDecision),
            NormalizeRequired(operation.MutationDigest, nameof(operation.MutationDigest)));
    }

    private static TeamAutomationActivationDecision NormalizeTeamActivationDecision(
        TeamAutomationActivationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var mode = NormalizeScheduleMode(decision.ScheduleMode);
        var callerAuthority = NormalizeCallerAuthority(decision.CallerAuthority) ??
            throw new ArgumentException("Team automation caller authority is required.", nameof(decision));
        var payload = decision.Payload?.Clone() ??
            throw new ArgumentException("Team automation payload is required.", nameof(decision));
        ValidateScheduledPrompt(payload, nameof(decision));
        return new TeamAutomationActivationDecision(
            NormalizeScheduleId(decision.ScheduleId),
            NormalizeOptional(decision.DisplayName),
            NormalizeTeamOwner(decision.Owner),
            NormalizeServiceInvocationIdentity(decision.ServiceIdentity),
            NormalizeRequired(decision.EndpointId, nameof(decision.EndpointId)),
            payload,
            callerAuthority,
            NormalizeAuthorizationFact(decision.AuthorizationFact),
            mode == ScheduledDispatchScheduleMode.RecurringCron
                ? NormalizeRequired(decision.CronExpression, nameof(decision.CronExpression))
                : string.Empty,
            ScheduledDispatchCalculator.NormalizeTimezone(decision.Timezone),
            decision.Enabled,
            decision.ScheduleKind,
            NormalizeHeaders(decision.Headers),
            mode,
            NormalizeOneShotFireAt(mode, decision.OneShotFireAt),
            decision.CredentialRequirementTargetKind,
            NormalizeOptional(decision.RevisionId),
            decision.Caller == null
                ? null
                : new ServiceInvocationCaller
                {
                    ServiceKey = NormalizeOptional(decision.Caller.ServiceKey),
                    TenantId = NormalizeOptional(decision.Caller.TenantId),
                    AppId = NormalizeOptional(decision.Caller.AppId),
                });
    }

    private static ScheduledInvocationAuthorizationFact NormalizeAuthorizationFact(
        ScheduledInvocationAuthorizationFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(fact.Disclosure);
        ArgumentNullException.ThrowIfNull(fact.Authority);
        var grants = (fact.ServiceGrants ?? [])
            .Select(static grant => new ScheduledInvocationAuthorizationServiceGrant(
                NormalizeRequired(grant.ServiceId, nameof(grant.ServiceId)),
                (grant.NodeIds ?? [])
                    .Select(static nodeId => NormalizeRequired(nodeId, nameof(nodeId)))
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                grant.NodeGrantsNotRequired))
            .OrderBy(static grant => grant.ServiceId, StringComparer.Ordinal)
            .ThenBy(static grant => grant.NodeGrantsNotRequired)
            .ThenBy(static grant => string.Join('\n', grant.NodeIds), StringComparer.Ordinal)
            .ToArray();
        return new ScheduledInvocationAuthorizationFact(
            NormalizeRequired(fact.PermissionDigest, nameof(fact.PermissionDigest)),
            NormalizeRequired(fact.PolicyVersion, nameof(fact.PolicyVersion)),
            NormalizeAuthorizationOwner(fact.Owner),
            grants,
            NormalizeOptional(fact.Scopes),
            fact.ExpiresAt.ToUniversalTime(),
            fact.ServiceGrantsNotRequired,
            new ScheduledInvocationAuthorizationDisclosure(
                fact.Disclosure.DedicatedToSchedule,
                fact.Disclosure.SecretManagedByAevatar,
                fact.Disclosure.BrowserReceivesRawKey,
                fact.Disclosure.DeleteRevokesCredential,
                fact.Disclosure.PauseResumeRevokesCredential),
            new ScheduledInvocationAuthorizationAuthority(
                fact.Authority.MemberStateVersion,
                fact.Authority.WorkflowStateVersion,
                fact.Authority.ConnectorStateVersion,
                fact.Authority.OwnerLlmStateVersion,
                fact.Authority.CatalogStateVersion,
                fact.Authority.CatalogObservedAt.ToUniversalTime(),
                fact.Authority.CatalogFreshUntil.ToUniversalTime(),
                NormalizeOptional(fact.Authority.CatalogContentDigest),
                NormalizeOptional(fact.Authority.CatalogContractVersion),
                NormalizeOptional(fact.Authority.CatalogPolicyVersion),
                fact.Authority.CatalogEvaluatedAt.ToUniversalTime()),
            fact.OwnerLLMSelection?.Clone());
    }

    private static ScheduledCredentialEffectLocator NormalizeCredentialEffectLocator(
        ScheduledCredentialEffectLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return new ScheduledCredentialEffectLocator(
            NormalizeRequired(locator.CredentialName, nameof(locator.CredentialName)),
            NormalizeRequired(locator.RequestedSecretReference, nameof(locator.RequestedSecretReference)),
            NormalizeRequired(locator.SecretPurpose, nameof(locator.SecretPurpose)),
            NormalizeRequired(locator.SecretOwnerScopeKey, nameof(locator.SecretOwnerScopeKey)),
            NormalizeAuthorizationOwner(locator.CredentialOwner));
    }

    private static bool TeamOwnerEquals(TeamMemberAutomationOwner left, TeamMemberAutomationOwner? right) =>
        right != null &&
        string.Equals(left.ScopeId, right.ScopeId, StringComparison.Ordinal) &&
        string.Equals(left.TeamId, right.TeamId, StringComparison.Ordinal) &&
        string.Equals(left.MemberId, right.MemberId, StringComparison.Ordinal);

    private static bool TeamOwnerEquals(ScheduledDispatchSummary schedule, TeamMemberAutomationOwner owner) =>
        string.Equals(schedule.TeamOwnerScopeId, owner.ScopeId, StringComparison.Ordinal) &&
        string.Equals(schedule.TeamId, owner.TeamId, StringComparison.Ordinal) &&
        string.Equals(schedule.TeamOwnerMemberId, owner.MemberId, StringComparison.Ordinal);

    private static bool TeamScopeEquals(ScheduledDispatchSummary schedule, string scopeId) =>
        string.Equals(schedule.TeamOwnerScopeId, scopeId, StringComparison.Ordinal);

    private static bool TeamEquals(ScheduledDispatchSummary schedule, string teamId) =>
        string.Equals(schedule.TeamId, teamId, StringComparison.Ordinal);

    private static bool TeamMemberEquals(ScheduledDispatchSummary schedule, string memberId) =>
        string.Equals(schedule.TeamOwnerMemberId, memberId, StringComparison.Ordinal);

    private async Task<TeamAutomationCommittedMutationReceipt> DispatchObservedTeamOperationAsync(
        string scheduleId,
        string actorId,
        string operationId,
        string idempotencyKey,
        string expectedStage,
        Func<string, CancellationToken, Task<DispatchAdmission>> dispatchAsync,
        CancellationToken ct)
    {
        if (_teamOperationObservationPreparation == null || _teamOperationObservationProjection == null)
            throw TeamAutomationOperationUnavailable();

        TeamAutomationOperationObservationScopeLeasePreparation? preparation = null;
        EventSinkProjectionAttachment<ITeamAutomationOperationObservationProjectionLease>? attachment = null;
        await using var sink = new EventChannel<TeamAutomationOperationCommittedOutcome>(8);
        TeamAutomationCommittedMutationReceipt receipt;
        try
        {
            preparation = await InvokeWithStableFailureAsync(
                    () => _teamOperationObservationPreparation.PrepareAsync(actorId, operationId, ct),
                    ct,
                    TeamAutomationCommitObservationUnavailableCode)
                .ConfigureAwait(false);
            if (preparation == null)
                throw TeamAutomationOperationUnavailable();

            attachment = await InvokeWithStableFailureAsync(
                    () => _teamOperationObservationProjection.AttachExistingOperationProjectionAsync(
                        actorId,
                        operationId,
                        sink,
                        ct),
                    ct,
                    TeamAutomationCommitObservationUnavailableCode)
                .ConfigureAwait(false);
            if (attachment == null)
                throw TeamAutomationOperationUnavailable();

            var observationRequestId = Guid.NewGuid().ToString("N");
            var admission = await InvokeWithStableFailureAsync(
                    () => dispatchAsync(observationRequestId, ct),
                    ct,
                    TeamAutomationDispatchRejectedCode)
                .ConfigureAwait(false);
            if (!admission.Accepted)
                throw new InvalidOperationException(TeamAutomationDispatchRejectedCode);

            var outcome = await ReadCorrelatedTeamAutomationOutcomeAsync(
                    sink,
                    scheduleId,
                    operationId,
                    idempotencyKey,
                    expectedStage,
                    observationRequestId,
                    ct)
                .ConfigureAwait(false);
            ThrowIfTeamAutomationOperationRejected(scheduleId, outcome);
            receipt = new TeamAutomationCommittedMutationReceipt(
                CreateMutationReceipt(scheduleId, actorId, admission),
                outcome);
        }
        finally
        {
            if (attachment != null)
            {
                await TryCleanupTeamAutomationObservationAsync(
                        () => _teamOperationObservationProjection.DetachLiveSinkAsync(
                            attachment.LiveSinkLease,
                            CancellationToken.None),
                        "detach_live_sink")
                    .ConfigureAwait(false);
                await TryCleanupTeamAutomationObservationAsync(
                        () => _teamOperationObservationProjection.ReleaseActorProjectionAsync(
                            attachment.ProjectionLease,
                            CancellationToken.None),
                        "release_projection")
                    .ConfigureAwait(false);
            }

            if (preparation != null)
            {
                await TryCleanupTeamAutomationObservationAsync(
                        () => _teamOperationObservationPreparation.ReleaseAsync(
                            preparation,
                            CancellationToken.None),
                        "release_preparation")
                    .ConfigureAwait(false);
            }
        }

        return receipt;
    }

    private static async Task<TeamAutomationOperationCommittedOutcome>
        ReadCorrelatedTeamAutomationOutcomeAsync(
            EventChannel<TeamAutomationOperationCommittedOutcome> sink,
            string scheduleId,
            string operationId,
            string idempotencyKey,
            string expectedStage,
            string observationRequestId,
            CancellationToken ct)
    {
        try
        {
            await foreach (var outcome in sink.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!string.Equals(outcome.ScheduleId, scheduleId, StringComparison.Ordinal) ||
                    !string.Equals(outcome.OperationId, operationId, StringComparison.Ordinal) ||
                    !string.Equals(outcome.IdempotencyKey, idempotencyKey, StringComparison.Ordinal) ||
                    !string.Equals(outcome.Stage, expectedStage, StringComparison.Ordinal) ||
                    !string.Equals(
                        outcome.ObservationRequestId,
                        observationRequestId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                sink.Complete();
                return outcome;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException(TeamAutomationCommitObservationEndedCode);
        }

        throw new InvalidOperationException(TeamAutomationCommitObservationEndedCode);
    }

    private static async Task<T> InvokeWithStableFailureAsync<T>(
        Func<Task<T>> operation,
        CancellationToken callerCancellationToken,
        string stableFailureCode)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException(stableFailureCode);
        }
    }

    private async Task TryCleanupTeamAutomationObservationAsync(
        Func<Task> cleanup,
        string cleanupOperation)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to clean up team automation operation observation resource {CleanupOperation}.",
                cleanupOperation);
        }
    }

    private static InvalidOperationException TeamAutomationOperationUnavailable() =>
        new(TeamAutomationCommitObservationUnavailableCode);

    private static void ThrowIfTeamAutomationOperationRejected(
        string scheduleId,
        TeamAutomationOperationCommittedOutcome outcome)
    {
        var errorCode = string.IsNullOrWhiteSpace(outcome.ErrorCode)
            ? "team_automation_operation_rejected"
            : outcome.ErrorCode;
        switch (outcome.Status)
        {
            case TeamAutomationOperationObservationStatus.Committed:
                return;
            case TeamAutomationOperationObservationStatus.RejectedConflict:
                throw new ScheduledDispatchConflictException(scheduleId, errorCode);
            case TeamAutomationOperationObservationStatus.RejectedUnauthorized:
                throw new UnauthorizedAccessException(errorCode);
            case TeamAutomationOperationObservationStatus.RejectedNotFound:
                throw new ScheduledDispatchNotFoundException(scheduleId);
            case TeamAutomationOperationObservationStatus.RejectedInvalidRequest:
                throw new InvalidOperationException(errorCode);
            default:
                throw new InvalidOperationException("team_automation_observation_status_invalid");
        }
    }

    private static string NormalizeStableErrorCode(string? value)
    {
        var normalized = NormalizeRequired(value, nameof(value));
        if (normalized.Length > 128 || normalized.Any(static c =>
                !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
        {
            throw new ArgumentException("Team automation error code must be a stable identifier.", nameof(value));
        }

        return normalized;
    }

    private async Task<ScheduledDispatchDetail> EnsureMutableAsync(
        string scheduleId,
        ScheduledDispatchMutationContext? context,
        CancellationToken ct)
    {
        var normalizedContext = NormalizeMutationContext(context ?? ScheduledDispatchMutationContext.None);
        var existing = await GetMutableScheduleAsync(scheduleId, owner: null, ct);
        if (existing?.Schedule.Deleted == true || existing == null)
            throw new ScheduledDispatchNotFoundException(scheduleId);
        EnsureExpectedServiceTarget(scheduleId, existing.Schedule, normalizedContext.ExpectedServiceTarget);
        return existing;
    }

    private static void EnsureExpectedServiceTarget(
        string scheduleId,
        ScheduledDispatchSummary? schedule,
        ScheduledDispatchExpectedServiceTarget? expectedTarget)
    {
        if (expectedTarget == null)
            return;

        if (schedule == null ||
            schedule.ScheduleKind != expectedTarget.ScheduleKind ||
            schedule.TargetKind != expectedTarget.TargetKind ||
            !string.Equals(schedule.ServiceEndpointId, expectedTarget.ServiceEndpointId, StringComparison.Ordinal) ||
            !ProjectedServiceIdentityEquals(schedule, expectedTarget.ServiceIdentity))
        {
            throw new ScheduledDispatchNotFoundException(scheduleId);
        }
    }

    private static bool ProjectedServiceIdentityEquals(
        ScheduledDispatchSummary schedule,
        ServiceIdentity expectedIdentity)
    {
        if (!IsEmptyServiceIdentity(schedule.ServiceIdentity))
            return ServiceIdentityEquals(schedule.ServiceIdentity, expectedIdentity);

        // Documents projected before service_identity was introduced retain the
        // canonical service key and service id. This compatibility path is only
        // for workflow-owned routes; generic schedule routes remain fail-closed.
        return schedule.ScheduleKind == ScheduledDispatchScheduleKind.Workflow &&
               string.Equals(schedule.ServiceId, expectedIdentity.ServiceId, StringComparison.Ordinal) &&
               string.Equals(
                   schedule.ServiceKey,
                   ServiceKeys.Build(expectedIdentity),
                   StringComparison.Ordinal);
    }

    private static bool IsEmptyServiceIdentity(ServiceIdentity? identity) =>
        identity == null ||
        (string.IsNullOrEmpty(identity.TenantId) &&
         string.IsNullOrEmpty(identity.AppId) &&
         string.IsNullOrEmpty(identity.Namespace) &&
         string.IsNullOrEmpty(identity.ServiceId));

    private static bool ServiceIdentityEquals(ServiceIdentity? left, ServiceIdentity? right) =>
        left != null &&
        right != null &&
        string.Equals(left.TenantId, right.TenantId, StringComparison.Ordinal) &&
        string.Equals(left.AppId, right.AppId, StringComparison.Ordinal) &&
        string.Equals(left.Namespace, right.Namespace, StringComparison.Ordinal) &&
        string.Equals(left.ServiceId, right.ServiceId, StringComparison.Ordinal);

    private async Task<ScheduledDispatchDetail?> GetMutableScheduleAsync(
        string scheduleId,
        TeamMemberAutomationOwner? owner,
        CancellationToken ct)
    {
        var existing = await _queryPort.GetAsync(scheduleId, ct);
        if (existing is not null &&
            existing.Schedule.TargetKind != ScheduledDispatchTargetKind.ServiceInvocation)
        {
            throw new ScheduledDispatchNotFoundException(scheduleId);
        }
        if (existing?.Schedule.Deleted == true)
            throw new ScheduledDispatchNotFoundException(scheduleId);
        if (existing?.Schedule.TeamOwned == true)
        {
            if (owner == null || !TeamOwnerEquals(existing.Schedule, owner))
                throw new ScheduledDispatchNotFoundException(scheduleId);
        }
        else if (owner != null && existing != null)
        {
            throw new ScheduledDispatchNotFoundException(scheduleId);
        }

        return existing;
    }

    private async Task<ScheduledDispatchDetail> GetTeamOwnedScheduleIncludingDeletedAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        CancellationToken ct)
    {
        var existing = await _queryPort.GetAsync(scheduleId, ct);
        if (existing?.Schedule is not
            {
                TeamOwned: true,
                TargetKind: ScheduledDispatchTargetKind.ServiceInvocation,
            } || !TeamOwnerEquals(existing.Schedule, owner))
        {
            throw new ScheduledDispatchNotFoundException(scheduleId);
        }
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

    private static bool HasTeamCredentialLifecycle(ScheduledDispatchSummary schedule) =>
        schedule.TeamAutomationLifecycleStatus != TeamAutomationLifecycleStatus.Unspecified ||
        schedule.CredentialGeneration > 0 ||
        schedule.CredentialExpiresAt != null ||
        schedule.RevocationPending ||
        !string.IsNullOrWhiteSpace(schedule.TeamAutomationOperationId) ||
        !string.IsNullOrWhiteSpace(schedule.TeamAutomationIdempotencyKey) ||
        !string.IsNullOrWhiteSpace(schedule.CredentialOwnerAuthority) ||
        !string.IsNullOrWhiteSpace(schedule.CredentialOwnerKind) ||
        !string.IsNullOrWhiteSpace(schedule.CredentialOwnerSubject);

    private static ScheduledInvocationAuthorizationOwner ResolveActiveCredentialOwner(
        ScheduledDispatchSummary schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule.CredentialOwnerAuthority) ||
            string.IsNullOrWhiteSpace(schedule.CredentialOwnerKind) ||
            string.IsNullOrWhiteSpace(schedule.CredentialOwnerSubject))
        {
            throw new InvalidOperationException("team_automation_credential_owner_missing");
        }

        return new ScheduledInvocationAuthorizationOwner(
            schedule.CredentialOwnerAuthority,
            schedule.CredentialOwnerKind,
            schedule.CredentialOwnerSubject);
    }

    private static string BuildBackendOperationId(string scheduleId, string operation) =>
        $"schedule-{NormalizeRequired(operation, nameof(operation))}-{NormalizeScheduleId(scheduleId)}-{Guid.NewGuid():N}";

    private static string BuildBackendIdempotencyKey(string scheduleId, string operationId) =>
        $"schedule:{NormalizeScheduleId(scheduleId)}:{NormalizeRequired(operationId, nameof(operationId))}";

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
            ScheduledDispatchTargetKind.ServiceInvocation => NormalizeServiceInvocationTarget(target),
            ScheduledDispatchTargetKind.Envelope => throw new ArgumentException(
                "Raw envelope scheduled dispatch targets are not supported by the Application contract.",
                nameof(target)),
            _ => throw new ArgumentException($"Unsupported scheduled dispatch target kind '{target.Kind}'.", nameof(target)),
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
                AuthorizationFact = invocation.AuthorizationFact,
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
            ScheduledServiceInvocationNyxIdCredentialSource nyxId => NormalizeNyxIdAuth(nyxId, auth),
            ScheduledServiceInvocationDurableCredentialReference durable =>
                new ScheduledServiceInvocationAuth(NormalizeDurableCredentialReference(durable))
                {
                    CallerAuthority = NormalizeCallerAuthority(auth.CallerAuthority),
                },
            ScheduledInvocationAgentKeyCredentialReference agentKey =>
                new ScheduledServiceInvocationAuth(NormalizeScheduledInvocationAgentKey(agentKey))
                {
                    CallerAuthority = NormalizeCallerAuthority(auth.CallerAuthority),
                },
            _ => throw new ArgumentException("Unsupported service invocation credential source.", nameof(auth)),
        };
    }

    private static ScheduledCallerNyxIdAuthority? NormalizeCallerAuthority(
        ScheduledCallerNyxIdAuthority? authority) =>
        authority == null
            ? null
            : new ScheduledCallerNyxIdAuthority
            {
                Platform = NormalizeRequired(authority.Platform, nameof(authority.Platform)),
                Tenant = NormalizeNullable(authority.Tenant) ?? string.Empty,
                ExternalUserId = NormalizeRequired(authority.ExternalUserId, nameof(authority.ExternalUserId)),
                Scope = NormalizeRequired(authority.Scope, nameof(authority.Scope)),
                BindingId = NormalizeRequired(authority.BindingId, nameof(authority.BindingId)),
            };

    private static ScheduledServiceInvocationAuth NormalizeNyxIdAuth(
        ScheduledServiceInvocationNyxIdCredentialSource source,
        ScheduledServiceInvocationAuth auth)
    {
        var role = source.Role switch
        {
            ScheduledServiceInvocationNyxIdCredentialRole.Sender => ScheduledServiceInvocationNyxIdCredentialRole.Sender,
            ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner => ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner,
            _ => throw new ArgumentException("Service invocation NyxID credential role is required.", nameof(auth)),
        };

        if (source.Subject == null)
        {
            if (role == ScheduledServiceInvocationNyxIdCredentialRole.Sender)
                throw new ArgumentException(ToMissingSubjectMessage(role), nameof(auth));

            return new ScheduledServiceInvocationAuth(
                new ScheduledServiceInvocationNyxIdCredentialSource(
                    null!,
                    NormalizeRequired(source.Scope, nameof(source.Scope)),
                    role))
            {
                CallerAuthority = NormalizeCallerAuthority(auth.CallerAuthority),
            };
        }

        return new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            NormalizeSubject(source.Subject),
            NormalizeRequired(source.Scope, nameof(source.Scope)),
            role))
        {
            CallerAuthority = NormalizeCallerAuthority(auth.CallerAuthority),
        };
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

    private static string ToMissingSubjectMessage(ScheduledServiceInvocationNyxIdCredentialRole role) =>
        role == ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner
            ? "Service invocation scope owner NyxID subject is required."
            : "Service invocation sender NyxID subject is required.";

    private static ScheduledServiceInvocationNyxIdSubjectRef NormalizeSubject(
        ScheduledServiceInvocationNyxIdSubjectRef subject) =>
        new(
            NormalizeRequired(subject.Platform, nameof(subject.Platform)),
            NormalizeOptional(subject.Tenant),
            NormalizeRequired(subject.ExternalUserId, nameof(subject.ExternalUserId)));

    private static void ValidateSchedule(ScheduledDispatchConfiguration configuration)
    {
        ValidateScheduledPrompt(
            configuration.Target.ServiceInvocation?.Payload,
            nameof(configuration));
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

    private static void ValidateScheduledPrompt(Any? payload, string parameterName)
    {
        var validation = ScheduledDispatchPromptTemplate.ValidatePayload(payload);
        if (!validation.Succeeded)
            throw new ArgumentException(validation.Error, parameterName);
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
