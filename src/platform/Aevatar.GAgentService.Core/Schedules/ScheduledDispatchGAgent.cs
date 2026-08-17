using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Core.Schedules;

[GAgent("gagent.service.scheduled-dispatch")]
public sealed class ScheduledDispatchGAgent : GAgentBase<ScheduledDispatchState>
{
    private const string NextFireCallbackId = "scheduled-dispatch-next-fire";
    private const string TeamCredentialExpiryCallbackId = "scheduled-dispatch-team-credential-expiry";
    private const int MaxFireRecordCount = 128;
    // How overdue an armed occurrence must be, when the actor reactivates, before it counts as
    // a detected miss. Wide enough that routine reactivation catch-up (pod churn at the boundary
    // is seconds-to-minutes late) is not flagged, tight enough that genuine drops (production
    // misses run 90+ minutes) always are.
    private static readonly TimeSpan OverdueFireGracePeriod = TimeSpan.FromMinutes(10);
    private const string LegacyDurableSenderBearerBlockedError =
        "Scheduled service invocation contains legacy durable bearer auth; reconfigure the schedule with senderNyxId or scopeOwnerNyxId.";
    private const string LegacyUnmarkedEnvelopeRetiredError =
        "Scheduled dispatch envelope target is retired because it lacks trusted internal authority.";
    private const string LegacyUnmarkedEnvelopeRetiredReason =
        "legacy_unmarked_envelope_target_retired";
    private static readonly TimeSpan MaxNextFireCallbackHop = TimeSpan.FromDays(7);
    internal static readonly TimeSpan TeamAutomationEffectAttemptLeaseDuration = TimeSpan.FromMinutes(5);
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IScheduledServiceInvocationDispatchPort _serviceInvocationDispatchPort;
    private readonly IScheduledDispatchCredentialRequirementPolicy _credentialRequirementPolicy;
    private readonly TimeProvider _timeProvider;

    public ScheduledDispatchGAgent(
        IActorDispatchPort dispatchPort,
        IScheduledServiceInvocationDispatchPort serviceInvocationDispatchPort,
        IScheduledDispatchCredentialRequirementPolicy credentialRequirementPolicy,
        TimeProvider? timeProvider = null)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _serviceInvocationDispatchPort = serviceInvocationDispatchPort
            ?? throw new ArgumentNullException(nameof(serviceInvocationDispatchPort));
        _credentialRequirementPolicy = credentialRequirementPolicy
            ?? throw new ArgumentNullException(nameof(credentialRequirementPolicy));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        if (State.Deleted)
        {
            await PurgeDurableCallbacksAsync(ct);
            return;
        }

        if (await RetireUnmarkedEnvelopeTargetAsync(ct))
            return;

        await RecoverTeamCredentialExpiryAsync(ct);
        if (CanScheduleAutomaticFire())
        {
            await DetectOverdueArmedFireAsync(DateTimeOffset.UtcNow, ct);
            if (State.PendingNextFireAt != null)
            {
                var pendingNextFireAt = State.PendingNextFireAt.ToDateTimeOffset();
                var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
                await ActivateNextFireIntentAsync(pendingNextFireAt, previousLease, ct);
            }
            else if (State.NextFireAt != null)
            {
                // A fire is already armed for State.NextFireAt. Re-arm for that exact time
                // instead of computing from now: if reactivation happens at or after the armed
                // time (pod churn at the boundary), recomputing from now would silently skip the
                // due occurrence. Re-arming a past armed time fires it immediately (catch-up); the
                // fire handler then advances to the next occurrence normally.
                var armedNextFireAt = State.NextFireAt.Value;
                var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
                await ActivateNextFireIntentAsync(armedNextFireAt, previousLease, ct);
            }
            else
            {
                await EnsureNextFireScheduledAsync(DateTimeOffset.UtcNow, ct);
            }
        }
    }

    public override Task<string> GetDescriptionAsync()
    {
        var scheduleId = string.IsNullOrWhiteSpace(State.ScheduleId) ? Id : State.ScheduleId;
        var status = State.Enabled ? "enabled" : "disabled";
        return Task.FromResult($"ScheduledDispatchGAgent[{scheduleId}] {status}");
    }

    protected override ScheduledDispatchState TransitionState(ScheduledDispatchState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScheduledDispatchConfiguredEvent>(ApplyConfigured)
            .On<ScheduledDispatchEnabledEvent>(ApplyEnabled)
            .On<ScheduledDispatchDisabledEvent>(ApplyDisabled)
            .On<ScheduledDispatchDeletedEvent>(ApplyDeleted)
            .On<ScheduledDispatchCompletedEvent>(ApplyCompleted)
            .On<ScheduledDispatchNextFireIntentRecordedEvent>(ApplyNextFireIntentRecorded)
            .On<ScheduledDispatchNextFireScheduledEvent>(ApplyNextFireScheduled)
            .On<ScheduledDispatchFireStartedEvent>(ApplyFireStarted)
            .On<ScheduledDispatchFireDispatchedEvent>(ApplyFireDispatched)
            .On<ScheduledDispatchFireFailedEvent>(ApplyFireFailed)
            .On<ScheduledDispatchFireOverdueDetectedEvent>(ApplyFireOverdueDetected)
            .On<TeamAutomationCredentialOperationBeganEvent>(ApplyTeamAutomationCredentialOperationBegan)
            .On<TeamAutomationCredentialCandidateRecordedEvent>(ApplyTeamAutomationCredentialCandidateRecorded)
            .On<TeamAutomationCredentialActivatedEvent>(ApplyTeamAutomationCredentialActivated)
            .On<TeamAutomationCredentialOperationFailedEvent>(ApplyTeamAutomationCredentialOperationFailed)
            .On<TeamAutomationDeletionRequestedEvent>(ApplyTeamAutomationDeletionRequested)
            .On<TeamAutomationRevocationCompletedEvent>(ApplyTeamAutomationRevocationCompleted)
            .On<TeamAutomationAuthorizationRequiredEvent>(ApplyTeamAutomationAuthorizationRequired)
            .On<TeamAutomationCredentialExpiryIntentRecordedEvent>(ApplyTeamAutomationCredentialExpiryIntentRecorded)
            .On<TeamAutomationCredentialExpiryScheduledEvent>(ApplyTeamAutomationCredentialExpiryScheduled)
            .On<TeamAutomationOperationObservedEvent>(ApplyTeamAutomationOperationObserved)
            .OrCurrent();

    [EventHandler]
    public Task HandleConfigureAsync(ScheduledDispatchCreateCommand command) =>
        HandleConfigureAsync(
            command,
            command.ScheduleId,
            command.DisplayName,
            command.TargetActorId,
            command.TriggerEnvelope,
            command.CronExpression,
            command.Timezone,
            command.Enabled,
            command.Headers,
            command.Target,
            command.ScheduleKind,
            command.ScheduleMode,
            command.OneShotFireAt,
            command.TeamAutomationOwner,
            expectedServiceTarget: null,
            isCreate: true);

    [EventHandler]
    public Task HandleConfigureAsync(ScheduledDispatchUpdateCommand command) =>
        HandleConfigureAsync(
            command,
            command.ScheduleId,
            command.DisplayName,
            command.TargetActorId,
            command.TriggerEnvelope,
            command.CronExpression,
            command.Timezone,
            command.Enabled,
            command.Headers,
            command.Target,
            command.ScheduleKind,
            command.ScheduleMode,
            command.OneShotFireAt,
            command.TeamAutomationOwner,
            command.ExpectedServiceTarget,
            isCreate: false);

    [EventHandler]
    public async Task HandleEnsureAsync(ScheduledDispatchEnsureCommand command)
    {
        EnsureTeamAutomationOwnerAccess(command.TeamAutomationOwner, "ensure");
        if (!IsConfigured())
        {
            await HandleConfigureAsync(
                command,
                command.ScheduleId,
                command.DisplayName,
                command.TargetActorId,
                command.TriggerEnvelope,
                command.CronExpression,
                command.Timezone,
                command.Enabled,
                command.Headers,
                command.Target,
                command.ScheduleKind,
                command.ScheduleMode,
                command.OneShotFireAt,
                command.TeamAutomationOwner,
                expectedServiceTarget: null,
                isCreate: true);
            return;
        }

        EnsureValidDefinition(
            command.TargetActorId,
            command.Target,
            command.TriggerEnvelope,
            command.CronExpression,
            command.Timezone,
            command.ScheduleKind,
            command.ScheduleMode,
            command.OneShotFireAt);
        if (MatchesConfiguredDefinition(command))
            return;

        await HandleConfigureAsync(
            command,
            command.ScheduleId,
            command.DisplayName,
            command.TargetActorId,
            command.TriggerEnvelope,
            command.CronExpression,
            command.Timezone,
            command.Enabled,
            command.Headers,
            command.Target,
            command.ScheduleKind,
            command.ScheduleMode,
            command.OneShotFireAt,
            command.TeamAutomationOwner,
            expectedServiceTarget: null,
            isCreate: false);
    }

    private async Task HandleConfigureAsync(
        IMessage command,
        string scheduleId,
        string displayName,
        string? targetActorId,
        EventEnvelope triggerEnvelope,
        string cronExpression,
        string timezone,
        bool enabled,
        IEnumerable<KeyValuePair<string, string>> headers,
        ScheduledDispatchTargetState? target,
        ScheduledDispatchScheduleKindState scheduleKind,
        ScheduledDispatchScheduleModeState scheduleMode,
        Timestamp? oneShotFireAt,
        TeamMemberAutomationOwnerState? teamAutomationOwner,
        ScheduledDispatchExpectedServiceTargetState? expectedServiceTarget,
        bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (State.Deleted)
            throw new InvalidOperationException($"Scheduled dispatch '{ResolveScheduleId()}' is deleted.");
        if (isCreate && IsConfigured())
            throw new InvalidOperationException($"Scheduled dispatch '{ResolveScheduleId()}' already exists.");
        if (!isCreate && !IsConfigured())
            throw new InvalidOperationException($"Scheduled dispatch '{ResolveScheduleId()}' is not configured.");
        if (!isCreate)
            EnsureExpectedServiceTargetAccess(expectedServiceTarget);
        EnsureTeamAutomationOwnerAccess(teamAutomationOwner, "configure", allowUnconfiguredOwner: isCreate);
        if (!isCreate &&
            State.TeamAutomationOwner != null &&
            State.TeamAutomationLifecycleStatus == TeamAutomationLifecycleStatusState.ReplacementPending)
        {
            throw new InvalidOperationException("team_automation_replacement_pending");
        }

        EnsureValidDefinition(targetActorId, target, triggerEnvelope, cronExpression, timezone, scheduleKind, scheduleMode, oneShotFireAt);

        var now = DateTimeOffset.UtcNow;
        var normalizedMode = NormalizeScheduleMode(scheduleMode);
        var normalizedOneShotFireAt = NormalizeOneShotFireAt(normalizedMode, oneShotFireAt);
        var configuredTarget = PreserveExistingServiceInvocationAuth(
            NormalizeTarget(target, scheduleKind),
            isCreate);
        EnsureCredentialRequirementAllowed(
            ResolveCredentialRequirementOperation(command),
            NormalizeRequired(scheduleId, nameof(scheduleId)),
            scheduleKind,
            configuredTarget,
            headers);
        Logger.LogInformation(
            "Scheduled dispatch configuration prepared. scheduleId={ScheduleId} isCreate={IsCreate} targetKind={TargetKind} scheduleKind={ScheduleKind} credentialRequirementTargetKind={CredentialRequirementTargetKind} hasServiceInvocationAuth={HasServiceInvocationAuth} hasScopeOwnerNyxId={HasScopeOwnerNyxId} hasSenderNyxId={HasSenderNyxId} hasDurableCredentialReference={HasDurableCredentialReference} hasScheduledInvocationAgentKey={HasScheduledInvocationAgentKey} hasLegacyDurableSenderBearerBlocked={HasLegacyDurableSenderBearerBlocked}",
            NormalizeRequired(scheduleId, nameof(scheduleId)),
            isCreate,
            configuredTarget.Kind,
            scheduleKind,
            configuredTarget.CredentialRequirementTargetKind,
            HasServiceInvocationAuth(configuredTarget),
            HasScopeOwnerNyxId(configuredTarget),
            HasSenderNyxId(configuredTarget),
            HasDurableCredentialReference(configuredTarget),
            HasScheduledInvocationAgentKey(configuredTarget),
            HasLegacyDurableSenderBearerBlocked(configuredTarget));
        var configured = new ScheduledDispatchConfiguredEvent
        {
            ScheduleId = NormalizeRequired(scheduleId, nameof(scheduleId)),
            DisplayName = NormalizeOptional(displayName),
            TargetActorId = NormalizeOptional(targetActorId),
            TriggerEnvelope = NormalizeTriggerEnvelope(triggerEnvelope),
            CronExpression = NormalizeCronExpression(normalizedMode, cronExpression),
            Timezone = ScheduledDispatchCalculator.NormalizeTimezone(timezone),
            Enabled = enabled,
            ConfiguredAt = Timestamp.FromDateTimeOffset(now),
            PayloadTypeUrl = ResolvePayloadTypeUrl(triggerEnvelope),
            Target = configuredTarget,
            ScheduleKind = scheduleKind,
            ScheduleMode = normalizedMode,
            OneShotFireAt = normalizedOneShotFireAt.HasValue
                ? Timestamp.FromDateTimeOffset(normalizedOneShotFireAt.Value)
                : null,
            TeamAutomationOwner = teamAutomationOwner?.Clone(),
        };
        foreach (var (key, value) in NormalizeHeaders(headers))
            configured.Headers[key] = value;

        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        await PersistDomainEventAsync(configured);

        if (enabled)
            await EnsureNextFireScheduledAsync(now, CancellationToken.None);
        else
            await CancelNextFireLeaseAsync(previousLease, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleEnableAsync(ScheduledDispatchEnableCommand command)
    {
        EnsureConfiguredForWrite("enable");
        EnsureExpectedServiceTargetAccess(command.ExpectedServiceTarget);
        EnsureTeamAutomationOwnerAccess(command.TeamAutomationOwner, "enable");
        if (HasTeamCredentialLifecycle() &&
            State.TeamAutomationLifecycleStatus != TeamAutomationLifecycleStatusState.Active)
        {
            throw new InvalidOperationException("team_automation_credential_not_active");
        }

        await PersistDomainEventAsync(new ScheduledDispatchEnabledEvent
        {
            Reason = NormalizeOptional(command.Reason),
            EnabledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ScheduleId = ResolveScheduleId(),
            ScopeId = ResolveScheduleScopeId(),
        });
        await EnsureNextFireScheduledAsync(DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleDisableAsync(ScheduledDispatchDisableCommand command)
    {
        EnsureConfiguredForWrite("disable");
        EnsureExpectedServiceTargetAccess(command.ExpectedServiceTarget);
        EnsureTeamAutomationOwnerAccess(command.TeamAutomationOwner, "disable");
        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        await PersistDomainEventAsync(new ScheduledDispatchDisabledEvent
        {
            Reason = NormalizeOptional(command.Reason),
            DisabledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ScheduleId = ResolveScheduleId(),
            ScopeId = ResolveScheduleScopeId(),
        });
        await CancelNextFireLeaseAsync(previousLease, CancellationToken.None);
    }

    [EventHandler]
    public Task HandleDeleteAsync(ScheduledDispatchDeleteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteObservedTeamAutomationCommandAsync(
            ResolveScheduleId(),
            command.OperationId,
            command.IdempotencyKey,
            TeamAutomationOperationObservationStages.Delete,
            command.ObservationRequestId,
            () => HandleDeleteCoreAsync(command));
    }

    private async Task HandleDeleteCoreAsync(ScheduledDispatchDeleteCommand command)
    {
        EnsureExpectedServiceTargetAccess(command.ExpectedServiceTarget);
        var normalizedReason = NormalizeOptional(command.Reason);
        var exactDeleteReplayState =
            State.TeamAutomationOperationKind ==
                TeamAutomationOperationKindState.Delete;
        if (exactDeleteReplayState)
        {
            TeamMemberAutomationOwnerState normalizedTeamAutomationOwner;
            try
            {
                normalizedTeamAutomationOwner =
                    NormalizeTeamAutomationOwner(command.TeamAutomationOwner);
            }
            catch (InvalidOperationException)
            {
                throw TeamAutomationCommandRejectedException.Conflict(
                    "team_automation_operation_conflict");
            }
            catch (ArgumentException)
            {
                throw TeamAutomationCommandRejectedException.Conflict(
                    "team_automation_operation_conflict");
            }

            if (!IsSameDeleteOperation(
                    command,
                    normalizedTeamAutomationOwner,
                    normalizedReason))
            {
                throw TeamAutomationCommandRejectedException.Conflict(
                    "team_automation_operation_conflict");
            }

            EnsureObservedCredentialAuthorizationOwnerAccess(
                command.AuthenticatedCredentialOwner,
                State.TeamCredentialEffectLocator?.CredentialOwner);
            var healingPartialDelete = !State.Deleted;
            if (healingPartialDelete)
            {
                await PersistDomainEventAsync(new ScheduledDispatchDeletedEvent
                {
                    Reason = normalizedReason,
                    DeletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    ScheduleId = ResolveScheduleId(),
                    ScopeId = ResolveScheduleScopeId(),
                });
            }
            await PurgeDurableCallbacksAsync(CancellationToken.None);
            await PersistTeamAutomationObservationAsync(
                TeamAutomationOperationObservationStages.Delete,
                State.PendingRevocationTeamCredential != null &&
                CanClaimTeamAutomationEffectAttempt(_timeProvider.GetUtcNow()),
                CancellationToken.None,
                observationRequestId: command.ObservationRequestId);
            return;
        }

        if (State.TeamAutomationLifecycleStatus is
                TeamAutomationLifecycleStatusState.ProvisioningPending or
                TeamAutomationLifecycleStatusState.ReplacementPending ||
            State.CandidateTeamCredential != null)
        {
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_operation_in_progress");
        }
        if (State.TeamAutomationLifecycleStatus == TeamAutomationLifecycleStatusState.RevocationPending ||
            State.PendingRevocationTeamCredential != null)
        {
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_revocation_in_progress");
        }

        EnsureConfiguredForWrite("delete");
        EnsureTeamAutomationOwnerAccess(command.TeamAutomationOwner, "delete");
        var deletionEvents = new List<IMessage>();
        var deletedAt = DateTimeOffset.UtcNow;
        if (HasTeamCredentialLifecycle())
        {
            EnsureObservedTeamAutomationOwnerAccess(command.TeamAutomationOwner);
            var credentialOwner = State.ActiveTeamCredentialOwner ??
                State.TeamCredentialEffectLocator?.CredentialOwner;
            EnsureObservedCredentialAuthorizationOwnerAccess(
                command.AuthenticatedCredentialOwner,
                credentialOwner);
            deletionEvents.Add(new TeamAutomationDeletionRequestedEvent
            {
                Owner = State.TeamAutomationOwner.Clone(),
                OperationId = NormalizeRequired(command.OperationId, nameof(command.OperationId)),
                IdempotencyKey = NormalizeRequired(command.IdempotencyKey, nameof(command.IdempotencyKey)),
                PendingRevocationCredential = State.ActiveTeamCredential?.Clone(),
                PendingRevocationCredentialOwner = State.ActiveTeamCredentialOwner?.Clone(),
                OccurredAt = Timestamp.FromDateTimeOffset(deletedAt),
                Reason = normalizedReason,
            });
        }
        deletionEvents.Add(new ScheduledDispatchDeletedEvent
        {
            Reason = normalizedReason,
            DeletedAt = Timestamp.FromDateTimeOffset(deletedAt),
            ScheduleId = ResolveScheduleId(),
            ScopeId = ResolveScheduleScopeId(),
        });
        await PersistDomainEventsAsync(deletionEvents);
        await PurgeDurableCallbacksAsync(CancellationToken.None);
        await PersistTeamAutomationObservationAsync(
            TeamAutomationOperationObservationStages.Delete,
            State.PendingRevocationTeamCredential != null,
            CancellationToken.None,
            observationRequestId: command.ObservationRequestId);
    }

    [EventHandler]
    public Task HandleBeginTeamAutomationCredentialOperationAsync(
        BeginTeamAutomationCredentialOperationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteObservedTeamAutomationCommandAsync(
            command.ScheduleId,
            command.OperationId,
            command.IdempotencyKey,
            TeamAutomationOperationObservationStages.Begin,
            command.ObservationRequestId,
            () => HandleBeginTeamAutomationCredentialOperationCoreAsync(command));
    }

    [EventHandler]
    public Task HandleRetryTeamAutomationCredentialOperationAsync(
        RetryTeamAutomationCredentialOperationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteObservedTeamAutomationCommandAsync(
            ResolveScheduleId(),
            command.OperationId,
            command.IdempotencyKey,
            TeamAutomationOperationObservationStages.Begin,
            command.ObservationRequestId,
            () => HandleRetryTeamAutomationCredentialOperationCoreAsync(command));
    }

    private async Task HandleRetryTeamAutomationCredentialOperationCoreAsync(
        RetryTeamAutomationCredentialOperationCommand command)
    {
        var owner = NormalizeTeamAutomationOwner(command.Owner);
        EnsureObservedTeamAutomationOwnerAccess(owner);
        EnsureCurrentTeamAutomationOperation(command.OperationId, command.IdempotencyKey);
        if (State.TeamAutomationLifecycleStatus is not (
                TeamAutomationLifecycleStatusState.ProvisioningPending or
                TeamAutomationLifecycleStatusState.ReplacementPending))
        {
            throw TeamAutomationCommandRejectedException.Conflict(
                "team_automation_operation_not_pending");
        }

        await PersistTeamAutomationObservationAsync(
            TeamAutomationOperationObservationStages.Begin,
            CanClaimTeamAutomationEffectAttempt(_timeProvider.GetUtcNow()),
            CancellationToken.None,
            observationRequestId: command.ObservationRequestId);
    }

    private async Task HandleBeginTeamAutomationCredentialOperationCoreAsync(
        BeginTeamAutomationCredentialOperationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var scheduleId = NormalizeRequired(command.ScheduleId, nameof(command.ScheduleId));
        var owner = NormalizeTeamAutomationOwner(command.Owner);
        var operationId = NormalizeRequired(command.OperationId, nameof(command.OperationId));
        var idempotencyKey = NormalizeRequired(command.IdempotencyKey, nameof(command.IdempotencyKey));
        var permissionDigest = NormalizeRequired(command.PermissionDigest, nameof(command.PermissionDigest));
        var policyVersion = NormalizeRequired(command.PolicyVersion, nameof(command.PolicyVersion));
        var credentialEffectLocator = NormalizeCredentialEffectLocator(command.CredentialEffectLocator);
        var activationDecision = NormalizeTeamAutomationActivationDecision(command.ActivationDecision);
        var mutationDigest = NormalizeRequired(command.MutationDigest, nameof(command.MutationDigest));
        if (command.OperationKind is not (TeamAutomationOperationKindState.Create or
            TeamAutomationOperationKindState.Reauthorize))
        {
            throw new ArgumentException("Team automation credential operation kind is invalid.", nameof(command));
        }

        if (State.Deleted)
            throw TeamAutomationCommandRejectedException.NotFound("team_automation_schedule_deleted");
        if (IsConfigured() && State.TeamAutomationOwner == null)
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_owner_conflict");
        if (!string.IsNullOrWhiteSpace(State.ScheduleId) &&
            !string.Equals(State.ScheduleId, scheduleId, StringComparison.Ordinal))
        {
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_schedule_id_conflict");
        }

        EnsureStableTeamAutomationOwner(owner);
        EnsureValidTeamAutomationActivationDecision(
            activationDecision,
            scheduleId,
            owner,
            permissionDigest,
            policyVersion);
        if (IsExactTeamAutomationOperation(
                owner,
                operationId,
                idempotencyKey,
                permissionDigest,
                policyVersion,
                command.OperationKind,
                credentialEffectLocator,
                activationDecision,
                mutationDigest))
        {
            var ownsEffectAttempt = (State.TeamAutomationLifecycleStatus is
                    TeamAutomationLifecycleStatusState.ProvisioningPending or
                    TeamAutomationLifecycleStatusState.ReplacementPending) &&
                CanClaimTeamAutomationEffectAttempt(_timeProvider.GetUtcNow());
            await PersistTeamAutomationObservationAsync(
                TeamAutomationOperationObservationStages.Begin,
                ownsEffectAttempt,
                CancellationToken.None,
                observationRequestId: command.ObservationRequestId);
            return;
        }

        if (string.Equals(State.TeamAutomationOperationId, operationId, StringComparison.Ordinal) ||
            string.Equals(State.TeamAutomationIdempotencyKey, idempotencyKey, StringComparison.Ordinal))
        {
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_operation_conflict");
        }

        if (State.TeamAutomationLifecycleStatus is TeamAutomationLifecycleStatusState.ProvisioningPending or
            TeamAutomationLifecycleStatusState.ReplacementPending or
            TeamAutomationLifecycleStatusState.Deleting or
            TeamAutomationLifecycleStatusState.RevocationPending)
        {
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_operation_in_progress");
        }
        if (State.PendingRevocationTeamCredential != null)
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_revocation_in_progress");

        if (command.OperationKind == TeamAutomationOperationKindState.Create && State.ActiveTeamCredential != null)
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_credential_already_active");
        if (command.OperationKind == TeamAutomationOperationKindState.Reauthorize && State.ActiveTeamCredential == null)
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_credential_not_active");
        if (command.OperationKind == TeamAutomationOperationKindState.Reauthorize &&
            !CredentialAuthorizationOwnerEquals(
                State.ActiveTeamCredentialOwner,
                credentialEffectLocator.CredentialOwner))
        {
            throw TeamAutomationCommandRejectedException.Unauthorized(
                "team_automation_credential_owner_mismatch");
        }

        await PersistDomainEventAsync(new TeamAutomationCredentialOperationBeganEvent
        {
            ScheduleId = scheduleId,
            Owner = owner,
            OperationId = operationId,
            IdempotencyKey = idempotencyKey,
            PermissionDigest = permissionDigest,
            PolicyVersion = policyVersion,
            OperationKind = command.OperationKind,
            CredentialEffectLocator = credentialEffectLocator,
            ActivationDecision = activationDecision,
            MutationDigest = mutationDigest,
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await PersistTeamAutomationObservationAsync(
            TeamAutomationOperationObservationStages.Begin,
            ownsEffectAttempt: true,
            CancellationToken.None,
            observationRequestId: command.ObservationRequestId,
            newOperationCommitted: true);
    }

    [EventHandler]
    public Task HandleRecordTeamAutomationCredentialCandidateAsync(
        RecordTeamAutomationCredentialCandidateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteObservedTeamAutomationCommandAsync(
            ResolveScheduleId(),
            command.OperationId,
            command.IdempotencyKey,
            TeamAutomationOperationObservationStages.Candidate,
            command.ObservationRequestId,
            () => HandleRecordTeamAutomationCredentialCandidateCoreAsync(command));
    }

    private async Task HandleRecordTeamAutomationCredentialCandidateCoreAsync(
        RecordTeamAutomationCredentialCandidateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var owner = NormalizeTeamAutomationOwner(command.Owner);
        EnsureObservedTeamAutomationOwnerAccess(owner);
        EnsureCurrentTeamAutomationOperation(command.OperationId, command.IdempotencyKey);
        EnsureCurrentTeamAutomationEffectAttempt(command.EffectAttemptId);
        if (State.TeamAutomationLifecycleStatus is not (
                TeamAutomationLifecycleStatusState.ProvisioningPending or
                TeamAutomationLifecycleStatusState.ReplacementPending))
        {
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_operation_not_pending");
        }

        var credential = NormalizeTeamCredential(command.Credential);
        EnsureTeamCredentialMatchesEffectLocator(credential, State.TeamCredentialEffectLocator);
        var credentialOwner = NormalizeCredentialAuthorizationOwner(command.CredentialOwner);
        if (!CredentialAuthorizationOwnerEquals(
                State.TeamCredentialEffectLocator?.CredentialOwner,
                credentialOwner))
        {
            throw TeamAutomationCommandRejectedException.Unauthorized(
                "team_automation_candidate_credential_owner_mismatch");
        }
        if (CredentialEquals(State.CandidateTeamCredential, credential) &&
            CredentialAuthorizationOwnerEquals(State.CandidateTeamCredentialOwner, credentialOwner))
        {
            await PersistTeamAutomationObservationAsync(
                TeamAutomationOperationObservationStages.Candidate,
                ownsEffectAttempt: false,
                CancellationToken.None,
                observationRequestId: command.ObservationRequestId);
            return;
        }
        if (State.CandidateTeamCredential != null)
            throw TeamAutomationCommandRejectedException.Conflict(
                "team_automation_candidate_credential_conflict");

        await PersistDomainEventAsync(new TeamAutomationCredentialCandidateRecordedEvent
        {
            Owner = owner,
            OperationId = State.TeamAutomationOperationId,
            IdempotencyKey = State.TeamAutomationIdempotencyKey,
            EffectAttemptId = State.TeamAutomationEffectAttemptId,
            Credential = credential,
            CredentialOwner = credentialOwner,
            OccurredAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        });
        await PersistTeamAutomationObservationAsync(
            TeamAutomationOperationObservationStages.Candidate,
            ownsEffectAttempt: false,
            CancellationToken.None,
            observationRequestId: command.ObservationRequestId);
    }

    [EventHandler]
    public Task HandleCompleteTeamAutomationCredentialOperationAsync(
        CompleteTeamAutomationCredentialOperationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteObservedTeamAutomationCommandAsync(
            ResolveScheduleId(),
            command.OperationId,
            command.IdempotencyKey,
            TeamAutomationOperationObservationStages.Complete,
            command.ObservationRequestId,
            () => HandleCompleteTeamAutomationCredentialOperationCoreAsync(command));
    }

    private async Task HandleCompleteTeamAutomationCredentialOperationCoreAsync(
        CompleteTeamAutomationCredentialOperationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var owner = NormalizeTeamAutomationOwner(command.Owner);
        EnsureObservedTeamAutomationOwnerAccess(owner);
        EnsureCurrentTeamAutomationOperation(command.OperationId, command.IdempotencyKey);
        var credential = NormalizeTeamCredential(command.Credential);
        var configuration = NormalizeTeamAutomationActivationConfiguration(command.Configuration, owner, credential);
        var completedDecision = CreateTeamAutomationActivationDecision(configuration);
        if (State.TeamAutomationLifecycleStatus == TeamAutomationLifecycleStatusState.Active)
        {
            var installedDecision = CreateInstalledTeamAutomationActivationDecision();
            if (!CredentialEquals(State.ActiveTeamCredential, credential) ||
                !TeamAutomationActivationDecisionEquals(installedDecision, completedDecision))
            {
                throw TeamAutomationCommandRejectedException.Conflict(
                    "team_automation_activation_decision_mismatch");
            }
            await PersistTeamAutomationObservationAsync(
                TeamAutomationOperationObservationStages.Complete,
                ownsEffectAttempt: false,
                CancellationToken.None,
                observationRequestId: command.ObservationRequestId);
            return;
        }
        if (State.TeamAutomationLifecycleStatus is not (TeamAutomationLifecycleStatusState.ProvisioningPending or
            TeamAutomationLifecycleStatusState.ReplacementPending))
        {
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_operation_not_pending");
        }
        if (State.TeamAutomationActivationDecision == null)
        {
            throw TeamAutomationCommandRejectedException.Conflict(
                "team_automation_activation_decision_missing");
        }
        EnsureCurrentTeamAutomationEffectAttempt(command.EffectAttemptId);
        if (!CredentialEquals(State.CandidateTeamCredential, credential) ||
            State.CandidateTeamCredentialOwner == null)
        {
            throw TeamAutomationCommandRejectedException.Conflict(
                "team_automation_candidate_credential_not_committed");
        }
        if (!TeamAutomationActivationDecisionEquals(State.TeamAutomationActivationDecision, completedDecision))
        {
            throw TeamAutomationCommandRejectedException.Conflict(
                "team_automation_activation_decision_mismatch");
        }
        var configurationOwner = NormalizeCredentialAuthorizationOwner(
            configuration.Target?.ServiceInvocation?.AuthorizationFact?.Owner);
        if (!CredentialAuthorizationOwnerEquals(State.CandidateTeamCredentialOwner, configurationOwner))
            throw TeamAutomationCommandRejectedException.Unauthorized(
                "team_automation_candidate_credential_owner_mismatch");
        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        var previousCredentialExpiryLease =
            ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.TeamCredentialExpiryLease);
        await PersistDomainEventAsync(new TeamAutomationCredentialActivatedEvent
        {
            Owner = owner,
            OperationId = State.TeamAutomationOperationId,
            IdempotencyKey = State.TeamAutomationIdempotencyKey,
            Credential = credential,
            ReplacedCredential = State.ActiveTeamCredential?.Clone(),
            CredentialOwner = State.CandidateTeamCredentialOwner.Clone(),
            ReplacedCredentialOwner = State.ActiveTeamCredentialOwner?.Clone(),
            Generation = checked(State.TeamCredentialGeneration + 1),
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Configuration = configuration,
        });
        await PersistTeamAutomationObservationAsync(
            TeamAutomationOperationObservationStages.Complete,
            State.PendingRevocationTeamCredential != null,
            CancellationToken.None,
            observationRequestId: command.ObservationRequestId);
        await EnsureTeamCredentialExpiryScheduledAsync(
            previousCredentialExpiryLease,
            CancellationToken.None);
        if (CanScheduleAutomaticFire())
            await EnsureNextFireScheduledAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        else
            await CancelNextFireLeaseAsync(previousLease, CancellationToken.None);
    }

    [EventHandler]
    public Task HandleFailTeamAutomationCredentialOperationAsync(
        FailTeamAutomationCredentialOperationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteObservedTeamAutomationCommandAsync(
            ResolveScheduleId(),
            command.OperationId,
            command.IdempotencyKey,
            TeamAutomationOperationObservationStages.Fail,
            command.ObservationRequestId,
            () => HandleFailTeamAutomationCredentialOperationCoreAsync(command));
    }

    private async Task HandleFailTeamAutomationCredentialOperationCoreAsync(
        FailTeamAutomationCredentialOperationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var owner = NormalizeTeamAutomationOwner(command.Owner);
        EnsureObservedTeamAutomationOwnerAccess(owner);
        EnsureCurrentTeamAutomationOperation(command.OperationId, command.IdempotencyKey);
        var errorCode = NormalizeStableErrorCode(command.ErrorCode);
        if (State.TeamAutomationLifecycleStatus is
                TeamAutomationLifecycleStatusState.Failed or
                TeamAutomationLifecycleStatusState.Active or
                TeamAutomationLifecycleStatusState.RevocationPending &&
            string.Equals(State.LastAuthorizationErrorCode, errorCode, StringComparison.Ordinal))
        {
            await PersistTeamAutomationObservationAsync(
                TeamAutomationOperationObservationStages.Fail,
                ownsEffectAttempt: State.PendingRevocationTeamCredential != null &&
                                   CanClaimTeamAutomationEffectAttempt(_timeProvider.GetUtcNow()),
                CancellationToken.None,
                errorCode,
                observationRequestId: command.ObservationRequestId);
            return;
        }
        EnsureCurrentTeamAutomationEffectAttempt(command.EffectAttemptId);

        await PersistDomainEventAsync(new TeamAutomationCredentialOperationFailedEvent
        {
            Owner = owner,
            OperationId = State.TeamAutomationOperationId,
            IdempotencyKey = State.TeamAutomationIdempotencyKey,
            ErrorCode = errorCode,
            ActiveCredentialPreserved = State.ActiveTeamCredential != null,
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await PersistTeamAutomationObservationAsync(
            TeamAutomationOperationObservationStages.Fail,
            ownsEffectAttempt: State.PendingRevocationTeamCredential != null,
            CancellationToken.None,
            errorCode,
            observationRequestId: command.ObservationRequestId);
    }

    [EventHandler]
    public Task HandleCompleteTeamAutomationRevocationAsync(
        CompleteTeamAutomationRevocationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteObservedTeamAutomationCommandAsync(
            ResolveScheduleId(),
            command.OperationId,
            command.IdempotencyKey,
            TeamAutomationOperationObservationStages.Revocation,
            command.ObservationRequestId,
            () => HandleCompleteTeamAutomationRevocationCoreAsync(command));
    }

    private async Task HandleCompleteTeamAutomationRevocationCoreAsync(
        CompleteTeamAutomationRevocationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var owner = NormalizeTeamAutomationOwner(command.Owner);
        EnsureObservedTeamAutomationOwnerAccess(owner);
        EnsureCurrentTeamAutomationOperation(command.OperationId, command.IdempotencyKey);
        EnsureCurrentTeamAutomationEffectAttempt(command.EffectAttemptId);

        await PersistDomainEventAsync(new TeamAutomationRevocationCompletedEvent
        {
            Owner = owner,
            OperationId = State.TeamAutomationOperationId,
            NyxidRevoked = command.NyxidRevoked,
            VaultRevoked = command.VaultRevoked,
            ErrorCode = string.IsNullOrWhiteSpace(command.ErrorCode)
                ? string.Empty
                : NormalizeStableErrorCode(command.ErrorCode),
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await PersistTeamAutomationObservationAsync(
            TeamAutomationOperationObservationStages.Revocation,
            ownsEffectAttempt: false,
            CancellationToken.None,
            State.LastAuthorizationErrorCode,
            observationRequestId: command.ObservationRequestId);
    }

    [EventHandler]
    public Task HandleRetryTeamAutomationRevocationAsync(
        RetryTeamAutomationRevocationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteObservedTeamAutomationCommandAsync(
            ResolveScheduleId(),
            command.OperationId,
            command.IdempotencyKey,
            TeamAutomationOperationObservationStages.Delete,
            command.ObservationRequestId,
            () => HandleRetryTeamAutomationRevocationCoreAsync(command));
    }

    private async Task HandleRetryTeamAutomationRevocationCoreAsync(
        RetryTeamAutomationRevocationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var owner = NormalizeTeamAutomationOwner(command.Owner);
        EnsureObservedTeamAutomationOwnerAccess(owner);
        EnsureCurrentTeamAutomationOperation(command.OperationId, command.IdempotencyKey);
        EnsureObservedCredentialAuthorizationOwnerAccess(
            command.AuthenticatedCredentialOwner,
            State.PendingRevocationTeamCredentialOwner);
        if (State.PendingRevocationTeamCredential == null)
            throw TeamAutomationCommandRejectedException.Conflict(
                "team_automation_revocation_not_pending");
        await PersistTeamAutomationObservationAsync(
            TeamAutomationOperationObservationStages.Delete,
            ownsEffectAttempt: CanClaimTeamAutomationEffectAttempt(_timeProvider.GetUtcNow()),
            CancellationToken.None,
            observationRequestId: command.ObservationRequestId);
    }

    [EventHandler(AllowSelfHandling = true)]
    public Task HandleTeamAutomationCredentialExpiryAsync(
        TeamAutomationCredentialExpiryCommand command) =>
        HandleTeamAutomationCredentialExpiryAsync(
            command,
            ActiveInboundEnvelope,
            CancellationToken.None);

    internal async Task HandleTeamAutomationCredentialExpiryAsync(
        TeamAutomationCredentialExpiryCommand command,
        EventEnvelope? inboundEnvelope,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (State.Deleted ||
            State.TeamAutomationOwner == null ||
            State.ActiveTeamCredential == null ||
            State.TeamAutomationLifecycleStatus == TeamAutomationLifecycleStatusState.NeedsAuthorization)
        {
            return;
        }

        if (!string.Equals(command.ScheduleId, ResolveScheduleId(), StringComparison.Ordinal) ||
            command.CredentialGeneration != State.TeamCredentialGeneration ||
            command.ExpiresAt == null ||
            State.TeamCredentialExpiresAt == null ||
            command.ExpiresAt.ToDateTimeOffset() != State.TeamCredentialExpiresAt.ToDateTimeOffset() ||
            !MatchesTeamCredentialExpiryLease(inboundEnvelope))
        {
            Logger.LogInformation(
                "Scheduled dispatch {ActorId} ignored stale Team credential expiry callback scheduleId={ScheduleId} credentialGeneration={CredentialGeneration}.",
                Id,
                ResolveScheduleId(),
                command.CredentialGeneration);
            return;
        }

        var expiresAt = State.TeamCredentialExpiresAt.ToDateTimeOffset();
        var now = _timeProvider.GetUtcNow();
        if (now < expiresAt)
        {
            var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(
                State.TeamCredentialExpiryLease);
            await RecordTeamCredentialExpiryIntentAsync(
                State.TeamCredentialGeneration,
                expiresAt,
                ct);
            await ActivateTeamCredentialExpiryIntentAsync(
                State.TeamCredentialGeneration,
                expiresAt,
                previousLease,
                ct);
            return;
        }

        await TransitionTeamAutomationToCredentialExpiredAsync(now, ct);
    }

    [EventHandler(AllowSelfHandling = true)]
    public Task HandleFireAsync(ScheduledDispatchFireCommand command) =>
        HandleFireAsync(command, ActiveInboundEnvelope, CancellationToken.None);

    internal async Task HandleFireAsync(
        ScheduledDispatchFireCommand command,
        EventEnvelope? inboundEnvelope,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!command.Manual && State.Deleted)
        {
            Logger.LogInformation("Scheduled dispatch {ActorId} ignored fire because it is deleted.", Id);
            return;
        }

        if (!command.Manual && State.Completed)
        {
            Logger.LogInformation("Scheduled dispatch {ActorId} ignored fire because it is completed.", Id);
            return;
        }

        EnsureConfiguredForWrite(command.Manual ? "manual fire" : "fire");
        if (command.Manual)
            EnsureExpectedServiceTargetAccess(command.ExpectedServiceTarget);
        if (await RetireUnmarkedEnvelopeTargetAsync(ct, rejectManualFire: command.Manual))
            return;

        if (command.Manual)
        {
            EnsureTeamAutomationOwnerAccess(command.TeamAutomationOwner, "manual fire");
        }

        var scheduledFireAt = ResolveScheduledFireAt(command);
        var callbackFiredAt = command.Manual ? (DateTimeOffset?)null : ResolveCallbackFiredAt(inboundEnvelope);

        if (!command.Manual && !MatchesNextFireLease(inboundEnvelope))
        {
            Logger.LogInformation(
                "Scheduled dispatch {ActorId} ignored stale fire callback scheduleId={ScheduleId} scheduledFireAt={ScheduledFireAt} leaseGeneration={LeaseGeneration}.",
                Id,
                ResolveScheduleId(),
                scheduledFireAt,
                State.NextFireLease?.Generation);
            return;
        }

        if (!command.Manual && callbackFiredAt < scheduledFireAt)
        {
            Logger.LogInformation(
                "Scheduled dispatch {ActorId} re-armed early fire callback scheduleId={ScheduleId} scheduledFireAt={ScheduledFireAt} callbackFiredAt={CallbackFiredAt}.",
                Id,
                ResolveScheduleId(),
                scheduledFireAt,
                callbackFiredAt);
            var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
            await RecordNextFireIntentAsync(scheduledFireAt, ct);
            await ActivateNextFireIntentAsync(scheduledFireAt, previousLease, ct);
            return;
        }

        if (!command.Manual && !State.Enabled)
        {
            Logger.LogInformation("Scheduled dispatch {ActorId} ignored fire because it is disabled.", Id);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (HasTeamCredentialLifecycle() && !HasUsableActiveTeamCredential(now))
        {
            if (State.ActiveTeamCredential != null &&
                State.TeamCredentialExpiresAt?.ToDateTimeOffset() <= now &&
                State.TeamAutomationLifecycleStatus != TeamAutomationLifecycleStatusState.NeedsAuthorization)
            {
                await TransitionTeamAutomationToCredentialExpiredAsync(now, ct);
            }
            if (command.Manual)
                throw new InvalidOperationException("team_automation_credential_not_active");

            Logger.LogWarning(
                "Scheduled dispatch {ActorId} skipped automatic fire because Team credential status is {LifecycleStatus}.",
                Id,
                State.TeamAutomationLifecycleStatus);
            return;
        }

        var idempotencyKey = command.Manual
            ? NormalizeRequired(command.IdempotencyKey, nameof(command.IdempotencyKey))
            : ScheduledDispatchCalculator.BuildIdempotencyKey(ResolveScheduleId(), scheduledFireAt);
        if (HasTerminalFireRecord(idempotencyKey))
        {
            State.FireRecords.TryGetValue(idempotencyKey, out var priorRecord);
            // A suppressed fire was previously an Information no-op, so #2366-style silent skips
            // left no signal for ops post-mortems. Elevate to Warning with the full decision
            // context: the stale-lease guard above already absorbs superseded re-deliveries, so a
            // duplicate that reaches here is an unexpected same-occurrence collision worth seeing.
            Logger.LogWarning(
                "Scheduled dispatch {ActorId} suppressed duplicate fire scheduleId={ScheduleId} idempotencyKey={IdempotencyKey} scheduledFireAt={ScheduledFireAt} nextFireAt={NextFireAt} callbackFiredAt={CallbackFiredAt} leaseGeneration={LeaseGeneration} priorStatus={PriorStatus} manual={Manual}.",
                Id,
                ResolveScheduleId(),
                idempotencyKey,
                scheduledFireAt,
                State.NextFireAt,
                callbackFiredAt,
                State.NextFireLease?.Generation,
                priorRecord?.Status,
                command.Manual);
            if (!command.Manual && !IsOneShot())
                await EnsureNextFireScheduledAsync(scheduledFireAt, ct);
            return;
        }

        if (callbackFiredAt is { } firedAt && firedAt - scheduledFireAt > OverdueFireGracePeriod)
        {
            // The callback reached the handler well after its scheduled time (late delivery while
            // the grain stayed active, so OnActivate never re-ran). The fire still dispatches; the
            // Warning makes the lateness observable even though it is not counted as an overdue
            // detection.
            Logger.LogWarning(
                "Scheduled dispatch {ActorId} dispatching overdue fire scheduleId={ScheduleId} scheduledFireAt={ScheduledFireAt} callbackFiredAt={CallbackFiredAt} overdueSeconds={OverdueSeconds}.",
                Id,
                ResolveScheduleId(),
                scheduledFireAt,
                firedAt,
                (long)(firedAt - scheduledFireAt).TotalSeconds);
        }

        await PersistDomainEventAsync(new ScheduledDispatchFireStartedEvent
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            StartedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            IdempotencyKey = idempotencyKey,
            Manual = command.Manual,
        }, ct);

        try
        {
            var prepared = await BuildDispatchEnvelopeAsync(scheduledFireAt, idempotencyKey, ct);
            var receipt = await DispatchPreparedTargetAsync(prepared, scheduledFireAt, ct);
            if (!receipt.Accepted)
            {
                await PersistFireFailedAsync(
                    scheduledFireAt,
                    idempotencyKey,
                    "scheduled_dispatch_failed",
                    "Scheduled dispatch was not accepted.",
                    command.Manual,
                    ct);
            }
            else
            {
                await PersistDomainEventAsync(new ScheduledDispatchFireDispatchedEvent
                {
                    ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
                    DispatchedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    IdempotencyKey = idempotencyKey,
                    TargetActorId = receipt.TargetActorId,
                    CommandId = receipt.CommandId,
                    CorrelationId = receipt.CorrelationId,
                    Manual = command.Manual,
                }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation("Scheduled dispatch {ActorId} fire was canceled.", Id);
            throw;
        }
        catch (ScheduledServiceInvocationAuthorizationException ex) when (HasTeamCredentialLifecycle())
        {
            Logger.LogWarning(
                ex,
                "Scheduled dispatch {ActorId} requires Team automation reauthorization. errorCode={ErrorCode}",
                Id,
                ex.StableCode);
            var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
            var previousCredentialExpiryLease =
                ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.TeamCredentialExpiryLease);
            await PersistDomainEventAsync(new TeamAutomationAuthorizationRequiredEvent
            {
                Owner = State.TeamAutomationOwner.Clone(),
                ErrorCode = ex.StableCode,
                OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
                IdempotencyKey = idempotencyKey,
                Manual = command.Manual,
            }, CancellationToken.None);
            await CancelNextFireLeaseAsync(previousLease, CancellationToken.None);
            await CancelTeamCredentialExpiryLeaseAsync(
                previousCredentialExpiryLease,
                CancellationToken.None);
        }
        catch (ScheduledWorkflowAdmissionException ex)
        {
            Logger.LogWarning(
                "Scheduled dispatch {ActorId} workflow admission failed. errorCode={ErrorCode}",
                Id,
                ex.StableCode);
            await PersistFireFailedAsync(
                scheduledFireAt,
                idempotencyKey,
                ex.StableCode,
                ex.SafeMessage,
                command.Manual,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Scheduled dispatch {ActorId} dispatch failed.", Id);
            await PersistFireFailedAsync(
                scheduledFireAt,
                idempotencyKey,
                "scheduled_dispatch_failed",
                ex.Message,
                command.Manual,
                CancellationToken.None);
        }

        if (!command.Manual)
        {
            if (State.TeamAutomationLifecycleStatus == TeamAutomationLifecycleStatusState.NeedsAuthorization)
                return;
            if (IsOneShot())
                await CompleteOneShotAsync(CancellationToken.None);
            else
                await EnsureNextFireScheduledAsync(scheduledFireAt, CancellationToken.None);
        }
    }

    private async Task<ScheduledDispatchReceipt> DispatchPreparedTargetAsync(
        ScheduledDispatchEnvelope prepared,
        DateTimeOffset scheduledFireAt,
        CancellationToken ct)
    {
        if (prepared.TargetKind == ScheduledDispatchTargetKindState.ServiceInvocation)
        {
            if (prepared.Envelope.Payload?.TryUnpack<ServiceInvocationRequest>(out var request) != true)
                throw new InvalidOperationException("Scheduled service invocation payload is not configured.");

            var stateTarget = State.Target;
            Logger.LogInformation(
                "Scheduled service invocation fire prepared from actor state. scheduleId={ScheduleId} scheduleKind={ScheduleKind} hasServiceInvocationAuth={HasServiceInvocationAuth} hasScopeOwnerNyxId={HasScopeOwnerNyxId} hasSenderNyxId={HasSenderNyxId} hasDurableCredentialReference={HasDurableCredentialReference} hasScheduledInvocationAgentKey={HasScheduledInvocationAgentKey} hasLegacyDurableSenderBearerBlocked={HasLegacyDurableSenderBearerBlocked} projectWorkflowCallerCredential={ProjectWorkflowCallerCredential}",
                ResolveScheduleId(),
                State.ScheduleKind,
                HasServiceInvocationAuth(stateTarget),
                HasScopeOwnerNyxId(stateTarget),
                HasSenderNyxId(stateTarget),
                HasDurableCredentialReference(stateTarget),
                HasScheduledInvocationAgentKey(stateTarget),
                HasLegacyDurableSenderBearerBlocked(stateTarget),
                State.ScheduleKind == ScheduledDispatchScheduleKindState.Workflow);
            if (HasLegacyDurableSenderBearerBlocked(stateTarget))
            {
                // Ops-grade transition signal (#2586): a schedule provisioned before the durable-bearer
                // removal is permanently blocked until reconfigured — every fire lands here, so alert on
                // this message pattern instead of letting per-fire Warning + FailureCount accumulate as
                // the only trace of a schedule that "looks alive" but never dispatches.
                Logger.LogError(
                    "Scheduled dispatch {ActorId} is blocked by legacy durable bearer auth and will never fire until reconfigured. scheduleId={ScheduleId} remediation=recreate the schedule with senderNyxId or scopeOwnerNyxId",
                    Id,
                    ResolveScheduleId());
                throw new InvalidOperationException(LegacyDurableSenderBearerBlockedError);
            }

            EnsureCredentialRequirementAllowed(
                ScheduledDispatchCredentialRequirementOperation.Fire,
                ResolveScheduleId(),
                State.ScheduleKind,
                NormalizeTarget(stateTarget, State.ScheduleKind),
                prepared.Headers ?? EmptyHeaders);

            var replacementPending = State.TeamAutomationLifecycleStatus ==
                TeamAutomationLifecycleStatusState.ReplacementPending;
            var effectiveAuth = replacementPending && State.ActiveTeamCredential != null
                ? new ScheduledServiceInvocationAuthState
                {
                    ScheduledInvocationAgentKey = State.ActiveTeamCredential.Clone(),
                    CallerAuthority = State.Target?.ServiceInvocation?.Auth?.CallerAuthority?.Clone(),
                }
                : State.Target?.ServiceInvocation?.Auth;
            var effectiveAuthorizationFact = replacementPending
                ? State.ActiveTeamAuthorizationFact
                : State.Target?.ServiceInvocation?.AuthorizationFact;
            var receipt = await _serviceInvocationDispatchPort.DispatchAsync(
                new ScheduledServiceInvocationDispatchRequest(
                    request,
                    ToRuntimeAuth(effectiveAuth),
                    ReadOnlyCopy(prepared.Headers ?? EmptyHeaders),
                    ProjectNyxIdAccessTokenToWorkflowCallerCredential:
                        State.ScheduleKind == ScheduledDispatchScheduleKindState.Workflow,
                    ScheduleId: ResolveScheduleId(),
                    AuthorizationFact: ToRuntimeAuthorizationFact(effectiveAuthorizationFact),
                    FireContext: new ScheduledDispatchFireContext(
                        scheduledFireAt,
                        State.Timezone),
                    ScheduleOperationId: State.TeamAutomationOperationId),
                ct);
            return new ScheduledDispatchReceipt(
                receipt.Accepted,
                receipt.CommandId,
                receipt.TargetActorId,
                receipt.CorrelationId);
        }

        var admission = await _dispatchPort.DispatchAsync(prepared.TargetActorId, prepared.Envelope, ct);
        return new ScheduledDispatchReceipt(
            admission.Accepted,
            admission.CommandId,
            admission.ActorId,
            admission.CorrelationId);
    }

    private async Task PersistFireFailedAsync(
        DateTimeOffset scheduledFireAt,
        string idempotencyKey,
        string errorCode,
        string error,
        bool manual,
        CancellationToken ct)
    {
        await PersistDomainEventAsync(new ScheduledDispatchFireFailedEvent
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            FailedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            IdempotencyKey = idempotencyKey,
            Error = string.IsNullOrWhiteSpace(error) ? "Scheduled dispatch failed." : error.Trim(),
            ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "scheduled_dispatch_failed" : errorCode.Trim(),
            Manual = manual,
        }, ct);
    }

    private async Task<ScheduledDispatchEnvelope> BuildDispatchEnvelopeAsync(
        DateTimeOffset scheduledFireAtUtc,
        string idempotencyKey,
        CancellationToken ct)
    {
        var headers = BuildFireHeaders(scheduledFireAtUtc, idempotencyKey);
        if (ResolveTargetKind() == ScheduledDispatchTargetKindState.ServiceInvocation)
            return await BuildServiceInvocationDispatchEnvelopeAsync(headers, idempotencyKey, ct);

        var envelope = State.TriggerEnvelope?.Clone()
            ?? throw new InvalidOperationException("Scheduled dispatch trigger envelope is not configured.");
        if (envelope.Payload == null)
            throw new InvalidOperationException("Scheduled dispatch trigger envelope payload is not configured.");

        envelope.Payload = ScheduledServiceInvocationPayloadPolicy.StripScheduleOwnedCredentialFields(envelope.Payload);
        envelope.Id = idempotencyKey;
        envelope.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        envelope.Route = EnvelopeRouteSemantics.CreateDirect(ResolveScheduleId(), ResolveDispatchTargetActorId());
        envelope.Runtime = null;
        var propagation = envelope.EnsurePropagation();
        if (string.IsNullOrWhiteSpace(propagation.CorrelationId))
            propagation.CorrelationId = idempotencyKey;
        foreach (var (key, value) in headers)
            propagation.Baggage[key] = value;

        if (envelope.Payload.TryUnpack<ServiceInvocationRequest>(out var serviceInvocationRequest))
        {
            serviceInvocationRequest.CommandId = idempotencyKey;
            serviceInvocationRequest.CorrelationId = propagation.CorrelationId;
            envelope.Payload = Any.Pack(serviceInvocationRequest);
            return new ScheduledDispatchEnvelope(
                ResolveDispatchTargetActorId(),
                ResolveTargetKind(),
                envelope);
        }

        return new ScheduledDispatchEnvelope(
            ResolveDispatchTargetActorId(),
            ResolveTargetKind(),
            envelope);
    }

    private Task<ScheduledDispatchEnvelope> BuildServiceInvocationDispatchEnvelopeAsync(
        IReadOnlyDictionary<string, string> headers,
        string idempotencyKey,
        CancellationToken ct)
    {
        var target = State.Target?.ServiceInvocation
            ?? throw new InvalidOperationException("Scheduled service invocation target is not configured.");
        ct.ThrowIfCancellationRequested();
        var request = new ServiceInvocationRequest
        {
            Identity = target.Identity?.Clone(),
            EndpointId = target.EndpointId ?? string.Empty,
            Payload = target.Payload == null
                ? throw new InvalidOperationException("Scheduled service invocation payload is not configured.")
                : ScheduledServiceInvocationPayloadPolicy.StripScheduleOwnedCredentialFields(target.Payload),
            CommandId = idempotencyKey,
            CorrelationId = idempotencyKey,
            RevisionId = target.RevisionId ?? string.Empty,
            ScheduleId = ResolveScheduleId(),
        };
        if (target.Caller != null)
            request.Caller = target.Caller.Clone();

        var envelope = new EventEnvelope
        {
            Id = idempotencyKey,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(request),
            Route = EnvelopeRouteSemantics.CreateDirect(
                ResolveScheduleId(),
                ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = idempotencyKey,
            },
        };
        foreach (var (key, value) in headers)
            envelope.Propagation.Baggage[key] = value;

        return Task.FromResult(new ScheduledDispatchEnvelope(
            ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
            ScheduledDispatchTargetKindState.ServiceInvocation,
            envelope,
            ReadOnlyCopy(headers)));
    }

    private static ScheduledServiceInvocationAuth? ToRuntimeAuth(ScheduledServiceInvocationAuthState? auth)
    {
        if (auth == null)
            return null;

        if (auth.LegacyDurableSenderBearerBlocked ||
            !string.IsNullOrWhiteSpace(auth.DurableSenderBearerToken))
        {
            throw new InvalidOperationException(LegacyDurableSenderBearerBlockedError);
        }

        if (auth.SourceCase == ScheduledServiceInvocationAuthState.SourceOneofCase.Durable)
        {
            return new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationDurableCredentialReference(
                auth.Durable.CredentialId ?? string.Empty,
                auth.Durable.SecretReference?.Clone() ?? new SecretReference()))
            {
                CallerAuthority = auth.CallerAuthority?.Clone(),
            };
        }

        if (auth.SourceCase == ScheduledServiceInvocationAuthState.SourceOneofCase.ScheduledInvocationAgentKey)
        {
            return new ScheduledServiceInvocationAuth(new ScheduledInvocationAgentKeyCredentialReference(
                auth.ScheduledInvocationAgentKey.SecretReference?.Clone() ?? new SecretReference(),
                auth.ScheduledInvocationAgentKey.ApiKeyId ?? string.Empty,
                auth.ScheduledInvocationAgentKey.KeyExpiresAtUnixMs))
            {
                CallerAuthority = auth.CallerAuthority?.Clone(),
            };
        }

        var nyxId = ResolveNyxIdSource(auth);
        if (nyxId == null)
            return null;

        return new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            ToRuntimeSubject(nyxId.Subject) ?? new ScheduledServiceInvocationNyxIdSubjectRef(
                string.Empty,
                string.Empty,
                string.Empty),
            nyxId.Scope ?? string.Empty,
            ToRuntimeRole(nyxId.Role)))
        {
            CallerAuthority = auth.CallerAuthority?.Clone(),
        };
    }

    private static ScheduledServiceInvocationNyxIdCredentialSourceState? ResolveNyxIdSource(
        ScheduledServiceInvocationAuthState auth)
    {
        if (auth.SourceCase == ScheduledServiceInvocationAuthState.SourceOneofCase.NyxId)
            return auth.NyxId;

        if (auth.ScopeOwnerNyxId != null)
        {
            return new ScheduledServiceInvocationNyxIdCredentialSourceState
            {
                Subject = auth.ScopeOwnerNyxId.OwnerSubject?.Clone(),
                Scope = auth.ScopeOwnerNyxId.Scope ?? string.Empty,
                Role = ScheduledServiceInvocationNyxIdCredentialRoleState.ScopeOwner,
            };
        }

        if (auth.SenderNyxId != null)
        {
            var sender = auth.SenderNyxId.Clone();
            sender.Role = ScheduledServiceInvocationNyxIdCredentialRoleState.Sender;
            return sender;
        }

        return null;
    }

    private static ScheduledServiceInvocationNyxIdCredentialRole ToRuntimeRole(
        ScheduledServiceInvocationNyxIdCredentialRoleState role) =>
        role == ScheduledServiceInvocationNyxIdCredentialRoleState.ScopeOwner
            ? ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner
            : ScheduledServiceInvocationNyxIdCredentialRole.Sender;

    private static ScheduledServiceInvocationNyxIdSubjectRef? ToRuntimeSubject(
        ScheduledServiceInvocationNyxIdSubjectRefState? subject) =>
        subject == null
            ? null
            : new ScheduledServiceInvocationNyxIdSubjectRef(
                subject.Platform ?? string.Empty,
                subject.Tenant ?? string.Empty,
                subject.ExternalUserId ?? string.Empty);

    private void EnsureCredentialRequirementAllowed(
        ScheduledDispatchCredentialRequirementOperation operation,
        string scheduleId,
        ScheduledDispatchScheduleKindState scheduleKind,
        ScheduledDispatchTargetState target,
        IEnumerable<KeyValuePair<string, string>> headers)
    {
        var request = new ScheduledDispatchCredentialRequirementRequest(
            scheduleId,
            operation,
            ToRuntimeScheduleKind(scheduleKind),
            ToRuntimeCredentialRequirementTargetKind(target.CredentialRequirementTargetKind),
            SummarizeAuth(target.ServiceInvocation?.Auth),
            SummarizePayloadCredentialSignal(target, headers));
        var decision = _credentialRequirementPolicy.Evaluate(request);
        if (!decision.Allowed)
            throw new InvalidOperationException(decision.Message);
    }

    private static ScheduledDispatchCredentialRequirementOperation ResolveCredentialRequirementOperation(
        IMessage command) =>
        command switch
        {
            ScheduledDispatchCreateCommand => ScheduledDispatchCredentialRequirementOperation.Create,
            ScheduledDispatchEnsureCommand => ScheduledDispatchCredentialRequirementOperation.Ensure,
            ScheduledDispatchUpdateCommand => ScheduledDispatchCredentialRequirementOperation.Update,
            _ => ScheduledDispatchCredentialRequirementOperation.Fire,
        };

    private static ScheduledDispatchScheduleKind ToRuntimeScheduleKind(
        ScheduledDispatchScheduleKindState scheduleKind) =>
        scheduleKind switch
        {
            ScheduledDispatchScheduleKindState.Workflow => ScheduledDispatchScheduleKind.Workflow,
            _ => ScheduledDispatchScheduleKind.Generic,
        };

    private static ScheduledDispatchCredentialRequirementTargetKind ToRuntimeCredentialRequirementTargetKind(
        ScheduledDispatchCredentialRequirementTargetKindState targetKind) =>
        targetKind switch
        {
            ScheduledDispatchCredentialRequirementTargetKindState.Envelope =>
                ScheduledDispatchCredentialRequirementTargetKind.Envelope,
            ScheduledDispatchCredentialRequirementTargetKindState.StaticService =>
                ScheduledDispatchCredentialRequirementTargetKind.StaticService,
            ScheduledDispatchCredentialRequirementTargetKindState.ScriptingService =>
                ScheduledDispatchCredentialRequirementTargetKind.ScriptingService,
            ScheduledDispatchCredentialRequirementTargetKindState.WorkflowService =>
                ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
            ScheduledDispatchCredentialRequirementTargetKindState.Connector =>
                ScheduledDispatchCredentialRequirementTargetKind.Connector,
            _ => ScheduledDispatchCredentialRequirementTargetKind.Unspecified,
        };

    private static ScheduledDispatchCredentialSourceSummary SummarizeAuth(
        ScheduledServiceInvocationAuthState? auth)
    {
        if (auth == null)
            return new ScheduledDispatchCredentialSourceSummary(ScheduledDispatchCredentialSourceKind.None);
        if (auth.LegacyDurableSenderBearerBlocked ||
            !string.IsNullOrWhiteSpace(auth.DurableSenderBearerToken))
        {
            return new ScheduledDispatchCredentialSourceSummary(
                ScheduledDispatchCredentialSourceKind.LegacyDurableSenderBearer);
        }

        var sourceCount = 0;
        var kind = ScheduledDispatchCredentialSourceKind.None;
        AddCredentialSourceKind(ResolveOneofCredentialSourceKind(auth), ref sourceCount, ref kind);
        if (auth.SenderNyxId != null)
        {
            AddCredentialSourceKind(ScheduledDispatchCredentialSourceKind.SenderNyxId, ref sourceCount, ref kind);
        }

        if (auth.ScopeOwnerNyxId != null)
        {
            AddCredentialSourceKind(ScheduledDispatchCredentialSourceKind.ScopeOwnerNyxId, ref sourceCount, ref kind);
        }

        return sourceCount switch
        {
            0 => new ScheduledDispatchCredentialSourceSummary(ScheduledDispatchCredentialSourceKind.None),
            1 => new ScheduledDispatchCredentialSourceSummary(kind),
            _ => new ScheduledDispatchCredentialSourceSummary(ScheduledDispatchCredentialSourceKind.Multiple),
        };
    }

    private static ScheduledDispatchCredentialSourceKind ResolveOneofCredentialSourceKind(
        ScheduledServiceInvocationAuthState auth) =>
        auth.SourceCase switch
        {
            ScheduledServiceInvocationAuthState.SourceOneofCase.NyxId =>
                auth.NyxId?.Role == ScheduledServiceInvocationNyxIdCredentialRoleState.ScopeOwner
                    ? ScheduledDispatchCredentialSourceKind.ScopeOwnerNyxId
                    : ScheduledDispatchCredentialSourceKind.SenderNyxId,
            ScheduledServiceInvocationAuthState.SourceOneofCase.Durable =>
                ScheduledDispatchCredentialSourceKind.DurableCredentialReference,
            ScheduledServiceInvocationAuthState.SourceOneofCase.ScheduledInvocationAgentKey =>
                ScheduledDispatchCredentialSourceKind.ScheduledInvocationAgentKey,
            _ => ScheduledDispatchCredentialSourceKind.None,
        };

    private static void AddCredentialSourceKind(
        ScheduledDispatchCredentialSourceKind candidate,
        ref int sourceCount,
        ref ScheduledDispatchCredentialSourceKind kind)
    {
        if (candidate == ScheduledDispatchCredentialSourceKind.None)
            return;

        sourceCount++;
        kind = candidate;
    }

    private static ScheduledDispatchPayloadCredentialSignal SummarizePayloadCredentialSignal(
        ScheduledDispatchTargetState target,
        IEnumerable<KeyValuePair<string, string>> headers)
    {
        var normalizedHeaders = ShouldInspectRawCredentialSignalHeaders(target.CredentialRequirementTargetKind)
            ? NormalizeCredentialSignalHeaders(headers)
            : NormalizeHeaders(headers);
        var payload = target.Kind == ScheduledDispatchTargetKindState.ServiceInvocation
            ? target.ServiceInvocation?.Payload
            : target.Envelope?.Payload;
        return ScheduledDispatchCredentialRequirementRequests.SummarizePayloadCredentialSignal(
            payload,
            normalizedHeaders);
    }

    private static IReadOnlyDictionary<string, string> ReadOnlyCopy(IReadOnlyDictionary<string, string> headers) =>
        new Dictionary<string, string>(headers, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, string> BuildFireHeaders(
        DateTimeOffset scheduledFireAtUtc,
        string idempotencyKey) =>
        new Dictionary<string, string>(State.Headers, StringComparer.Ordinal)
        {
            [ScheduledDispatchMetadataKeys.ScheduleId] = ResolveScheduleId(),
            [ScheduledDispatchMetadataKeys.FireAtUtc] = scheduledFireAtUtc.ToUniversalTime().ToString("O"),
            [ScheduledDispatchMetadataKeys.IdempotencyKey] = idempotencyKey,
        };

    private sealed record ScheduledDispatchEnvelope(
        string TargetActorId,
        ScheduledDispatchTargetKindState TargetKind,
        EventEnvelope Envelope,
        IReadOnlyDictionary<string, string>? Headers = null);

    private sealed record ScheduledDispatchReceipt(
        bool Accepted,
        string CommandId,
        string TargetActorId,
        string CorrelationId);

    private async Task EnsureNextFireScheduledAsync(DateTimeOffset fromUtc, CancellationToken ct)
    {
        if (!CanScheduleAutomaticFire())
            return;

        if (!TryResolveNextFireAt(fromUtc, out var nextFireAtUtc, out var error))
        {
            Logger.LogWarning("Scheduled dispatch {ActorId} could not compute next fire: {Error}", Id, error);
            return;
        }

        ct.ThrowIfCancellationRequested();
        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        var pendingNextFireAt = State.PendingNextFireAt?.ToDateTimeOffset();
        if (pendingNextFireAt != nextFireAtUtc)
            await RecordNextFireIntentAsync(nextFireAtUtc, ct);

        await ActivateNextFireIntentAsync(nextFireAtUtc, previousLease, ct);
    }

    private bool CanScheduleAutomaticFire() =>
        State.Enabled &&
        !State.Completed &&
        !State.Deleted &&
        IsConfigured() &&
        !HasEnvelopeTargetWithoutTrustedInternalAuthority() &&
        (!HasTeamCredentialLifecycle() ||
         HasUsableActiveTeamCredential(_timeProvider.GetUtcNow()));

    private async Task<bool> RetireUnmarkedEnvelopeTargetAsync(
        CancellationToken ct,
        bool rejectManualFire = false)
    {
        if (!HasEnvelopeTargetWithoutTrustedInternalAuthority())
            return false;
        if (rejectManualFire)
            throw new InvalidOperationException(LegacyUnmarkedEnvelopeRetiredError);

        if (State.Enabled)
        {
            await PersistDomainEventAsync(new ScheduledDispatchDisabledEvent
            {
                Reason = LegacyUnmarkedEnvelopeRetiredReason,
                DisabledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                ScheduleId = ResolveScheduleId(),
                ScopeId = ResolveScheduleScopeId(),
            }, ct);
        }

        await PurgeDurableCallbacksAsync(ct);
        Logger.LogWarning(
            "Scheduled dispatch {ActorId} retired an envelope target without trusted internal authority. scheduleId={ScheduleId}",
            Id,
            ResolveScheduleId());
        return true;
    }

    private bool HasEnvelopeTargetWithoutTrustedInternalAuthority() =>
        IsConfigured() &&
        ResolveTargetKind() == ScheduledDispatchTargetKindState.Envelope &&
        !HasTrustedInternalEnvelopeAuthority(State.Target);

    private static bool HasTrustedInternalEnvelopeAuthority(ScheduledDispatchTargetState? target) =>
        target?.Kind == ScheduledDispatchTargetKindState.Envelope &&
        target.EnvelopeAuthority == ScheduledDispatchEnvelopeAuthorityState.TrustedInternal;

    private async Task RecoverTeamCredentialExpiryAsync(CancellationToken ct)
    {
        if (State.Deleted ||
            State.TeamAutomationOwner == null ||
            State.ActiveTeamCredential == null ||
            State.TeamCredentialExpiresAt == null ||
            State.TeamAutomationLifecycleStatus == TeamAutomationLifecycleStatusState.NeedsAuthorization)
        {
            return;
        }

        var expiresAt = State.TeamCredentialExpiresAt.ToDateTimeOffset();
        var now = _timeProvider.GetUtcNow();
        if (expiresAt <= now)
        {
            await TransitionTeamAutomationToCredentialExpiredAsync(now, ct);
            return;
        }

        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(
            State.TeamCredentialExpiryLease);
        if (State.PendingTeamCredentialExpiryAt == null ||
            State.PendingTeamCredentialExpiryAt.ToDateTimeOffset() != expiresAt ||
            State.PendingTeamCredentialExpiryGeneration != State.TeamCredentialGeneration)
        {
            await RecordTeamCredentialExpiryIntentAsync(
                State.TeamCredentialGeneration,
                expiresAt,
                ct);
        }

        await ActivateTeamCredentialExpiryIntentAsync(
            State.TeamCredentialGeneration,
            expiresAt,
            previousLease,
            ct);
    }

    private async Task EnsureTeamCredentialExpiryScheduledAsync(
        RuntimeCallbackLease? previousLease,
        CancellationToken ct)
    {
        if (State.TeamAutomationOwner == null ||
            State.ActiveTeamCredential == null ||
            State.TeamCredentialExpiresAt == null ||
            State.Deleted)
        {
            await CancelTeamCredentialExpiryLeaseAsync(previousLease, CancellationToken.None);
            return;
        }

        var expiresAt = State.TeamCredentialExpiresAt.ToDateTimeOffset();
        var now = _timeProvider.GetUtcNow();
        if (expiresAt <= now)
        {
            await TransitionTeamAutomationToCredentialExpiredAsync(now, ct);
            return;
        }

        if (State.PendingTeamCredentialExpiryAt == null ||
            State.PendingTeamCredentialExpiryAt.ToDateTimeOffset() != expiresAt ||
            State.PendingTeamCredentialExpiryGeneration != State.TeamCredentialGeneration)
        {
            await RecordTeamCredentialExpiryIntentAsync(
                State.TeamCredentialGeneration,
                expiresAt,
                ct);
        }

        await ActivateTeamCredentialExpiryIntentAsync(
            State.TeamCredentialGeneration,
            expiresAt,
            previousLease,
            ct);
    }

    private Task RecordTeamCredentialExpiryIntentAsync(
        long credentialGeneration,
        DateTimeOffset expiresAt,
        CancellationToken ct) =>
        PersistDomainEventAsync(new TeamAutomationCredentialExpiryIntentRecordedEvent
        {
            CredentialGeneration = credentialGeneration,
            ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt.ToUniversalTime()),
            RequestedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, ct);

    private async Task ActivateTeamCredentialExpiryIntentAsync(
        long credentialGeneration,
        DateTimeOffset expiresAt,
        RuntimeCallbackLease? previousLease,
        CancellationToken ct)
    {
        var dueTime = ComputeNextFireCallbackDueTime(expiresAt, _timeProvider.GetUtcNow());
        var lease = await ScheduleSelfDurableTimeoutAsync(
            TeamCredentialExpiryCallbackId,
            dueTime,
            new TeamAutomationCredentialExpiryCommand
            {
                ScheduleId = ResolveScheduleId(),
                CredentialGeneration = credentialGeneration,
                ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt.ToUniversalTime()),
            },
            ct: ct);

        try
        {
            await PersistDomainEventAsync(new TeamAutomationCredentialExpiryScheduledEvent
            {
                CredentialGeneration = credentialGeneration,
                ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt.ToUniversalTime()),
                Lease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToState(lease),
                ScheduledAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            }, ct);
        }
        catch
        {
            await CancelTeamCredentialExpiryLeaseAsync(lease, CancellationToken.None);
            throw;
        }

        await CancelTeamCredentialExpiryLeaseAsync(previousLease, CancellationToken.None);
    }

    private async Task TransitionTeamAutomationToCredentialExpiredAsync(
        DateTimeOffset occurredAt,
        CancellationToken ct)
    {
        if (State.TeamAutomationOwner == null ||
            State.TeamAutomationLifecycleStatus == TeamAutomationLifecycleStatusState.NeedsAuthorization)
        {
            return;
        }

        var previousNextFireLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(
            State.NextFireLease);
        var previousExpiryLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(
            State.TeamCredentialExpiryLease);
        await PersistDomainEventAsync(new TeamAutomationAuthorizationRequiredEvent
        {
            Owner = State.TeamAutomationOwner.Clone(),
            ErrorCode = "credential_expired",
            OccurredAt = Timestamp.FromDateTimeOffset(occurredAt.ToUniversalTime()),
        }, ct);
        await CancelNextFireLeaseAsync(previousNextFireLease, CancellationToken.None);
        await CancelTeamCredentialExpiryLeaseAsync(previousExpiryLease, CancellationToken.None);
    }

    private async Task RecordNextFireIntentAsync(DateTimeOffset nextFireAtUtc, CancellationToken ct)
    {
        await PersistDomainEventAsync(new ScheduledDispatchNextFireIntentRecordedEvent
        {
            NextFireAt = Timestamp.FromDateTimeOffset(nextFireAtUtc),
            RequestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        }, ct);
    }

    private bool TryResolveNextFireAt(
        DateTimeOffset fromUtc,
        out DateTimeOffset nextFireAtUtc,
        out string? error)
    {
        if (IsOneShot())
        {
            if (!State.OneShotFireAt.HasValue)
            {
                nextFireAtUtc = default;
                error = "One-shot fire time is not configured.";
                return false;
            }

            nextFireAtUtc = State.OneShotFireAt.Value.ToUniversalTime();
            error = null;
            return true;
        }

        return ScheduledDispatchCalculator.TryGetNextOccurrence(
            State.CronExpression,
            State.Timezone,
            fromUtc,
            out nextFireAtUtc,
            out error);
    }

    private async Task CompleteOneShotAsync(CancellationToken ct)
    {
        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        await PersistDomainEventAsync(new ScheduledDispatchCompletedEvent
        {
            Reason = "one_shot_fired",
            CompletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        }, ct);
        await CancelNextFireLeaseAsync(previousLease, CancellationToken.None);
    }

    private async Task ActivateNextFireIntentAsync(
        DateTimeOffset nextFireAtUtc,
        RuntimeCallbackLease? previousLease,
        CancellationToken ct)
    {
        var dueTime = ComputeNextFireCallbackDueTime(nextFireAtUtc, DateTimeOffset.UtcNow);
        var lease = await ScheduleSelfDurableTimeoutAsync(
            NextFireCallbackId,
            dueTime,
            new ScheduledDispatchFireCommand
            {
                ScheduledFireAt = Timestamp.FromDateTimeOffset(nextFireAtUtc),
                Manual = false,
            },
            ct: ct);

        try
        {
            await PersistDomainEventAsync(new ScheduledDispatchNextFireScheduledEvent
            {
                NextFireAt = Timestamp.FromDateTimeOffset(nextFireAtUtc),
                Lease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToState(lease),
                ScheduledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }, ct);
        }
        catch
        {
            await CancelNextFireLeaseAsync(lease, CancellationToken.None);
            throw;
        }

        await CancelNextFireLeaseAsync(previousLease, CancellationToken.None);
    }

    private static TimeSpan ComputeNextFireCallbackDueTime(
        DateTimeOffset nextFireAtUtc,
        DateTimeOffset nowUtc)
    {
        var dueTime = ScheduledDispatchCalculator.ComputeDueTime(nextFireAtUtc, nowUtc);
        return dueTime <= MaxNextFireCallbackHop ? dueTime : MaxNextFireCallbackHop;
    }

    private async Task CancelNextFireLeaseAsync(CancellationToken ct)
    {
        var lease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        await CancelNextFireLeaseAsync(lease, ct);
    }

    private async Task CancelNextFireLeaseAsync(RuntimeCallbackLease? lease, CancellationToken ct)
    {
        if (lease == null)
            return;

        await CancelDurableCallbackAsync(lease, ct);
    }

    private async Task CancelTeamCredentialExpiryLeaseAsync(
        RuntimeCallbackLease? lease,
        CancellationToken ct)
    {
        if (lease == null)
            return;

        await CancelDurableCallbackAsync(lease, ct);
    }

    private bool MatchesNextFireLease(EventEnvelope? envelope)
    {
        if (envelope == null)
            return false;

        var lease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        return lease != null && RuntimeCallbackEnvelopeStateReader.MatchesLease(envelope, lease);
    }

    private bool MatchesTeamCredentialExpiryLease(EventEnvelope? envelope)
    {
        if (envelope == null)
            return false;

        var lease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(
            State.TeamCredentialExpiryLease);
        return lease != null && RuntimeCallbackEnvelopeStateReader.MatchesLease(envelope, lease);
    }

    private async Task DetectOverdueArmedFireAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        // The occurrence the actor is about to (re-)arm: a pending intent that never armed, or the
        // steady-state armed NextFireAt. When it is overdue past the grace window with no terminal
        // record, the tick that should have fired was silently dropped (a dead reminder or a
        // callback that never reached this handler) and we only notice now, on reactivation.
        var candidate = State.PendingNextFireAt?.ToDateTimeOffset() ?? State.NextFireAt;
        if (candidate == null)
            return;

        var overdue = nowUtc - candidate.Value;
        if (overdue <= OverdueFireGracePeriod)
            return;

        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey(ResolveScheduleId(), candidate.Value);
        if (HasTerminalFireRecord(idempotencyKey))
            return;

        // Once-per-occurrence: LastOverdueFireAt is persisted state, so repeated reactivations
        // against the same still-overdue armed occurrence do not inflate the counter.
        if (State.LastOverdueFireAt == candidate.Value)
            return;

        Logger.LogWarning(
            "Scheduled dispatch {ActorId} detected overdue armed fire scheduleId={ScheduleId} scheduledFireAt={ScheduledFireAt} overdueSeconds={OverdueSeconds} with no terminal record; re-arming as catch-up.",
            Id,
            ResolveScheduleId(),
            candidate.Value,
            (long)overdue.TotalSeconds);

        await PersistDomainEventAsync(new ScheduledDispatchFireOverdueDetectedEvent
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(candidate.Value),
            DetectedAt = Timestamp.FromDateTimeOffset(nowUtc),
            OverdueSeconds = (long)overdue.TotalSeconds,
        }, ct);
    }

    private bool HasTerminalFireRecord(string idempotencyKey)
    {
        if (!State.FireRecords.TryGetValue(idempotencyKey, out var record))
            return false;

        return record.Status is ScheduledDispatchFireStatusState.Dispatched or ScheduledDispatchFireStatusState.Failed;
    }

    private DateTimeOffset ResolveScheduledFireAt(ScheduledDispatchFireCommand command)
    {
        if (command.ScheduledFireAt != null)
            return command.ScheduledFireAt.ToDateTimeOffset().ToUniversalTime();

        return DateTimeOffset.UtcNow;
    }

    private static DateTimeOffset ResolveCallbackFiredAt(EventEnvelope? envelope)
    {
        if (envelope != null &&
            RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var state) &&
            state.FiredAtUnixTimeMs > 0)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(state.FiredAtUnixTimeMs);
        }

        return DateTimeOffset.UtcNow;
    }

    private string ResolveScheduleId() =>
        string.IsNullOrWhiteSpace(State.ScheduleId) ? Id : State.ScheduleId;

    private string ResolveDispatchTargetActorId() =>
        string.IsNullOrWhiteSpace(State.TargetActorId)
            ? ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId
            : State.TargetActorId.Trim();

    private ScheduledDispatchTargetKindState ResolveTargetKind() =>
        State.Target?.Kind == ScheduledDispatchTargetKindState.ServiceInvocation
            ? ScheduledDispatchTargetKindState.ServiceInvocation
            : ScheduledDispatchTargetKindState.Envelope;

    private bool IsOneShot() =>
        State.ScheduleMode == ScheduledDispatchScheduleModeState.OneShotAtUtc;

    private bool IsConfigured() =>
        !State.Deleted &&
        !string.IsNullOrWhiteSpace(State.ScheduleId) &&
        HasConfiguredSchedule() &&
        State.TriggerEnvelope?.Payload != null;

    private bool HasConfiguredSchedule() =>
        IsOneShot()
            ? State.OneShotFireAt.HasValue
            : !string.IsNullOrWhiteSpace(State.CronExpression);

    private bool MatchesConfiguredDefinition(ScheduledDispatchEnsureCommand command)
    {
        var normalizedTarget = PreserveExistingServiceInvocationAuth(
            NormalizeTarget(command.Target, command.ScheduleKind),
            isCreate: false);
        var normalizedTriggerEnvelope = NormalizeTriggerEnvelope(command.TriggerEnvelope);
        var normalizedHeaders = NormalizeHeaders(command.Headers);
        var normalizedScheduleId = NormalizeRequired(command.ScheduleId, nameof(command.ScheduleId));
        var normalizedDisplayName = NormalizeOptional(command.DisplayName);
        var normalizedTargetActorId = NormalizeOptional(command.TargetActorId);
        var normalizedMode = NormalizeScheduleMode(command.ScheduleMode);
        var normalizedCronExpression = normalizedMode == ScheduledDispatchScheduleModeState.OneShotAtUtc
            ? string.Empty
            : NormalizeRequired(command.CronExpression, nameof(command.CronExpression));
        var normalizedTimezone = ScheduledDispatchCalculator.NormalizeTimezone(command.Timezone);
        var normalizedOneShotFireAt = NormalizeOneShotFireAt(normalizedMode, command.OneShotFireAt);

        return string.Equals(State.ScheduleId, normalizedScheduleId, StringComparison.Ordinal) &&
               string.Equals(State.DisplayName, normalizedDisplayName, StringComparison.Ordinal) &&
               string.Equals(State.TargetActorId, normalizedTargetActorId, StringComparison.Ordinal) &&
               string.Equals(State.CronExpression, normalizedCronExpression, StringComparison.Ordinal) &&
               string.Equals(State.Timezone, normalizedTimezone, StringComparison.Ordinal) &&
               string.Equals(State.PayloadTypeUrl, ResolvePayloadTypeUrl(normalizedTriggerEnvelope), StringComparison.Ordinal) &&
               State.Enabled == command.Enabled &&
               State.ScheduleKind == command.ScheduleKind &&
               State.ScheduleMode == normalizedMode &&
               State.OneShotFireAt == normalizedOneShotFireAt &&
               TeamAutomationOwnerEquals(State.TeamAutomationOwner, command.TeamAutomationOwner) &&
               DictionaryEquals(State.Headers, normalizedHeaders) &&
               EnvelopePayloadEquals(State.TriggerEnvelope, normalizedTriggerEnvelope) &&
               TargetEquals(NormalizeTarget(State.Target, State.ScheduleKind), normalizedTarget);
    }

    private void EnsureConfiguredForWrite(string operation)
    {
        if (!IsConfigured())
            throw new InvalidOperationException(
                $"Scheduled dispatch '{ResolveScheduleId()}' cannot {operation} because it is not configured.");
    }

    private void EnsureExpectedServiceTargetAccess(
        ScheduledDispatchExpectedServiceTargetState? expected)
    {
        if (expected == null)
            return;

        var currentInvocation = State.Target?.ServiceInvocation;
        var expectedIdentity = expected.ServiceIdentity;
        var currentIdentity = currentInvocation?.Identity;
        if (State.ScheduleKind != expected.ScheduleKind ||
            State.Target?.Kind != expected.TargetKind ||
            currentIdentity == null ||
            expectedIdentity == null ||
            !string.Equals(currentInvocation?.EndpointId, expected.ServiceEndpointId, StringComparison.Ordinal) ||
            !string.Equals(currentIdentity.TenantId, expectedIdentity.TenantId, StringComparison.Ordinal) ||
            !string.Equals(currentIdentity.AppId, expectedIdentity.AppId, StringComparison.Ordinal) ||
            !string.Equals(currentIdentity.Namespace, expectedIdentity.Namespace, StringComparison.Ordinal) ||
            !string.Equals(currentIdentity.ServiceId, expectedIdentity.ServiceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("scheduled_dispatch_expected_service_target_mismatch");
        }
    }

    private void EnsureTeamAutomationOwnerAccess(
        TeamMemberAutomationOwnerState? supplied,
        string operation,
        bool allowUnconfiguredOwner = false)
    {
        if (State.TeamAutomationOwner == null)
        {
            if (supplied != null && !allowUnconfiguredOwner)
                throw new InvalidOperationException("team_automation_begin_required");
            return;
        }

        var normalized = NormalizeTeamAutomationOwner(supplied);
        if (!TeamAutomationOwnerEquals(State.TeamAutomationOwner, normalized))
            throw new InvalidOperationException($"Team automation owner cannot {operation} this schedule.");
    }

    private void EnsureStableTeamAutomationOwner(TeamMemberAutomationOwnerState owner)
    {
        if (State.TeamAutomationOwner != null &&
            !TeamAutomationOwnerEquals(State.TeamAutomationOwner, owner))
        {
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_owner_conflict");
        }
    }

    private bool IsExactTeamAutomationOperation(
        TeamMemberAutomationOwnerState owner,
        string operationId,
        string idempotencyKey,
        string permissionDigest,
        string policyVersion,
        TeamAutomationOperationKindState operationKind,
        ScheduledCredentialEffectLocatorState credentialEffectLocator,
        TeamAutomationActivationDecisionState activationDecision,
        string mutationDigest) =>
        TeamAutomationOwnerEquals(State.TeamAutomationOwner, owner) &&
        string.Equals(State.TeamAutomationOperationId, operationId, StringComparison.Ordinal) &&
        string.Equals(State.TeamAutomationIdempotencyKey, idempotencyKey, StringComparison.Ordinal) &&
        string.Equals(State.TeamAutomationPermissionDigest, permissionDigest, StringComparison.Ordinal) &&
        string.Equals(State.TeamAutomationPolicyVersion, policyVersion, StringComparison.Ordinal) &&
        State.TeamAutomationOperationKind == operationKind &&
        CredentialEffectLocatorEquals(State.TeamCredentialEffectLocator, credentialEffectLocator) &&
        TeamAutomationActivationDecisionEquals(State.TeamAutomationActivationDecision, activationDecision) &&
        string.Equals(State.TeamAutomationMutationDigest, mutationDigest, StringComparison.Ordinal);

    private static ScheduledCredentialEffectLocatorState NormalizeCredentialEffectLocator(
        ScheduledCredentialEffectLocatorState? locator)
    {
        if (locator == null)
            throw new InvalidOperationException("team_automation_credential_effect_locator_required");

        var secretPurpose = NormalizeRequired(locator.SecretPurpose, nameof(locator.SecretPurpose));
        if (!string.Equals(
                secretPurpose,
                CredentialSecretPurposes.ScheduledInvocationAgentKey,
                StringComparison.Ordinal))
        {
            throw TeamAutomationCommandRejectedException.InvalidRequest(
                "team_automation_credential_effect_locator_purpose_invalid");
        }

        return new ScheduledCredentialEffectLocatorState
        {
            CredentialName = NormalizeRequired(locator.CredentialName, nameof(locator.CredentialName)),
            RequestedSecretReference = NormalizeRequired(
                locator.RequestedSecretReference,
                nameof(locator.RequestedSecretReference)),
            SecretPurpose = secretPurpose,
            SecretOwnerScopeKey = NormalizeRequired(locator.SecretOwnerScopeKey, nameof(locator.SecretOwnerScopeKey)),
            CredentialOwner = NormalizeCredentialAuthorizationOwner(locator.CredentialOwner),
        };
    }

    private static bool CredentialEffectLocatorEquals(
        ScheduledCredentialEffectLocatorState? left,
        ScheduledCredentialEffectLocatorState? right) =>
        left != null && right != null &&
        string.Equals(left.CredentialName, right.CredentialName, StringComparison.Ordinal) &&
        string.Equals(left.RequestedSecretReference, right.RequestedSecretReference, StringComparison.Ordinal) &&
        string.Equals(left.SecretPurpose, right.SecretPurpose, StringComparison.Ordinal) &&
        string.Equals(left.SecretOwnerScopeKey, right.SecretOwnerScopeKey, StringComparison.Ordinal) &&
        CredentialAuthorizationOwnerEquals(left.CredentialOwner, right.CredentialOwner);

    private void EnsureCurrentTeamAutomationOperation(string? operationId, string? idempotencyKey)
    {
        if (!string.Equals(State.TeamAutomationOperationId,
                NormalizeRequired(operationId, nameof(operationId)), StringComparison.Ordinal) ||
            !string.Equals(State.TeamAutomationIdempotencyKey,
                NormalizeRequired(idempotencyKey, nameof(idempotencyKey)), StringComparison.Ordinal))
        {
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_operation_conflict");
        }
    }

    private bool CanClaimTeamAutomationEffectAttempt(DateTimeOffset now) =>
        !State.TeamAutomationEffectAttemptClaimed ||
        string.IsNullOrWhiteSpace(State.TeamAutomationEffectAttemptId) ||
        State.TeamAutomationEffectAttemptExpiresAt == null ||
        State.TeamAutomationEffectAttemptExpiresAt.ToDateTimeOffset() <= now;

    private void EnsureCurrentTeamAutomationEffectAttempt(string? effectAttemptId)
    {
        var normalized = NormalizeRequired(effectAttemptId, nameof(effectAttemptId));
        if (!State.TeamAutomationEffectAttemptClaimed ||
            State.TeamAutomationEffectAttemptExpiresAt == null ||
            State.TeamAutomationEffectAttemptExpiresAt.ToDateTimeOffset() <= _timeProvider.GetUtcNow() ||
            !string.Equals(State.TeamAutomationEffectAttemptId, normalized, StringComparison.Ordinal))
        {
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_effect_attempt_stale");
        }
    }

    private void EnsureObservedTeamAutomationOwnerAccess(TeamMemberAutomationOwnerState? supplied)
    {
        if (State.TeamAutomationOwner == null)
        {
            if (supplied != null)
                throw TeamAutomationCommandRejectedException.Conflict("team_automation_begin_required");
            return;
        }

        TeamMemberAutomationOwnerState normalized;
        try
        {
            normalized = NormalizeTeamAutomationOwner(supplied);
        }
        catch (InvalidOperationException)
        {
            throw TeamAutomationCommandRejectedException.InvalidRequest("team_automation_owner_required");
        }

        if (!TeamAutomationOwnerEquals(State.TeamAutomationOwner, normalized))
            throw TeamAutomationCommandRejectedException.Unauthorized("team_automation_owner_mismatch");
    }

    private static void EnsureObservedCredentialAuthorizationOwnerAccess(
        ScheduledInvocationAuthorizationOwnerState? supplied,
        ScheduledInvocationAuthorizationOwnerState? expected)
    {
        if (expected == null)
            throw TeamAutomationCommandRejectedException.Conflict("team_automation_credential_owner_missing");
        if (supplied == null ||
            !string.Equals(supplied.Authority?.Trim(), expected.Authority, StringComparison.Ordinal) ||
            !string.Equals(supplied.OwnerKind?.Trim(), expected.OwnerKind, StringComparison.Ordinal) ||
            !string.Equals(supplied.OwnerSubject?.Trim(), expected.OwnerSubject, StringComparison.Ordinal))
        {
            throw TeamAutomationCommandRejectedException.Unauthorized(
                "team_automation_credential_owner_mismatch");
        }
    }

    private bool HasTeamCredentialLifecycle() =>
        State.ActiveTeamCredential != null ||
        State.CandidateTeamCredential != null ||
        State.PendingRevocationTeamCredential != null ||
        State.ActiveTeamCredentialOwner != null ||
        State.CandidateTeamCredentialOwner != null ||
        State.TeamCredentialEffectLocator != null ||
        State.TeamAutomationLifecycleStatus is not TeamAutomationLifecycleStatusState.Unspecified;

    private bool IsSameDeleteOperation(
        ScheduledDispatchDeleteCommand command,
        TeamMemberAutomationOwnerState normalizedTeamAutomationOwner,
        string normalizedReason) =>
        State.TeamAutomationOwner != null &&
        TeamAutomationOwnerEquals(
            State.TeamAutomationOwner,
            normalizedTeamAutomationOwner) &&
        string.Equals(State.TeamAutomationOperationId, command.OperationId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(State.TeamAutomationIdempotencyKey, command.IdempotencyKey?.Trim(), StringComparison.Ordinal) &&
        State.HasTeamAutomationDeleteReason &&
        string.Equals(
            State.TeamAutomationDeleteReason,
            normalizedReason,
            StringComparison.Ordinal);

    private static TeamMemberAutomationOwnerState NormalizeTeamAutomationOwner(TeamMemberAutomationOwnerState? owner)
    {
        if (owner == null)
            throw new InvalidOperationException("team_automation_owner_required");

        return new TeamMemberAutomationOwnerState
        {
            ScopeId = NormalizeRequired(owner.ScopeId, nameof(owner.ScopeId)),
            MemberId = NormalizeRequired(owner.MemberId, nameof(owner.MemberId)),
            TeamId = NormalizeOptional(owner.TeamId),
        };
    }

    private static bool TeamAutomationOwnerEquals(
        TeamMemberAutomationOwnerState? left,
        TeamMemberAutomationOwnerState? right)
    {
        if (left == null || right == null)
            return left == null && right == null;

        return string.Equals(left.ScopeId, right.ScopeId, StringComparison.Ordinal) &&
               string.Equals(left.TeamId, right.TeamId, StringComparison.Ordinal) &&
               string.Equals(left.MemberId, right.MemberId, StringComparison.Ordinal);
    }

    private static bool TeamAutomationOwnerAssignmentEquals(
        TeamMemberAutomationOwnerState? left,
        TeamMemberAutomationOwnerState? right) =>
        TeamAutomationOwnerEquals(left, right) &&
        string.Equals(left?.TeamId, right?.TeamId, StringComparison.Ordinal);

    private static void EnsureCredentialAuthorizationOwnerAccess(
        ScheduledInvocationAuthorizationOwnerState? supplied,
        ScheduledInvocationAuthorizationOwnerState? expected)
    {
        if (expected == null)
            throw new InvalidOperationException("team_automation_credential_owner_missing");
        if (supplied == null ||
            !string.Equals(supplied.Authority?.Trim(), expected.Authority, StringComparison.Ordinal) ||
            !string.Equals(supplied.OwnerKind?.Trim(), expected.OwnerKind, StringComparison.Ordinal) ||
            !string.Equals(supplied.OwnerSubject?.Trim(), expected.OwnerSubject, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("team_automation_credential_owner_mismatch");
        }
    }

    private static ScheduledInvocationAuthorizationOwnerState NormalizeCredentialAuthorizationOwner(
        ScheduledInvocationAuthorizationOwnerState? owner)
    {
        if (owner == null ||
            string.IsNullOrWhiteSpace(owner.Authority) ||
            string.IsNullOrWhiteSpace(owner.OwnerKind) ||
            string.IsNullOrWhiteSpace(owner.OwnerSubject))
        {
            throw new InvalidOperationException("team_automation_credential_owner_missing");
        }

        return new ScheduledInvocationAuthorizationOwnerState
        {
            Authority = owner.Authority.Trim(),
            OwnerKind = owner.OwnerKind.Trim(),
            OwnerSubject = owner.OwnerSubject.Trim(),
        };
    }

    private static bool CredentialAuthorizationOwnerEquals(
        ScheduledInvocationAuthorizationOwnerState? left,
        ScheduledInvocationAuthorizationOwnerState? right) =>
        left != null && right != null &&
        string.Equals(left.Authority, right.Authority, StringComparison.Ordinal) &&
        string.Equals(left.OwnerKind, right.OwnerKind, StringComparison.Ordinal) &&
        string.Equals(left.OwnerSubject, right.OwnerSubject, StringComparison.Ordinal);

    private ScheduledInvocationAgentKeyCredentialReferenceState NormalizeTeamCredential(
        ScheduledInvocationAgentKeyCredentialReferenceState? credential)
    {
        if (credential?.SecretReference == null ||
            string.IsNullOrWhiteSpace(credential.SecretReference.Ref) ||
            string.IsNullOrWhiteSpace(credential.SecretReference.OwnerScopeKey) ||
            string.IsNullOrWhiteSpace(credential.ApiKeyId) ||
            credential.KeyExpiresAtUnixMs <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds())
        {
            throw new InvalidOperationException("team_automation_credential_invalid_or_expired");
        }

        if (!string.Equals(
                credential.SecretReference.Purpose,
                CredentialSecretPurposes.ScheduledInvocationAgentKey,
                StringComparison.Ordinal))
        {
            throw TeamAutomationCommandRejectedException.InvalidRequest(
                "team_automation_credential_purpose_invalid");
        }

        return NormalizeScheduledInvocationAgentKey(credential);
    }

    private static bool CredentialEquals(
        ScheduledInvocationAgentKeyCredentialReferenceState? left,
        ScheduledInvocationAgentKeyCredentialReferenceState? right) =>
        left != null && right != null &&
        string.Equals(left.ApiKeyId, right.ApiKeyId, StringComparison.Ordinal) &&
        left.KeyExpiresAtUnixMs == right.KeyExpiresAtUnixMs &&
        SecretReferenceEquals(left.SecretReference, right.SecretReference);

    private static bool SecretReferenceEquals(SecretReference? left, SecretReference? right) =>
        left != null && right != null &&
        string.Equals(left.Ref, right.Ref, StringComparison.Ordinal) &&
        string.Equals(left.Purpose, right.Purpose, StringComparison.Ordinal) &&
        string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.Ordinal) &&
        left.Version == right.Version &&
        string.Equals(left.OwnerScopeKey, right.OwnerScopeKey, StringComparison.Ordinal) &&
        left.CreatedAtUnixMs == right.CreatedAtUnixMs &&
        left.ExpiresAtUnixMs == right.ExpiresAtUnixMs;

    private static void EnsureTeamCredentialMatchesEffectLocator(
        ScheduledInvocationAgentKeyCredentialReferenceState credential,
        ScheduledCredentialEffectLocatorState? locator)
    {
        if (locator == null ||
            !string.Equals(
                credential.SecretReference.Ref,
                locator.RequestedSecretReference,
                StringComparison.Ordinal) ||
            !string.Equals(
                credential.SecretReference.Purpose,
                locator.SecretPurpose,
                StringComparison.Ordinal) ||
            !string.Equals(
                credential.SecretReference.OwnerScopeKey,
                locator.SecretOwnerScopeKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("team_automation_candidate_credential_locator_mismatch");
        }
    }

    private bool HasUsableActiveTeamCredential(DateTimeOffset now) =>
        State.ActiveTeamCredential != null &&
        State.TeamCredentialExpiresAt?.ToDateTimeOffset() > now &&
        State.TeamAutomationLifecycleStatus is TeamAutomationLifecycleStatusState.Active or
            TeamAutomationLifecycleStatusState.ReplacementPending;

    private ScheduledDispatchConfiguredEvent NormalizeTeamAutomationActivationConfiguration(
        ScheduledDispatchConfiguredEvent? source,
        TeamMemberAutomationOwnerState owner,
        ScheduledInvocationAgentKeyCredentialReferenceState credential)
    {
        if (source == null)
            throw new InvalidOperationException("team_automation_configuration_required");
        var configuredOwner = NormalizeTeamAutomationOwner(source.TeamAutomationOwner);
        if (!TeamAutomationOwnerEquals(owner, configuredOwner))
        {
            throw TeamAutomationCommandRejectedException.Conflict(
                "team_automation_activation_decision_mismatch");
        }
        EnsureValidDefinition(
            source.TargetActorId,
            source.Target,
            source.TriggerEnvelope,
            source.CronExpression,
            source.Timezone,
            source.ScheduleKind,
            source.ScheduleMode,
            source.OneShotFireAt);

        var normalizedMode = NormalizeScheduleMode(source.ScheduleMode);
        var normalizedTarget = NormalizeTarget(source.Target, source.ScheduleKind);
        var authorizationFact = normalizedTarget.ServiceInvocation?.AuthorizationFact;
        if (!CredentialEquals(normalizedTarget.ServiceInvocation?.Auth?.ScheduledInvocationAgentKey, credential) ||
            authorizationFact?.Owner == null ||
            string.IsNullOrWhiteSpace(authorizationFact.Owner.Authority) ||
            string.IsNullOrWhiteSpace(authorizationFact.Owner.OwnerKind) ||
            string.IsNullOrWhiteSpace(authorizationFact.Owner.OwnerSubject))
        {
            throw new InvalidOperationException("team_automation_configuration_not_applied");
        }
        if (!string.Equals(
                authorizationFact.PermissionDigest,
                State.TeamAutomationPermissionDigest,
                StringComparison.Ordinal) ||
            !string.Equals(
                authorizationFact.PolicyVersion,
                State.TeamAutomationPolicyVersion,
                StringComparison.Ordinal))
        {
            throw TeamAutomationCommandRejectedException.Conflict(
                "team_automation_activation_decision_mismatch");
        }

        EnsureCredentialRequirementAllowed(
            IsConfigured()
                ? ScheduledDispatchCredentialRequirementOperation.Update
                : ScheduledDispatchCredentialRequirementOperation.Create,
            NormalizeRequired(source.ScheduleId, nameof(source.ScheduleId)),
            source.ScheduleKind,
            normalizedTarget,
            source.Headers);

        var configured = new ScheduledDispatchConfiguredEvent
        {
            ScheduleId = NormalizeRequired(source.ScheduleId, nameof(source.ScheduleId)),
            DisplayName = NormalizeOptional(source.DisplayName),
            TargetActorId = NormalizeOptional(source.TargetActorId),
            TriggerEnvelope = NormalizeTriggerEnvelope(source.TriggerEnvelope),
            CronExpression = NormalizeCronExpression(normalizedMode, source.CronExpression),
            Timezone = ScheduledDispatchCalculator.NormalizeTimezone(source.Timezone),
            Enabled = source.Enabled,
            ConfiguredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            PayloadTypeUrl = ResolvePayloadTypeUrl(source.TriggerEnvelope),
            Target = normalizedTarget,
            ScheduleKind = source.ScheduleKind,
            ScheduleMode = normalizedMode,
            OneShotFireAt = NormalizeOneShotFireAt(normalizedMode, source.OneShotFireAt) is { } oneShot
                ? Timestamp.FromDateTimeOffset(oneShot)
                : null,
            TeamAutomationOwner = configuredOwner,
        };
        foreach (var (key, value) in NormalizeHeaders(source.Headers))
            configured.Headers[key] = value;
        EnsurePreparedServiceInvocationMatchesConfiguration(configured);
        return configured;
    }

    private static TeamAutomationActivationDecisionState NormalizeTeamAutomationActivationDecision(
        TeamAutomationActivationDecisionState? source)
    {
        if (source == null)
        {
            throw TeamAutomationCommandRejectedException.InvalidRequest(
                "team_automation_activation_decision_required");
        }

        var mode = NormalizeScheduleMode(source.ScheduleMode);
        var normalized = new TeamAutomationActivationDecisionState
        {
            ScheduleId = NormalizeOptional(source.ScheduleId),
            DisplayName = NormalizeOptional(source.DisplayName),
            EndpointId = NormalizeOptional(source.EndpointId),
            CronExpression = NormalizeCronExpression(mode, source.CronExpression),
            Timezone = ScheduledDispatchCalculator.NormalizeTimezone(source.Timezone),
            Enabled = source.Enabled,
            ScheduleKind = source.ScheduleKind,
            ScheduleMode = mode,
            OneShotFireAt = NormalizeOneShotFireAt(mode, source.OneShotFireAt) is { } fireAt
                ? Timestamp.FromDateTimeOffset(fireAt)
                : null,
            CredentialRequirementTargetKind = source.CredentialRequirementTargetKind,
            RevisionId = NormalizeOptional(source.RevisionId),
        };
        if (source.Owner != null)
            normalized.Owner = NormalizeTeamAutomationOwner(source.Owner);
        if (source.ServiceIdentity != null)
        {
            normalized.ServiceIdentity = new ServiceIdentity
            {
                TenantId = NormalizeOptional(source.ServiceIdentity.TenantId),
                AppId = NormalizeOptional(source.ServiceIdentity.AppId),
                Namespace = NormalizeOptional(source.ServiceIdentity.Namespace),
                ServiceId = NormalizeOptional(source.ServiceIdentity.ServiceId),
            };
        }
        if (source.Payload != null)
        {
            normalized.Payload =
                ScheduledServiceInvocationPayloadPolicy.StripScheduleOwnedCredentialFields(source.Payload);
        }
        if (source.CallerAuthority != null)
            normalized.CallerAuthority = NormalizeCallerAuthority(source.CallerAuthority);
        if (source.AuthorizationFact != null)
            normalized.AuthorizationFact = NormalizeTeamAutomationAuthorizationFact(source.AuthorizationFact);
        if (source.Caller != null)
        {
            normalized.Caller = new ServiceInvocationCaller
            {
                ServiceKey = NormalizeOptional(source.Caller.ServiceKey),
                TenantId = NormalizeOptional(source.Caller.TenantId),
                AppId = NormalizeOptional(source.Caller.AppId),
            };
        }
        foreach (var (key, value) in NormalizeHeaders(source.Headers).OrderBy(
                     static entry => entry.Key,
                     StringComparer.Ordinal))
        {
            normalized.Headers[key] = value;
        }
        return normalized;
    }

    private static ScheduledInvocationAuthorizationFactState NormalizeTeamAutomationAuthorizationFact(
        ScheduledInvocationAuthorizationFactState source)
    {
        var normalized = new ScheduledInvocationAuthorizationFactState
        {
            PermissionDigest = NormalizeOptional(source.PermissionDigest),
            PolicyVersion = NormalizeOptional(source.PolicyVersion),
            Scopes = NormalizeOptional(source.Scopes),
            ExpiresAt = source.ExpiresAt == null
                ? null
                : Timestamp.FromDateTimeOffset(source.ExpiresAt.ToDateTimeOffset().ToUniversalTime()),
            ServiceGrantsNotRequired = source.ServiceGrantsNotRequired,
            Disclosure = source.Disclosure?.Clone(),
            Authority = source.Authority?.Clone(),
        };
        if (source.Owner != null)
        {
            normalized.Owner = new ScheduledInvocationAuthorizationOwnerState
            {
                Authority = NormalizeOptional(source.Owner.Authority),
                OwnerKind = NormalizeOptional(source.Owner.OwnerKind),
                OwnerSubject = NormalizeOptional(source.Owner.OwnerSubject),
            };
        }
        if (normalized.Authority != null)
        {
            normalized.Authority.CatalogContentDigest =
                NormalizeOptional(normalized.Authority.CatalogContentDigest);
            normalized.Authority.CatalogContractVersion =
                NormalizeOptional(normalized.Authority.CatalogContractVersion);
            normalized.Authority.CatalogPolicyVersion =
                NormalizeOptional(normalized.Authority.CatalogPolicyVersion);
        }
        if (source.OwnerLlmSelection != null)
        {
            normalized.OwnerLlmSelection = source.OwnerLlmSelection.Clone();
            normalized.OwnerLlmSelection.RouteValue =
                NormalizeOptional(normalized.OwnerLlmSelection.RouteValue);
            normalized.OwnerLlmSelection.NyxIdUserServiceId =
                NormalizeOptional(normalized.OwnerLlmSelection.NyxIdUserServiceId);
            normalized.OwnerLlmSelection.ServiceSlugSnapshot =
                NormalizeOptional(normalized.OwnerLlmSelection.ServiceSlugSnapshot);
            normalized.OwnerLlmSelection.Model = NormalizeOptional(normalized.OwnerLlmSelection.Model);
        }
        normalized.ServiceGrants.Add(source.ServiceGrants
            .Select(static grant =>
            {
                var normalizedGrant = new ScheduledInvocationAuthorizationServiceGrantState
                {
                    ServiceId = NormalizeOptional(grant.ServiceId),
                    NodeGrantsNotRequired = grant.NodeGrantsNotRequired,
                };
                normalizedGrant.NodeIds.Add(grant.NodeIds
                    .Select(static nodeId => NormalizeOptional(nodeId))
                    .Order(StringComparer.Ordinal));
                return normalizedGrant;
            })
            .OrderBy(static grant => grant.ServiceId, StringComparer.Ordinal)
            .ThenBy(static grant => grant.NodeGrantsNotRequired)
            .ThenBy(static grant => string.Join('\n', grant.NodeIds), StringComparer.Ordinal));
        return normalized;
    }

    private static void EnsureValidTeamAutomationActivationDecision(
        TeamAutomationActivationDecisionState decision,
        string scheduleId,
        TeamMemberAutomationOwnerState owner,
        string permissionDigest,
        string policyVersion)
    {
        var fact = decision.AuthorizationFact;
        var authority = decision.CallerAuthority;
        if (!string.Equals(decision.ScheduleId, scheduleId, StringComparison.Ordinal) ||
            !TeamAutomationOwnerAssignmentEquals(decision.Owner, owner) ||
            decision.ServiceIdentity == null ||
            string.IsNullOrWhiteSpace(decision.ServiceIdentity.TenantId) ||
            string.IsNullOrWhiteSpace(decision.ServiceIdentity.AppId) ||
            string.IsNullOrWhiteSpace(decision.ServiceIdentity.Namespace) ||
            string.IsNullOrWhiteSpace(decision.ServiceIdentity.ServiceId) ||
            string.IsNullOrWhiteSpace(decision.EndpointId) ||
            decision.Payload == null ||
            string.IsNullOrWhiteSpace(decision.Payload.TypeUrl) ||
            authority == null ||
            string.IsNullOrWhiteSpace(authority.Platform) ||
            string.IsNullOrWhiteSpace(authority.ExternalUserId) ||
            string.IsNullOrWhiteSpace(authority.Scope) ||
            string.IsNullOrWhiteSpace(authority.BindingId) ||
            fact?.Owner == null ||
            string.IsNullOrWhiteSpace(fact.Owner.Authority) ||
            string.IsNullOrWhiteSpace(fact.Owner.OwnerKind) ||
            string.IsNullOrWhiteSpace(fact.Owner.OwnerSubject) ||
            !string.Equals(fact.PermissionDigest, permissionDigest, StringComparison.Ordinal) ||
            !string.Equals(fact.PolicyVersion, policyVersion, StringComparison.Ordinal) ||
            decision.CredentialRequirementTargetKind ==
                ScheduledDispatchCredentialRequirementTargetKindState.Unspecified ||
            decision.ScheduleMode == ScheduledDispatchScheduleModeState.OneShotAtUtc &&
            decision.OneShotFireAt == null ||
            decision.ScheduleMode == ScheduledDispatchScheduleModeState.RecurringCron &&
            string.IsNullOrWhiteSpace(decision.CronExpression))
        {
            throw TeamAutomationCommandRejectedException.InvalidRequest(
                "team_automation_activation_decision_invalid");
        }

        var promptValidation = ScheduledDispatchPromptTemplate.ValidatePayload(decision.Payload);
        if (!promptValidation.Succeeded)
        {
            throw TeamAutomationCommandRejectedException.InvalidRequest(
                "team_automation_activation_decision_invalid");
        }
    }

    private static TeamAutomationActivationDecisionState CreateTeamAutomationActivationDecision(
        ScheduledDispatchConfiguredEvent configuration) =>
        CreateTeamAutomationActivationDecision(
            configuration.ScheduleId,
            configuration.DisplayName,
            configuration.TeamAutomationOwner,
            configuration.Target,
            configuration.CronExpression,
            configuration.Timezone,
            configuration.Enabled,
            configuration.ScheduleKind,
            configuration.Headers,
            configuration.ScheduleMode,
            configuration.OneShotFireAt);

    private TeamAutomationActivationDecisionState CreateInstalledTeamAutomationActivationDecision() =>
        CreateTeamAutomationActivationDecision(
            State.ScheduleId,
            State.DisplayName,
            State.TeamAutomationOwner,
            State.Target,
            State.CronExpression,
            State.Timezone,
            State.Enabled,
            State.ScheduleKind,
            State.Headers,
            State.ScheduleMode,
            State.OneShotFireAt.HasValue
                ? Timestamp.FromDateTimeOffset(State.OneShotFireAt.Value.ToUniversalTime())
                : null);

    private static TeamAutomationActivationDecisionState CreateTeamAutomationActivationDecision(
        string scheduleId,
        string displayName,
        TeamMemberAutomationOwnerState? owner,
        ScheduledDispatchTargetState? target,
        string cronExpression,
        string timezone,
        bool enabled,
        ScheduledDispatchScheduleKindState scheduleKind,
        IEnumerable<KeyValuePair<string, string>> headers,
        ScheduledDispatchScheduleModeState scheduleMode,
        Timestamp? oneShotFireAt)
    {
        var normalizedTarget = NormalizeTarget(target, scheduleKind);
        var serviceInvocation = normalizedTarget.ServiceInvocation;
        var decision = new TeamAutomationActivationDecisionState
        {
            ScheduleId = scheduleId,
            DisplayName = displayName,
            EndpointId = serviceInvocation?.EndpointId ?? string.Empty,
            CronExpression = cronExpression,
            Timezone = timezone,
            Enabled = enabled,
            ScheduleKind = scheduleKind,
            ScheduleMode = scheduleMode,
            OneShotFireAt = oneShotFireAt?.Clone(),
            CredentialRequirementTargetKind = normalizedTarget.CredentialRequirementTargetKind,
            RevisionId = serviceInvocation?.RevisionId ?? string.Empty,
        };
        if (owner != null)
            decision.Owner = owner.Clone();
        if (serviceInvocation?.Identity != null)
            decision.ServiceIdentity = serviceInvocation.Identity.Clone();
        if (serviceInvocation?.Payload != null)
            decision.Payload = serviceInvocation.Payload.Clone();
        if (serviceInvocation?.Auth?.CallerAuthority != null)
            decision.CallerAuthority = serviceInvocation.Auth.CallerAuthority.Clone();
        if (serviceInvocation?.AuthorizationFact != null)
            decision.AuthorizationFact = serviceInvocation.AuthorizationFact.Clone();
        if (serviceInvocation?.Caller != null)
            decision.Caller = serviceInvocation.Caller.Clone();
        foreach (var (key, value) in headers)
            decision.Headers[key] = value;
        return NormalizeTeamAutomationActivationDecision(decision);
    }

    private static bool TeamAutomationActivationDecisionEquals(
        TeamAutomationActivationDecisionState? left,
        TeamAutomationActivationDecisionState? right) =>
        left == null || right == null
            ? left == null && right == null
            : NormalizeTeamAutomationActivationDecision(left).Equals(
                NormalizeTeamAutomationActivationDecision(right));

    private static void EnsurePreparedServiceInvocationMatchesConfiguration(
        ScheduledDispatchConfiguredEvent configuration)
    {
        if (!string.Equals(
                configuration.TargetActorId,
                ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("team_automation_configuration_not_applied");
        }
        var payload = configuration.TriggerEnvelope?.Payload;
        if (payload == null || !payload.Is(ServiceInvocationRequest.Descriptor))
            throw new InvalidOperationException("team_automation_configuration_not_applied");

        var prepared = payload.Unpack<ServiceInvocationRequest>();
        var target = configuration.Target?.ServiceInvocation;
        if (target == null ||
            !Equals(prepared.Identity, target.Identity) ||
            !string.Equals(prepared.EndpointId, target.EndpointId, StringComparison.Ordinal) ||
            !AnyPayloadEquals(prepared.Payload, target.Payload) ||
            !string.Equals(prepared.RevisionId, target.RevisionId, StringComparison.Ordinal) ||
            !Equals(prepared.Caller, target.Caller) ||
            !string.Equals(prepared.ScheduleId, configuration.ScheduleId, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(prepared.RunOrigin) ||
            !string.IsNullOrEmpty(prepared.RequestedRunId) ||
            prepared.WorkflowCompletionNotificationTarget != null ||
            prepared.ServiceRunCompletionNotificationTarget != null)
        {
            throw new InvalidOperationException("team_automation_configuration_not_applied");
        }
    }

    private static bool AnyPayloadEquals(Any? left, Any? right) =>
        left == null || right == null
            ? left == null && right == null
            : string.Equals(left.TypeUrl, right.TypeUrl, StringComparison.Ordinal) &&
              left.Value.Equals(right.Value);

    private async Task PersistTeamAutomationObservationAsync(
        string stage,
        bool ownsEffectAttempt,
        CancellationToken ct,
        string? errorCode = null,
        string? errorMessage = null,
        string? observationRequestId = null,
        bool newOperationCommitted = false)
    {
        var observedAt = _timeProvider.GetUtcNow();
        var effectAttemptId = ownsEffectAttempt ? Guid.NewGuid().ToString("N") : string.Empty;
        var effectAttemptGeneration = ownsEffectAttempt
            ? checked(State.TeamAutomationEffectAttemptGeneration + 1)
            : 0;
        var observed = new TeamAutomationOperationObservedEvent
        {
            ScheduleId = ResolveScheduleId(),
            OperationId = State.TeamAutomationOperationId,
            IdempotencyKey = State.TeamAutomationIdempotencyKey,
            Stage = NormalizeRequired(stage, nameof(stage)),
            OwnsEffectAttempt = ownsEffectAttempt,
            StateVersion = (EventSourcing ?? throw new InvalidOperationException(
                "Event sourcing must be configured before observing a Team automation operation."))
                .CurrentVersion,
            ErrorCode = NormalizeOptional(errorCode),
            ErrorMessage = NormalizeOptional(errorMessage),
            ObservedAtUtc = Timestamp.FromDateTimeOffset(observedAt),
            PendingRevocationCredential = State.PendingRevocationTeamCredential?.Clone(),
            PendingRevocationOwner = State.PendingRevocationTeamCredentialOwner?.Clone(),
            NyxidRevocationPending = State.PendingRevocationTeamCredential != null &&
                                     State.NyxidRevocationStatus != TeamAutomationEffectTrackStatusState.Completed,
            VaultRevocationPending = State.PendingRevocationTeamCredential != null &&
                                     State.VaultRevocationStatus != TeamAutomationEffectTrackStatusState.Completed,
            EffectAttemptId = effectAttemptId,
            EffectAttemptGeneration = effectAttemptGeneration,
            EffectAttemptExpiresAt = ownsEffectAttempt
                ? Timestamp.FromDateTimeOffset(observedAt + TeamAutomationEffectAttemptLeaseDuration)
                : null,
            CandidateCredential = State.CandidateTeamCredential?.Clone(),
            CandidateOwner = State.CandidateTeamCredentialOwner?.Clone(),
            CredentialEffectLocator = State.TeamCredentialEffectLocator?.Clone(),
            MutationDigest = State.TeamAutomationMutationDigest,
            ObservationRequestId = NormalizeOptional(observationRequestId),
            ObservationStatus = TeamAutomationOperationObservationStatusState.Committed,
            NewOperationCommitted = newOperationCommitted,
        };
        await PersistDomainEventAsync(observed, ct);
    }

    private async Task ExecuteObservedTeamAutomationCommandAsync(
        string? scheduleId,
        string? operationId,
        string? idempotencyKey,
        string stage,
        string? observationRequestId,
        Func<Task> executeAsync)
    {
        try
        {
            await executeAsync();
        }
        catch (TeamAutomationCommandRejectedException ex) when (
            !string.IsNullOrWhiteSpace(observationRequestId))
        {
            await PersistTeamAutomationRejectionAsync(
                scheduleId,
                operationId,
                idempotencyKey,
                stage,
                observationRequestId,
                ex.Status,
                ex.StableCode,
                CancellationToken.None);
        }
    }

    private async Task PersistTeamAutomationRejectionAsync(
        string? scheduleId,
        string? operationId,
        string? idempotencyKey,
        string stage,
        string? observationRequestId,
        TeamAutomationOperationObservationStatusState status,
        string stableCode,
        CancellationToken ct)
    {
        var observed = new TeamAutomationOperationObservedEvent
        {
            ScheduleId = NormalizeOptional(scheduleId),
            OperationId = NormalizeOptional(operationId),
            IdempotencyKey = NormalizeOptional(idempotencyKey),
            Stage = NormalizeRequired(stage, nameof(stage)),
            OwnsEffectAttempt = false,
            StateVersion = (EventSourcing ?? throw new InvalidOperationException(
                "Event sourcing must be configured before observing a Team automation operation."))
                .CurrentVersion,
            ErrorCode = NormalizeStableErrorCode(stableCode),
            ObservedAtUtc = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            ObservationRequestId = NormalizeRequired(
                observationRequestId,
                nameof(observationRequestId)),
            ObservationStatus = status,
        };
        await PersistDomainEventAsync(observed, ct);
    }

    private sealed class TeamAutomationCommandRejectedException : InvalidOperationException
    {
        private TeamAutomationCommandRejectedException(
            TeamAutomationOperationObservationStatusState status,
            string stableCode)
            : base(stableCode)
        {
            Status = status;
            StableCode = stableCode;
        }

        public TeamAutomationOperationObservationStatusState Status { get; }

        public string StableCode { get; }

        public static TeamAutomationCommandRejectedException InvalidRequest(string stableCode) =>
            new(TeamAutomationOperationObservationStatusState.RejectedInvalidRequest, stableCode);

        public static TeamAutomationCommandRejectedException Conflict(string stableCode) =>
            new(TeamAutomationOperationObservationStatusState.RejectedConflict, stableCode);

        public static TeamAutomationCommandRejectedException Unauthorized(string stableCode) =>
            new(TeamAutomationOperationObservationStatusState.RejectedUnauthorized, stableCode);

        public static TeamAutomationCommandRejectedException NotFound(string stableCode) =>
            new(TeamAutomationOperationObservationStatusState.RejectedNotFound, stableCode);
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

    private static void EnsureValidDefinition(
        string? targetActorId,
        ScheduledDispatchTargetState? target,
        EventEnvelope? triggerEnvelope,
        string cronExpression,
        string timezone,
        ScheduledDispatchScheduleKindState scheduleKind,
        ScheduledDispatchScheduleModeState scheduleMode,
        Timestamp? oneShotFireAt)
    {
        if (triggerEnvelope == null || triggerEnvelope.Payload == null)
            throw new ArgumentException("Trigger envelope with payload is required.", nameof(triggerEnvelope));
        if (target == null || target.Kind == ScheduledDispatchTargetKindState.Unspecified)
            throw new ArgumentException("Scheduled dispatch typed target is required.", nameof(target));
        var normalizedTarget = NormalizeTarget(target, scheduleKind);
        var promptValidation = ScheduledDispatchPromptTemplate.ValidatePayload(
            normalizedTarget.ServiceInvocation?.Payload);
        if (!promptValidation.Succeeded)
            throw new ArgumentException(promptValidation.Error, nameof(target));
        if (normalizedTarget.Kind == ScheduledDispatchTargetKindState.Envelope &&
            !HasTrustedInternalEnvelopeAuthority(normalizedTarget))
        {
            throw new ArgumentException(
                "Scheduled dispatch envelope target requires trusted internal authority.",
                nameof(target));
        }
        _ = NormalizeRequired(targetActorId, nameof(targetActorId));

        var normalizedMode = NormalizeScheduleMode(scheduleMode);
        if (normalizedMode == ScheduledDispatchScheduleModeState.OneShotAtUtc)
        {
            var normalizedOneShotFireAt = NormalizeOneShotFireAt(normalizedMode, oneShotFireAt);
            if (!normalizedOneShotFireAt.HasValue)
                throw new ArgumentException("One-shot fire time is required.", nameof(oneShotFireAt));
            if (normalizedOneShotFireAt.Value <= DateTimeOffset.UtcNow)
                throw new ArgumentException("One-shot fire time must be in the future.", nameof(oneShotFireAt));
            return;
        }

        var normalizedCronExpression = NormalizeRequired(cronExpression, nameof(cronExpression));
        if (!ScheduledDispatchCalculator.TryGetNextOccurrence(
                normalizedCronExpression,
                timezone,
                DateTimeOffset.UtcNow,
                out _,
                out var error))
        {
            throw new ArgumentException(error ?? "Schedule is invalid.", nameof(cronExpression));
        }
    }

    private static ScheduledDispatchTargetState NormalizeTarget(
        ScheduledDispatchTargetState? target,
        ScheduledDispatchScheduleKindState scheduleKind = ScheduledDispatchScheduleKindState.Generic)
    {
        if (target == null)
            return new ScheduledDispatchTargetState();

        return target.Kind switch
        {
            ScheduledDispatchTargetKindState.ServiceInvocation => new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ServiceInvocation = NormalizeServiceInvocationTarget(target.ServiceInvocation),
                CredentialRequirementTargetKind = ResolveCredentialRequirementTargetKind(
                    target.CredentialRequirementTargetKind,
                    ScheduledDispatchTargetKindState.ServiceInvocation,
                    scheduleKind),
            },
            ScheduledDispatchTargetKindState.Envelope => new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.Envelope,
                ActorId = NormalizeOptional(target.ActorId),
                Envelope = target.Envelope == null ? null : NormalizeTriggerEnvelope(target.Envelope),
                EnvelopeAuthority = target.EnvelopeAuthority,
                CredentialRequirementTargetKind = ResolveCredentialRequirementTargetKind(
                    target.CredentialRequirementTargetKind,
                    ScheduledDispatchTargetKindState.Envelope,
                    scheduleKind),
            },
            _ => new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.Envelope,
                ActorId = NormalizeOptional(target.ActorId),
                Envelope = target.Envelope == null ? null : NormalizeTriggerEnvelope(target.Envelope),
                EnvelopeAuthority = target.EnvelopeAuthority,
                CredentialRequirementTargetKind = ResolveCredentialRequirementTargetKind(
                    target.CredentialRequirementTargetKind,
                    ScheduledDispatchTargetKindState.Envelope,
                    scheduleKind),
            },
        };
    }

    private static ScheduledDispatchCredentialRequirementTargetKindState ResolveCredentialRequirementTargetKind(
        ScheduledDispatchCredentialRequirementTargetKindState configuredKind,
        ScheduledDispatchTargetKindState targetKind,
        ScheduledDispatchScheduleKindState scheduleKind)
    {
        if (configuredKind != ScheduledDispatchCredentialRequirementTargetKindState.Unspecified)
            return configuredKind;
        if (targetKind == ScheduledDispatchTargetKindState.Envelope)
            return ScheduledDispatchCredentialRequirementTargetKindState.Envelope;
        if (targetKind == ScheduledDispatchTargetKindState.ServiceInvocation &&
            scheduleKind == ScheduledDispatchScheduleKindState.Workflow)
        {
            return ScheduledDispatchCredentialRequirementTargetKindState.WorkflowService;
        }

        return ScheduledDispatchCredentialRequirementTargetKindState.Unspecified;
    }

    private static ScheduledServiceInvocationTargetState NormalizeServiceInvocationTarget(
        ScheduledServiceInvocationTargetState? serviceInvocation)
    {
        if (serviceInvocation == null)
            return new ScheduledServiceInvocationTargetState();

        return new ScheduledServiceInvocationTargetState
        {
            Identity = serviceInvocation.Identity?.Clone(),
            EndpointId = NormalizeOptional(serviceInvocation.EndpointId),
            Payload = serviceInvocation.Payload == null
                ? null
                : ScheduledServiceInvocationPayloadPolicy.StripScheduleOwnedCredentialFields(serviceInvocation.Payload),
            RevisionId = NormalizeOptional(serviceInvocation.RevisionId),
            Caller = serviceInvocation.Caller?.Clone(),
            Auth = NormalizeServiceInvocationAuth(serviceInvocation.Auth),
            AuthorizationFact = serviceInvocation.AuthorizationFact?.Clone(),
        };
    }

    private static ScheduledInvocationAuthorizationFact? ToRuntimeAuthorizationFact(
        ScheduledInvocationAuthorizationFactState? fact)
    {
        if (fact == null)
            return null;

        return new ScheduledInvocationAuthorizationFact(
            fact.PermissionDigest,
            fact.PolicyVersion,
            new ScheduledInvocationAuthorizationOwner(
                fact.Owner?.Authority ?? string.Empty,
                fact.Owner?.OwnerKind ?? string.Empty,
                fact.Owner?.OwnerSubject ?? string.Empty),
            fact.ServiceGrants.Select(static grant => new ScheduledInvocationAuthorizationServiceGrant(
                grant.ServiceId,
                grant.NodeIds.ToArray(),
                grant.NodeGrantsNotRequired)).ToArray(),
            fact.Scopes,
            fact.ExpiresAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            fact.ServiceGrantsNotRequired,
            new ScheduledInvocationAuthorizationDisclosure(
                fact.Disclosure?.DedicatedToSchedule ?? false,
                fact.Disclosure?.SecretManagedByAevatar ?? false,
                fact.Disclosure?.BrowserReceivesRawKey ?? false,
                fact.Disclosure?.DeleteRevokesCredential ?? false,
                fact.Disclosure?.PauseResumeRevokesCredential ?? false),
            new ScheduledInvocationAuthorizationAuthority(
                fact.Authority?.MemberStateVersion ?? 0,
                fact.Authority?.WorkflowStateVersion ?? 0,
                fact.Authority?.ConnectorStateVersion ?? 0,
                fact.Authority?.OwnerLlmStateVersion ?? 0,
                fact.Authority?.CatalogStateVersion ?? 0,
                fact.Authority?.CatalogObservedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                fact.Authority?.CatalogFreshUntil?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                fact.Authority?.CatalogContentDigest ?? string.Empty,
                fact.Authority?.CatalogContractVersion ?? string.Empty,
                fact.Authority?.CatalogPolicyVersion ?? string.Empty,
                fact.Authority?.CatalogEvaluatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue),
            fact.OwnerLlmSelection?.Clone());
    }

    private static EventEnvelope NormalizeTriggerEnvelope(EventEnvelope triggerEnvelope)
    {
        var normalized = triggerEnvelope.Clone();
        if (normalized.Payload != null)
            normalized.Payload = ScheduledServiceInvocationPayloadPolicy.StripScheduleOwnedCredentialFields(normalized.Payload);

        return normalized;
    }

    private ScheduledDispatchTargetState PreserveExistingServiceInvocationAuth(
        ScheduledDispatchTargetState normalizedTarget,
        bool isCreate)
    {
        if (isCreate || normalizedTarget.Kind != ScheduledDispatchTargetKindState.ServiceInvocation)
            return normalizedTarget;
        if (normalizedTarget.ServiceInvocation?.Auth != null &&
            normalizedTarget.ServiceInvocation.AuthorizationFact != null)
            return normalizedTarget;

        var existingAuth = NormalizeServiceInvocationAuth(State.Target?.ServiceInvocation?.Auth);
        var existingAuthorizationFact = State.Target?.ServiceInvocation?.AuthorizationFact?.Clone();
        if (existingAuth == null && existingAuthorizationFact == null)
            return normalizedTarget;

        var preserved = normalizedTarget.Clone();
        preserved.ServiceInvocation ??= new ScheduledServiceInvocationTargetState();
        preserved.ServiceInvocation.Auth ??= existingAuth;
        preserved.ServiceInvocation.AuthorizationFact ??= existingAuthorizationFact;
        return preserved;
    }

    private static ScheduledServiceInvocationAuthState? NormalizeServiceInvocationAuth(
        ScheduledServiceInvocationAuthState? auth)
    {
        if (auth == null)
            return null;

        var hasLegacyDurableToken = !string.IsNullOrWhiteSpace(auth.DurableSenderBearerToken) ||
                                    auth.LegacyDurableSenderBearerBlocked;
        var callerAuthority = NormalizeCallerAuthority(auth.CallerAuthority);
        if (hasLegacyDurableToken)
        {
            return new ScheduledServiceInvocationAuthState
            {
                LegacyDurableSenderBearerBlocked = true,
                CallerAuthority = callerAuthority,
            };
        }

        if (auth.SourceCase == ScheduledServiceInvocationAuthState.SourceOneofCase.Durable)
        {
            var durable = NormalizeDurableCredentialReference(auth.Durable);
            return durable == null
                ? null
                : new ScheduledServiceInvocationAuthState
                {
                    Durable = durable,
                    CallerAuthority = callerAuthority,
                };
        }

        if (auth.SourceCase == ScheduledServiceInvocationAuthState.SourceOneofCase.ScheduledInvocationAgentKey)
        {
            return auth.ScheduledInvocationAgentKey == null
                ? null
                : new ScheduledServiceInvocationAuthState
                {
                    ScheduledInvocationAgentKey = NormalizeScheduledInvocationAgentKey(auth.ScheduledInvocationAgentKey),
                    CallerAuthority = callerAuthority,
                };
        }

        var nyxId = ResolveNyxIdSource(auth);
        if (nyxId == null)
            return null;

        var normalized = new ScheduledServiceInvocationAuthState
        {
            NyxId = NormalizeNyxIdSource(nyxId),
            CallerAuthority = callerAuthority,
        };

        return normalized;
    }

    private static ScheduledCallerNyxIdAuthority? NormalizeCallerAuthority(
        ScheduledCallerNyxIdAuthority? source) =>
        source == null
            ? null
            : new ScheduledCallerNyxIdAuthority
            {
                Platform = NormalizeOptional(source.Platform),
                Tenant = NormalizeOptional(source.Tenant),
                ExternalUserId = NormalizeOptional(source.ExternalUserId),
                Scope = NormalizeOptional(source.Scope),
                BindingId = NormalizeOptional(source.BindingId),
            };

    private static ScheduledServiceInvocationDurableCredentialReferenceState? NormalizeDurableCredentialReference(
        ScheduledServiceInvocationDurableCredentialReferenceState? source) =>
        source == null || string.IsNullOrWhiteSpace(source.CredentialId)
            ? null
            : new ScheduledServiceInvocationDurableCredentialReferenceState
            {
                CredentialId = NormalizeOptional(source.CredentialId),
                SecretReference = NormalizeSecretReference(source.SecretReference),
            };

    private static SecretReference? NormalizeSecretReference(SecretReference? reference) =>
        reference == null
            ? null
            : new SecretReference
            {
                Ref = NormalizeOptional(reference.Ref),
                Purpose = NormalizeOptional(reference.Purpose),
                Fingerprint = NormalizeOptional(reference.Fingerprint),
                Version = reference.Version,
                OwnerScopeKey = NormalizeOptional(reference.OwnerScopeKey),
                CreatedAtUnixMs = reference.CreatedAtUnixMs,
                ExpiresAtUnixMs = reference.ExpiresAtUnixMs,
            };

    private static ScheduledServiceInvocationNyxIdCredentialSourceState NormalizeNyxIdSource(
        ScheduledServiceInvocationNyxIdCredentialSourceState source)
    {
        var normalized = new ScheduledServiceInvocationNyxIdCredentialSourceState
        {
            Subject = NormalizeSubject(source.Subject),
            Scope = NormalizeOptional(source.Scope),
            Role = NormalizeRole(source.Role),
        };

        return normalized;
    }

    private static ScheduledServiceInvocationNyxIdCredentialRoleState NormalizeRole(
        ScheduledServiceInvocationNyxIdCredentialRoleState role) =>
        role == ScheduledServiceInvocationNyxIdCredentialRoleState.ScopeOwner
            ? ScheduledServiceInvocationNyxIdCredentialRoleState.ScopeOwner
            : ScheduledServiceInvocationNyxIdCredentialRoleState.Sender;

    private static ScheduledInvocationAgentKeyCredentialReferenceState NormalizeScheduledInvocationAgentKey(
        ScheduledInvocationAgentKeyCredentialReferenceState source) =>
        new()
        {
            SecretReference = source.SecretReference?.Clone(),
            ApiKeyId = NormalizeOptional(source.ApiKeyId),
            KeyExpiresAtUnixMs = source.KeyExpiresAtUnixMs,
        };

    private static ScheduledServiceInvocationNyxIdSubjectRefState? NormalizeSubject(
        ScheduledServiceInvocationNyxIdSubjectRefState? subject) =>
        subject == null
            ? null
            : new ScheduledServiceInvocationNyxIdSubjectRefState
            {
                Platform = NormalizeOptional(subject.Platform),
                Tenant = NormalizeOptional(subject.Tenant),
                ExternalUserId = NormalizeOptional(subject.ExternalUserId),
            };

    private static bool HasServiceInvocationAuth(ScheduledDispatchTargetState? target) =>
        target?.ServiceInvocation?.Auth != null;

    private static bool HasScopeOwnerNyxId(ScheduledDispatchTargetState? target) =>
        target?.ServiceInvocation?.Auth is { } auth &&
        ResolveNyxIdSource(auth)?.Role == ScheduledServiceInvocationNyxIdCredentialRoleState.ScopeOwner;

    private static bool HasSenderNyxId(ScheduledDispatchTargetState? target) =>
        target?.ServiceInvocation?.Auth is { } auth &&
        ResolveNyxIdSource(auth)?.Role == ScheduledServiceInvocationNyxIdCredentialRoleState.Sender;

    private static bool HasDurableCredentialReference(ScheduledDispatchTargetState? target) =>
        target?.ServiceInvocation?.Auth?.SourceCase == ScheduledServiceInvocationAuthState.SourceOneofCase.Durable;

    private static bool HasScheduledInvocationAgentKey(ScheduledDispatchTargetState? target) =>
        target?.ServiceInvocation?.Auth?.ScheduledInvocationAgentKey != null;

    private static bool HasLegacyDurableSenderBearerBlocked(ScheduledDispatchTargetState? target) =>
        target?.ServiceInvocation?.Auth?.LegacyDurableSenderBearerBlocked == true ||
        !string.IsNullOrWhiteSpace(target?.ServiceInvocation?.Auth?.DurableSenderBearerToken);

    private ScheduledDispatchState ApplyConfigured(
        ScheduledDispatchState current,
        ScheduledDispatchConfiguredEvent evt)
    {
        var next = current.Clone();
        var configuredAt = evt.ConfiguredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        var scheduleId = NormalizeRequired(evt.ScheduleId, nameof(evt.ScheduleId));
        if (string.IsNullOrWhiteSpace(next.ScheduleId))
        {
            next.ScheduleId = scheduleId;
            next.CreatedAt = configuredAt;
        }

        next.ScheduleId = scheduleId;
        next.DisplayName = evt.DisplayName ?? string.Empty;
        var normalizedTriggerEnvelope = evt.TriggerEnvelope == null
            ? null
            : NormalizeTriggerEnvelope(evt.TriggerEnvelope);
        next.TargetActorId = evt.TargetActorId ?? string.Empty;
        next.TriggerEnvelope = normalizedTriggerEnvelope;
        next.CronExpression = evt.CronExpression ?? string.Empty;
        next.Timezone = ScheduledDispatchCalculator.NormalizeTimezone(evt.Timezone);
        next.Enabled = evt.Enabled;
        next.UpdatedAt = configuredAt;
        next.PayloadTypeUrl = evt.PayloadTypeUrl ?? ResolvePayloadTypeUrl(normalizedTriggerEnvelope);
        next.Headers.Clear();
        foreach (var (key, value) in NormalizeHeaders(evt.Headers))
            next.Headers[key] = value;
        next.Target = NormalizeTarget(evt.Target, evt.ScheduleKind);
        next.ScheduleKind = evt.ScheduleKind;
        next.ScheduleMode = NormalizeScheduleMode(evt.ScheduleMode);
        next.OneShotFireAt = NormalizeOneShotFireAt(next.ScheduleMode, evt.OneShotFireAt);
        if (evt.TeamAutomationOwner != null)
            next.TeamAutomationOwner = NormalizeTeamAutomationOwner(evt.TeamAutomationOwner);
        next.Completed = false;
        next.CompletedAt = null;
        if (!next.Enabled)
        {
            next.NextFireAt = null;
            next.NextFireLease = null;
            next.PendingNextFireAt = null;
            next.PendingNextFireRequestedAt = null;
        }

        return next;
    }

    private ScheduledDispatchState ApplyEnabled(ScheduledDispatchState current, ScheduledDispatchEnabledEvent evt)
    {
        var next = current.Clone();
        next.Enabled = true;
        next.UpdatedAt = evt.EnabledAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private ScheduledDispatchState ApplyDisabled(ScheduledDispatchState current, ScheduledDispatchDisabledEvent evt)
    {
        var next = current.Clone();
        next.Enabled = false;
        next.NextFireAt = null;
        next.NextFireLease = null;
        next.PendingNextFireAt = null;
        next.PendingNextFireRequestedAt = null;
        next.UpdatedAt = evt.DisabledAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private ScheduledDispatchState ApplyDeleted(ScheduledDispatchState current, ScheduledDispatchDeletedEvent evt)
    {
        var next = ApplyDisabled(current, new ScheduledDispatchDisabledEvent
        {
            Reason = evt.Reason ?? string.Empty,
            DisabledAt = evt.DeletedAt?.Clone(),
        });
        var deletedAt = evt.DeletedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        next.Deleted = true;
        next.DeletedAt = deletedAt;
        next.TeamCredentialExpiryLease = null;
        next.PendingTeamCredentialExpiryAt = null;
        next.PendingTeamCredentialExpiryGeneration = 0;
        next.TeamAutomationActivationDecision = null;
        if (next.TeamAutomationOperationKind ==
                TeamAutomationOperationKindState.Delete &&
            !next.HasTeamAutomationDeleteReason)
        {
            next.TeamAutomationDeleteReason =
                NormalizeOptional(evt.Reason);
        }
        next.UpdatedAt = deletedAt;
        if (next.PendingRevocationTeamCredential != null)
            next.TeamAutomationLifecycleStatus = TeamAutomationLifecycleStatusState.RevocationPending;
        return next;
    }

    private static ScheduledDispatchState ApplyTeamAutomationCredentialOperationBegan(
        ScheduledDispatchState current,
        TeamAutomationCredentialOperationBeganEvent evt)
    {
        var next = current.Clone();
        next.ScheduleId = evt.ScheduleId ?? string.Empty;
        next.TeamAutomationOwner = evt.Owner?.Clone();
        next.TeamAutomationOperationId = evt.OperationId ?? string.Empty;
        next.TeamAutomationIdempotencyKey = evt.IdempotencyKey ?? string.Empty;
        next.TeamAutomationPermissionDigest = evt.PermissionDigest ?? string.Empty;
        next.TeamAutomationPolicyVersion = evt.PolicyVersion ?? string.Empty;
        next.TeamAutomationOperationKind = evt.OperationKind;
        next.TeamCredentialEffectLocator = evt.CredentialEffectLocator?.Clone();
        next.TeamAutomationActivationDecision = evt.ActivationDecision?.Clone();
        next.TeamAutomationMutationDigest = evt.MutationDigest ?? string.Empty;
        next.TeamAutomationLifecycleStatus = evt.OperationKind == TeamAutomationOperationKindState.Reauthorize
            ? TeamAutomationLifecycleStatusState.ReplacementPending
            : TeamAutomationLifecycleStatusState.ProvisioningPending;
        next.CandidateTeamCredential = null;
        next.CandidateTeamCredentialOwner = null;
        ClearTeamAutomationEffectAttempt(next);
        next.LastAuthorizationErrorCode = string.Empty;
        next.UpdatedAt = evt.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private static ScheduledDispatchState ApplyTeamAutomationCredentialCandidateRecorded(
        ScheduledDispatchState current,
        TeamAutomationCredentialCandidateRecordedEvent evt)
    {
        var next = current.Clone();
        next.CandidateTeamCredential = evt.Credential?.Clone();
        next.CandidateTeamCredentialOwner = evt.CredentialOwner?.Clone();
        next.UpdatedAt = evt.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private ScheduledDispatchState ApplyTeamAutomationCredentialActivated(
        ScheduledDispatchState current,
        TeamAutomationCredentialActivatedEvent evt)
    {
        var next = evt.Configuration == null
            ? current.Clone()
            : ApplyConfigured(current, evt.Configuration);
        next.TeamAutomationOwner = evt.Owner?.Clone();
        next.ActiveTeamCredential = evt.Credential?.Clone();
        next.ActiveTeamCredentialOwner = evt.CredentialOwner?.Clone();
        next.ActiveTeamAuthorizationFact = next.Target?.ServiceInvocation?.AuthorizationFact?.Clone();
        next.CandidateTeamCredential = null;
        next.CandidateTeamCredentialOwner = null;
        next.TeamAutomationActivationDecision = null;
        next.PendingRevocationTeamCredential = evt.ReplacedCredential?.Clone();
        next.PendingRevocationTeamCredentialOwner = evt.ReplacedCredentialOwner?.Clone();
        next.TeamCredentialGeneration = evt.Generation;
        next.TeamCredentialExpiresAt = evt.Credential?.KeyExpiresAtUnixMs > 0
            ? Timestamp.FromDateTimeOffset(
                DateTimeOffset.FromUnixTimeMilliseconds(evt.Credential.KeyExpiresAtUnixMs))
            : null;
        next.TeamAutomationLifecycleStatus = TeamAutomationLifecycleStatusState.Active;
        next.NyxidRevocationStatus = evt.ReplacedCredential == null
            ? TeamAutomationEffectTrackStatusState.NotRequired
            : TeamAutomationEffectTrackStatusState.Pending;
        next.VaultRevocationStatus = evt.ReplacedCredential == null
            ? TeamAutomationEffectTrackStatusState.NotRequired
            : TeamAutomationEffectTrackStatusState.Pending;
        next.LastAuthorizationErrorCode = string.Empty;
        ClearTeamAutomationEffectAttempt(next);
        next.UpdatedAt = evt.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private static ScheduledDispatchState ApplyTeamAutomationCredentialOperationFailed(
        ScheduledDispatchState current,
        TeamAutomationCredentialOperationFailedEvent evt)
    {
        var next = current.Clone();
        next.TeamAutomationOwner = evt.Owner?.Clone();
        var candidate = next.CandidateTeamCredential?.Clone();
        var candidateOwner = next.CandidateTeamCredentialOwner?.Clone();
        next.CandidateTeamCredential = null;
        next.CandidateTeamCredentialOwner = null;
        next.TeamAutomationActivationDecision = null;
        if (candidate != null)
        {
            next.PendingRevocationTeamCredential = candidate;
            next.PendingRevocationTeamCredentialOwner = candidateOwner;
            next.NyxidRevocationStatus = TeamAutomationEffectTrackStatusState.Pending;
            next.VaultRevocationStatus = TeamAutomationEffectTrackStatusState.Pending;
        }
        ClearTeamAutomationEffectAttempt(next);
        next.TeamAutomationLifecycleStatus = candidate != null
            ? TeamAutomationLifecycleStatusState.RevocationPending
            : evt.ActiveCredentialPreserved
                ? TeamAutomationLifecycleStatusState.Active
                : TeamAutomationLifecycleStatusState.Failed;
        next.LastAuthorizationErrorCode = evt.ErrorCode ?? string.Empty;
        next.UpdatedAt = evt.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private static ScheduledDispatchState ApplyTeamAutomationDeletionRequested(
        ScheduledDispatchState current,
        TeamAutomationDeletionRequestedEvent evt)
    {
        var next = current.Clone();
        next.TeamAutomationOwner = evt.Owner?.Clone();
        next.TeamAutomationOperationKind = TeamAutomationOperationKindState.Delete;
        next.TeamAutomationOperationId = evt.OperationId ?? string.Empty;
        next.TeamAutomationIdempotencyKey = evt.IdempotencyKey ?? string.Empty;
        if (evt.HasReason)
            next.TeamAutomationDeleteReason = evt.Reason;
        else
            next.ClearTeamAutomationDeleteReason();
        next.TeamAutomationLifecycleStatus = TeamAutomationLifecycleStatusState.Deleting;
        next.CandidateTeamCredential = null;
        next.CandidateTeamCredentialOwner = null;
        next.TeamAutomationActivationDecision = null;
        next.PendingRevocationTeamCredential = evt.PendingRevocationCredential?.Clone();
        next.PendingRevocationTeamCredentialOwner = evt.PendingRevocationCredentialOwner?.Clone();
        ClearTeamAutomationEffectAttempt(next);
        next.NyxidRevocationStatus = evt.PendingRevocationCredential == null
            ? TeamAutomationEffectTrackStatusState.NotRequired
            : TeamAutomationEffectTrackStatusState.Pending;
        next.VaultRevocationStatus = evt.PendingRevocationCredential == null
            ? TeamAutomationEffectTrackStatusState.NotRequired
            : TeamAutomationEffectTrackStatusState.Pending;
        next.LastAuthorizationErrorCode = string.Empty;
        next.UpdatedAt = evt.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private static ScheduledDispatchState ApplyTeamAutomationRevocationCompleted(
        ScheduledDispatchState current,
        TeamAutomationRevocationCompletedEvent evt)
    {
        var next = current.Clone();
        next.NyxidRevocationStatus = evt.NyxidRevoked
            ? TeamAutomationEffectTrackStatusState.Completed
            : TeamAutomationEffectTrackStatusState.Failed;
        next.VaultRevocationStatus = evt.VaultRevoked
            ? TeamAutomationEffectTrackStatusState.Completed
            : TeamAutomationEffectTrackStatusState.Failed;
        next.LastAuthorizationErrorCode = evt.ErrorCode ?? string.Empty;
        if (evt.NyxidRevoked && evt.VaultRevoked)
        {
            next.PendingRevocationTeamCredential = null;
            next.PendingRevocationTeamCredentialOwner = null;
            if (next.TeamAutomationOperationKind == TeamAutomationOperationKindState.Delete)
            {
                next.ActiveTeamCredential = null;
                next.ActiveTeamCredentialOwner = null;
                next.ActiveTeamAuthorizationFact = null;
                next.TeamAutomationLifecycleStatus = TeamAutomationLifecycleStatusState.Deleting;
            }
            else
            {
                next.TeamAutomationLifecycleStatus = next.ActiveTeamCredential != null
                    ? TeamAutomationLifecycleStatusState.Active
                    : TeamAutomationLifecycleStatusState.Failed;
            }
        }
        else
        {
            next.TeamAutomationLifecycleStatus = TeamAutomationLifecycleStatusState.RevocationPending;
        }
        ClearTeamAutomationEffectAttempt(next);
        next.UpdatedAt = evt.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private static ScheduledDispatchState ApplyTeamAutomationOperationObserved(
        ScheduledDispatchState current,
        TeamAutomationOperationObservedEvent evt)
    {
        var next = current.Clone();
        if (evt.OwnsEffectAttempt &&
            evt.ObservationStatus is TeamAutomationOperationObservationStatusState.Unspecified or
                TeamAutomationOperationObservationStatusState.Committed)
        {
            next.TeamAutomationEffectAttemptClaimed = true;
            next.TeamAutomationEffectAttemptId = evt.EffectAttemptId ?? string.Empty;
            next.TeamAutomationEffectAttemptGeneration = evt.EffectAttemptGeneration;
            next.TeamAutomationEffectAttemptClaimedAt = evt.ObservedAtUtc?.Clone();
            next.TeamAutomationEffectAttemptExpiresAt = evt.EffectAttemptExpiresAt?.Clone();
        }
        return next;
    }

    private static ScheduledDispatchState ApplyTeamAutomationCredentialExpiryIntentRecorded(
        ScheduledDispatchState current,
        TeamAutomationCredentialExpiryIntentRecordedEvent evt)
    {
        var next = current.Clone();
        if (evt.CredentialGeneration != next.TeamCredentialGeneration ||
            evt.ExpiresAt == null ||
            next.TeamCredentialExpiresAt == null ||
            evt.ExpiresAt.ToDateTimeOffset() != next.TeamCredentialExpiresAt.ToDateTimeOffset())
        {
            return next;
        }

        next.PendingTeamCredentialExpiryGeneration = evt.CredentialGeneration;
        next.PendingTeamCredentialExpiryAt = evt.ExpiresAt.Clone();
        return next;
    }

    private static ScheduledDispatchState ApplyTeamAutomationCredentialExpiryScheduled(
        ScheduledDispatchState current,
        TeamAutomationCredentialExpiryScheduledEvent evt)
    {
        var next = current.Clone();
        if (evt.CredentialGeneration != next.TeamCredentialGeneration ||
            evt.ExpiresAt == null ||
            next.TeamCredentialExpiresAt == null ||
            evt.ExpiresAt.ToDateTimeOffset() != next.TeamCredentialExpiresAt.ToDateTimeOffset())
        {
            return next;
        }

        next.TeamCredentialExpiryLease = evt.Lease?.Clone();
        next.PendingTeamCredentialExpiryAt = null;
        next.PendingTeamCredentialExpiryGeneration = 0;
        return next;
    }

    private static void ClearTeamAutomationEffectAttempt(ScheduledDispatchState state)
    {
        state.TeamAutomationEffectAttemptClaimed = false;
        state.TeamAutomationEffectAttemptId = string.Empty;
        state.TeamAutomationEffectAttemptClaimedAt = null;
        state.TeamAutomationEffectAttemptExpiresAt = null;
    }

    private ScheduledDispatchState ApplyTeamAutomationAuthorizationRequired(
        ScheduledDispatchState current,
        TeamAutomationAuthorizationRequiredEvent evt)
    {
        var next = current.Clone();
        next.TeamAutomationLifecycleStatus = TeamAutomationLifecycleStatusState.NeedsAuthorization;
        next.LastAuthorizationErrorCode = evt.ErrorCode ?? string.Empty;
        next.NextFireAt = null;
        next.PendingNextFireAt = null;
        next.NextFireLease = null;
        next.TeamCredentialExpiryLease = null;
        next.PendingTeamCredentialExpiryAt = null;
        next.PendingTeamCredentialExpiryGeneration = 0;
        next.UpdatedAt = evt.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        if (evt.ScheduledFireAt != null && !string.IsNullOrWhiteSpace(evt.IdempotencyKey))
        {
            next = ApplyFireFailed(next, new ScheduledDispatchFireFailedEvent
            {
                ScheduledFireAt = evt.ScheduledFireAt.Clone(),
                FailedAt = evt.OccurredAt?.Clone(),
                IdempotencyKey = evt.IdempotencyKey,
                Error = evt.ErrorCode ?? string.Empty,
                ErrorCode = evt.ErrorCode ?? string.Empty,
                Manual = evt.Manual,
            });
        }
        return next;
    }

    private static ScheduledDispatchState ApplyCompleted(
        ScheduledDispatchState current,
        ScheduledDispatchCompletedEvent evt)
    {
        var next = current.Clone();
        var completedAt = evt.CompletedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        next.Completed = true;
        next.CompletedAt = completedAt;
        next.Enabled = false;
        next.NextFireAt = null;
        next.NextFireLease = null;
        next.PendingNextFireAt = null;
        next.PendingNextFireRequestedAt = null;
        next.UpdatedAt = completedAt;
        return next;
    }

    private static ScheduledDispatchState ApplyNextFireIntentRecorded(
        ScheduledDispatchState current,
        ScheduledDispatchNextFireIntentRecordedEvent evt)
    {
        var next = current.Clone();
        next.PendingNextFireAt = evt.NextFireAt?.Clone();
        next.PendingNextFireRequestedAt = evt.RequestedAt?.Clone();
        next.UpdatedAt =
            evt.RequestedAt?.ToDateTimeOffset() ??
            evt.NextFireAt?.ToDateTimeOffset() ??
            DateTimeOffset.UtcNow;
        return next;
    }

    private static ScheduledDispatchState ApplyNextFireScheduled(
        ScheduledDispatchState current,
        ScheduledDispatchNextFireScheduledEvent evt)
    {
        var next = current.Clone();
        next.NextFireAt = evt.NextFireAt?.ToDateTimeOffset();
        next.NextFireLease = evt.Lease?.Clone();
        next.PendingNextFireAt = null;
        next.PendingNextFireRequestedAt = null;
        next.UpdatedAt =
            evt.ScheduledAt?.ToDateTimeOffset() ??
            evt.NextFireAt?.ToDateTimeOffset() ??
            DateTimeOffset.UtcNow;
        return next;
    }

    private ScheduledDispatchState ApplyFireStarted(
        ScheduledDispatchState current,
        ScheduledDispatchFireStartedEvent evt)
    {
        var next = current.Clone();
        next.LastFireAt = evt.ScheduledFireAt?.ToDateTimeOffset();
        next.LastError = string.Empty;
        next.LastErrorCode = string.Empty;
        next.UpdatedAt = evt.StartedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        UpsertFireRecord(next, evt.IdempotencyKey, new ScheduledDispatchFireRecordState
        {
            ScheduledFireAt = evt.ScheduledFireAt?.Clone(),
            CompletedAt = evt.StartedAt?.Clone(),
            IdempotencyKey = evt.IdempotencyKey ?? string.Empty,
            Manual = evt.Manual,
            Status = ScheduledDispatchFireStatusState.Started,
        });
        return next;
    }

    private ScheduledDispatchState ApplyFireDispatched(
        ScheduledDispatchState current,
        ScheduledDispatchFireDispatchedEvent evt)
    {
        var next = current.Clone();
        next.LastFireAt = evt.ScheduledFireAt?.ToDateTimeOffset();
        next.LastTargetActorId = evt.TargetActorId ?? string.Empty;
        next.LastCommandId = evt.CommandId ?? string.Empty;
        next.LastCorrelationId = evt.CorrelationId ?? string.Empty;
        next.LastError = string.Empty;
        next.LastErrorCode = string.Empty;
        next.FireCount++;
        next.UpdatedAt = evt.DispatchedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        UpsertFireRecord(next, evt.IdempotencyKey, new ScheduledDispatchFireRecordState
        {
            ScheduledFireAt = evt.ScheduledFireAt?.Clone(),
            CompletedAt = evt.DispatchedAt?.Clone(),
            IdempotencyKey = evt.IdempotencyKey ?? string.Empty,
            TargetActorId = evt.TargetActorId ?? string.Empty,
            CommandId = evt.CommandId ?? string.Empty,
            CorrelationId = evt.CorrelationId ?? string.Empty,
            Manual = evt.Manual,
            Status = ScheduledDispatchFireStatusState.Dispatched,
        });
        return next;
    }

    private ScheduledDispatchState ApplyFireFailed(
        ScheduledDispatchState current,
        ScheduledDispatchFireFailedEvent evt)
    {
        var next = current.Clone();
        next.LastFireAt = evt.ScheduledFireAt?.ToDateTimeOffset();
        next.LastError = evt.Error ?? string.Empty;
        next.LastErrorCode = evt.ErrorCode ?? string.Empty;
        next.FireCount++;
        next.FailureCount++;
        next.UpdatedAt = evt.FailedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        UpsertFireRecord(next, evt.IdempotencyKey, new ScheduledDispatchFireRecordState
        {
            ScheduledFireAt = evt.ScheduledFireAt?.Clone(),
            CompletedAt = evt.FailedAt?.Clone(),
            IdempotencyKey = evt.IdempotencyKey ?? string.Empty,
            Error = evt.Error ?? string.Empty,
            ErrorCode = evt.ErrorCode ?? string.Empty,
            Manual = evt.Manual,
            Status = ScheduledDispatchFireStatusState.Failed,
        });
        return next;
    }

    private static ScheduledDispatchState ApplyFireOverdueDetected(
        ScheduledDispatchState current,
        ScheduledDispatchFireOverdueDetectedEvent evt)
    {
        var next = current.Clone();
        next.OverdueFireDetectedCount++;
        next.LastOverdueFireAt = evt.ScheduledFireAt?.ToDateTimeOffset();
        next.UpdatedAt =
            evt.DetectedAt?.ToDateTimeOffset() ??
            evt.ScheduledFireAt?.ToDateTimeOffset() ??
            DateTimeOffset.UtcNow;
        return next;
    }

    private static void UpsertFireRecord(
        ScheduledDispatchState state,
        string idempotencyKey,
        ScheduledDispatchFireRecordState record)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return;

        state.FireRecords[idempotencyKey] = record;
        if (state.FireRecords.Count <= MaxFireRecordCount)
            return;

        var keysToRemove = state.FireRecords
            .OrderBy(static x => ResolveTimestampSeconds(x.Value.CompletedAt))
            .ThenBy(static x => ResolveTimestampNanos(x.Value.CompletedAt))
            .ThenBy(static x => x.Key, StringComparer.Ordinal)
            .Take(state.FireRecords.Count - MaxFireRecordCount)
            .Select(static x => x.Key)
            .ToArray();
        foreach (var key in keysToRemove)
            state.FireRecords.Remove(key);
    }

    private static long ResolveTimestampSeconds(Timestamp? timestamp) =>
        timestamp?.Seconds ?? 0;

    private static int ResolveTimestampNanos(Timestamp? timestamp) =>
        timestamp?.Nanos ?? 0;

    private static ScheduledDispatchScheduleModeState NormalizeScheduleMode(ScheduledDispatchScheduleModeState mode) =>
        mode == ScheduledDispatchScheduleModeState.OneShotAtUtc
            ? ScheduledDispatchScheduleModeState.OneShotAtUtc
            : ScheduledDispatchScheduleModeState.RecurringCron;

    private static string NormalizeCronExpression(
        ScheduledDispatchScheduleModeState mode,
        string? cronExpression) =>
        mode == ScheduledDispatchScheduleModeState.OneShotAtUtc
            ? string.Empty
            : NormalizeRequired(cronExpression, nameof(cronExpression));

    private static DateTimeOffset? NormalizeOneShotFireAt(
        ScheduledDispatchScheduleModeState mode,
        Timestamp? oneShotFireAt) =>
        mode == ScheduledDispatchScheduleModeState.OneShotAtUtc
            ? oneShotFireAt?.ToDateTimeOffset().ToUniversalTime()
            : null;

    private static string NormalizeRequired(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{parameterName} is required.", parameterName);

        return value.Trim();
    }

    private static string NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private string ResolveScheduleScopeId()
    {
        var ownerScopeId = NormalizeOptional(State.TeamAutomationOwner?.ScopeId);
        if (ownerScopeId.Length > 0)
            return ownerScopeId;

        if (State.Headers.TryGetValue("scope_id", out var snakeScopeId) &&
            !string.IsNullOrWhiteSpace(snakeScopeId))
        {
            return snakeScopeId.Trim();
        }

        return State.Headers.TryGetValue("scopeId", out var camelScopeId) &&
               !string.IsNullOrWhiteSpace(camelScopeId)
            ? camelScopeId.Trim()
            : string.Empty;
    }

    private static IReadOnlyDictionary<string, string> NormalizeHeaders(
        IEnumerable<KeyValuePair<string, string>>? source)
    {
        if (source == null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            var normalizedKey = NormalizeOptional(key);
            var normalizedValue = NormalizeOptional(value);
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;
            if (ScheduledServiceInvocationPayloadPolicy.IsConnectorHttpAuthorizationKey(normalizedKey))
            {
                continue;
            }

            normalized[normalizedKey] = normalizedValue;
        }

        return normalized;
    }

    private static bool ShouldInspectRawCredentialSignalHeaders(
        ScheduledDispatchCredentialRequirementTargetKindState targetKind) =>
        targetKind is ScheduledDispatchCredentialRequirementTargetKindState.WorkflowService
            or ScheduledDispatchCredentialRequirementTargetKindState.Connector;

    private static IReadOnlyDictionary<string, string> NormalizeCredentialSignalHeaders(
        IEnumerable<KeyValuePair<string, string>>? source)
    {
        if (source == null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            var normalizedKey = NormalizeOptional(key);
            var normalizedValue = NormalizeOptional(value);
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;

            normalized[normalizedKey] = normalizedValue;
        }

        return normalized;
    }

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) ||
                !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EnvelopePayloadEquals(EventEnvelope? left, EventEnvelope? right)
    {
        if (left?.Payload == null || right?.Payload == null)
            return left?.Payload == null && right?.Payload == null;

        return string.Equals(left.Payload.TypeUrl, right.Payload.TypeUrl, StringComparison.Ordinal) &&
               left.Payload.Value.Equals(right.Payload.Value);
    }

    private static bool TargetEquals(ScheduledDispatchTargetState? left, ScheduledDispatchTargetState? right) =>
        Equals(left, right);

    private static string ResolvePayloadTypeUrl(EventEnvelope? envelope) =>
        envelope?.Payload?.TypeUrl ?? string.Empty;

}
