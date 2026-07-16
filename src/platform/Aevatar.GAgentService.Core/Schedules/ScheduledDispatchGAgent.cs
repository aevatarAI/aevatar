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
    private const int MaxFireRecordCount = 128;
    // How overdue an armed occurrence must be, when the actor reactivates, before it counts as
    // a detected miss. Wide enough that routine reactivation catch-up (pod churn at the boundary
    // is seconds-to-minutes late) is not flagged, tight enough that genuine drops (production
    // misses run 90+ minutes) always are.
    private static readonly TimeSpan OverdueFireGracePeriod = TimeSpan.FromMinutes(10);
    private const string LegacyDurableSenderBearerBlockedError =
        "Scheduled service invocation contains legacy durable bearer auth; reconfigure the schedule with senderNyxId or scopeOwnerNyxId.";
    private static readonly TimeSpan MaxNextFireCallbackHop = TimeSpan.FromDays(7);
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IScheduledServiceInvocationDispatchPort _serviceInvocationDispatchPort;
    private readonly IScheduledDispatchCredentialRequirementPolicy _credentialRequirementPolicy;

    public ScheduledDispatchGAgent(
        IActorDispatchPort dispatchPort,
        IScheduledServiceInvocationDispatchPort serviceInvocationDispatchPort,
        IScheduledDispatchCredentialRequirementPolicy credentialRequirementPolicy)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _serviceInvocationDispatchPort = serviceInvocationDispatchPort
            ?? throw new ArgumentNullException(nameof(serviceInvocationDispatchPort));
        _credentialRequirementPolicy = credentialRequirementPolicy
            ?? throw new ArgumentNullException(nameof(credentialRequirementPolicy));
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        if (State.Enabled && !State.Completed && IsConfigured())
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
            .On<TeamAutomationCredentialActivatedEvent>(ApplyTeamAutomationCredentialActivated)
            .On<TeamAutomationCredentialOperationFailedEvent>(ApplyTeamAutomationCredentialOperationFailed)
            .On<TeamAutomationDeletionRequestedEvent>(ApplyTeamAutomationDeletionRequested)
            .On<TeamAutomationRevocationCompletedEvent>(ApplyTeamAutomationRevocationCompleted)
            .On<TeamAutomationAuthorizationRequiredEvent>(ApplyTeamAutomationAuthorizationRequired)
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
        bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (State.Deleted)
            throw new InvalidOperationException($"Scheduled dispatch '{ResolveScheduleId()}' is deleted.");
        if (isCreate && IsConfigured())
            throw new InvalidOperationException($"Scheduled dispatch '{ResolveScheduleId()}' already exists.");
        if (!isCreate && !IsConfigured())
            throw new InvalidOperationException($"Scheduled dispatch '{ResolveScheduleId()}' is not configured.");
        EnsureTeamAutomationOwnerAccess(teamAutomationOwner, "configure");
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
        EnsureTeamAutomationOwnerAccess(command.TeamAutomationOwner, "enable");
        if (State.TeamAutomationOwner != null &&
            State.TeamAutomationLifecycleStatus != TeamAutomationLifecycleStatusState.Active)
        {
            throw new InvalidOperationException("team_automation_credential_not_active");
        }

        await PersistDomainEventAsync(new ScheduledDispatchEnabledEvent
        {
            Reason = NormalizeOptional(command.Reason),
            EnabledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await EnsureNextFireScheduledAsync(DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleDisableAsync(ScheduledDispatchDisableCommand command)
    {
        EnsureConfiguredForWrite("disable");
        EnsureTeamAutomationOwnerAccess(command.TeamAutomationOwner, "disable");
        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        await PersistDomainEventAsync(new ScheduledDispatchDisabledEvent
        {
            Reason = NormalizeOptional(command.Reason),
            DisabledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await CancelNextFireLeaseAsync(previousLease, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleDeleteAsync(ScheduledDispatchDeleteCommand command)
    {
        if (State.Deleted && IsSameCompletedDeleteOperation(command))
        {
            EnsureCredentialAuthorizationOwnerAccess(
                command.AuthenticatedCredentialOwner,
                State.PendingRevocationTeamCredentialOwner);
            await PersistTeamAutomationObservationAsync(
                TeamAutomationOperationObservationStages.Delete,
                State.PendingRevocationTeamCredential != null && !State.TeamAutomationEffectAttemptClaimed,
                CancellationToken.None);
            return;
        }

        EnsureConfiguredForWrite("delete");
        EnsureTeamAutomationOwnerAccess(command.TeamAutomationOwner, "delete");
        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        if (State.TeamAutomationOwner != null)
        {
            EnsureCredentialAuthorizationOwnerAccess(
                command.AuthenticatedCredentialOwner,
                State.ActiveTeamCredentialOwner);
            await PersistDomainEventAsync(new TeamAutomationDeletionRequestedEvent
            {
                Owner = State.TeamAutomationOwner.Clone(),
                OperationId = NormalizeRequired(command.OperationId, nameof(command.OperationId)),
                IdempotencyKey = NormalizeRequired(command.IdempotencyKey, nameof(command.IdempotencyKey)),
                PendingRevocationCredential = State.ActiveTeamCredential?.Clone(),
                PendingRevocationCredentialOwner = State.ActiveTeamCredentialOwner?.Clone(),
                OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });
        }
        await PersistDomainEventAsync(new ScheduledDispatchDeletedEvent
        {
            Reason = NormalizeOptional(command.Reason),
            DeletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await PersistTeamAutomationObservationAsync(
            TeamAutomationOperationObservationStages.Delete,
            State.PendingRevocationTeamCredential != null,
            CancellationToken.None);
        await CancelNextFireLeaseAsync(previousLease, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleBeginTeamAutomationCredentialOperationAsync(
        BeginTeamAutomationCredentialOperationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var scheduleId = NormalizeRequired(command.ScheduleId, nameof(command.ScheduleId));
        var owner = NormalizeTeamAutomationOwner(command.Owner);
        var operationId = NormalizeRequired(command.OperationId, nameof(command.OperationId));
        var idempotencyKey = NormalizeRequired(command.IdempotencyKey, nameof(command.IdempotencyKey));
        var permissionDigest = NormalizeRequired(command.PermissionDigest, nameof(command.PermissionDigest));
        var policyVersion = NormalizeRequired(command.PolicyVersion, nameof(command.PolicyVersion));
        if (command.OperationKind is not (TeamAutomationOperationKindState.Create or
            TeamAutomationOperationKindState.Reauthorize))
        {
            throw new ArgumentException("Team automation credential operation kind is invalid.", nameof(command));
        }

        if (State.Deleted)
            throw new InvalidOperationException($"Scheduled dispatch '{ResolveScheduleId()}' is deleted.");
        if (IsConfigured() && State.TeamAutomationOwner == null)
            throw new InvalidOperationException("team_automation_owner_conflict");
        if (!string.IsNullOrWhiteSpace(State.ScheduleId) &&
            !string.Equals(State.ScheduleId, scheduleId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("team_automation_schedule_id_conflict");
        }

        EnsureStableTeamAutomationOwner(owner);
        if (IsExactTeamAutomationOperation(
                owner,
                operationId,
                idempotencyKey,
                permissionDigest,
                policyVersion,
                command.OperationKind))
        {
            await PersistTeamAutomationObservationAsync(
                TeamAutomationOperationObservationStages.Begin,
                ownsEffectAttempt: false,
                CancellationToken.None);
            return;
        }

        if (string.Equals(State.TeamAutomationOperationId, operationId, StringComparison.Ordinal) ||
            string.Equals(State.TeamAutomationIdempotencyKey, idempotencyKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("team_automation_operation_conflict");
        }

        if (State.TeamAutomationLifecycleStatus is TeamAutomationLifecycleStatusState.ProvisioningPending or
            TeamAutomationLifecycleStatusState.ReplacementPending or
            TeamAutomationLifecycleStatusState.Deleting or
            TeamAutomationLifecycleStatusState.RevocationPending)
        {
            throw new InvalidOperationException("team_automation_operation_in_progress");
        }
        if (State.PendingRevocationTeamCredential != null)
            throw new InvalidOperationException("team_automation_revocation_in_progress");

        if (command.OperationKind == TeamAutomationOperationKindState.Create && State.ActiveTeamCredential != null)
            throw new InvalidOperationException("team_automation_credential_already_active");
        if (command.OperationKind == TeamAutomationOperationKindState.Reauthorize && State.ActiveTeamCredential == null)
            throw new InvalidOperationException("team_automation_credential_not_active");

        await PersistDomainEventAsync(new TeamAutomationCredentialOperationBeganEvent
        {
            Owner = owner,
            OperationId = operationId,
            IdempotencyKey = idempotencyKey,
            PermissionDigest = permissionDigest,
            PolicyVersion = policyVersion,
            OperationKind = command.OperationKind,
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await PersistTeamAutomationObservationAsync(
            TeamAutomationOperationObservationStages.Begin,
            ownsEffectAttempt: true,
            CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleCompleteTeamAutomationCredentialOperationAsync(
        CompleteTeamAutomationCredentialOperationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var owner = NormalizeTeamAutomationOwner(command.Owner);
        EnsureTeamAutomationOwnerAccess(owner, "complete credential operation");
        EnsureCurrentTeamAutomationOperation(command.OperationId, command.IdempotencyKey);
        if (State.TeamAutomationLifecycleStatus == TeamAutomationLifecycleStatusState.Active &&
            CredentialEquals(State.ActiveTeamCredential, command.Credential))
        {
            await PersistTeamAutomationObservationAsync(
                TeamAutomationOperationObservationStages.Complete,
                ownsEffectAttempt: false,
                CancellationToken.None);
            return;
        }
        if (State.TeamAutomationLifecycleStatus is not (TeamAutomationLifecycleStatusState.ProvisioningPending or
            TeamAutomationLifecycleStatusState.ReplacementPending))
        {
            throw new InvalidOperationException("team_automation_operation_not_pending");
        }

        var credential = NormalizeTeamCredential(command.Credential);
        var configuration = NormalizeTeamAutomationActivationConfiguration(command.Configuration, owner, credential);
        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        await PersistDomainEventAsync(new TeamAutomationCredentialActivatedEvent
        {
            Owner = owner,
            OperationId = State.TeamAutomationOperationId,
            IdempotencyKey = State.TeamAutomationIdempotencyKey,
            Credential = credential,
            ReplacedCredential = State.ActiveTeamCredential?.Clone(),
            CredentialOwner = configuration.Target?.ServiceInvocation?.AuthorizationFact?.Owner?.Clone(),
            ReplacedCredentialOwner = State.ActiveTeamCredentialOwner?.Clone(),
            Generation = checked(State.TeamCredentialGeneration + 1),
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Configuration = configuration,
        });
        await PersistTeamAutomationObservationAsync(
            TeamAutomationOperationObservationStages.Complete,
            State.PendingRevocationTeamCredential != null,
            CancellationToken.None);
        if (State.Enabled)
            await EnsureNextFireScheduledAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        else
            await CancelNextFireLeaseAsync(previousLease, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleFailTeamAutomationCredentialOperationAsync(
        FailTeamAutomationCredentialOperationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var owner = NormalizeTeamAutomationOwner(command.Owner);
        EnsureTeamAutomationOwnerAccess(owner, "fail credential operation");
        EnsureCurrentTeamAutomationOperation(command.OperationId, command.IdempotencyKey);
        var errorCode = NormalizeStableErrorCode(command.ErrorCode);
        if (State.TeamAutomationLifecycleStatus is TeamAutomationLifecycleStatusState.Failed or
            TeamAutomationLifecycleStatusState.Active &&
            string.Equals(State.LastAuthorizationErrorCode, errorCode, StringComparison.Ordinal))
        {
            await PersistTeamAutomationObservationAsync(
                TeamAutomationOperationObservationStages.Fail,
                ownsEffectAttempt: false,
                CancellationToken.None,
                errorCode);
            return;
        }

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
            ownsEffectAttempt: false,
            CancellationToken.None,
            errorCode);
    }

    [EventHandler]
    public async Task HandleCompleteTeamAutomationRevocationAsync(
        CompleteTeamAutomationRevocationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var owner = NormalizeTeamAutomationOwner(command.Owner);
        EnsureTeamAutomationOwnerAccess(owner, "complete revocation");
        if (!string.Equals(State.TeamAutomationOperationId, NormalizeRequired(command.OperationId, nameof(command.OperationId)),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("team_automation_operation_conflict");
        }

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
            State.LastAuthorizationErrorCode);
    }

    [EventHandler]
    public async Task HandleRetryTeamAutomationRevocationAsync(
        RetryTeamAutomationRevocationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var owner = NormalizeTeamAutomationOwner(command.Owner);
        EnsureTeamAutomationOwnerAccess(owner, "retry revocation");
        EnsureCurrentTeamAutomationOperation(command.OperationId, command.IdempotencyKey);
        EnsureCredentialAuthorizationOwnerAccess(
            command.AuthenticatedCredentialOwner,
            State.PendingRevocationTeamCredentialOwner);
        if (State.PendingRevocationTeamCredential == null)
            throw new InvalidOperationException("team_automation_revocation_not_pending");
        await PersistTeamAutomationObservationAsync(
            TeamAutomationOperationObservationStages.Delete,
            ownsEffectAttempt: !State.TeamAutomationEffectAttemptClaimed,
            CancellationToken.None);
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
            EnsureTeamAutomationOwnerAccess(command.TeamAutomationOwner, "manual fire");
        if (State.TeamAutomationOwner != null && !HasUsableActiveTeamCredential(DateTimeOffset.UtcNow))
        {
            if (State.ActiveTeamCredential != null &&
                State.TeamCredentialExpiresAt?.ToDateTimeOffset() <= DateTimeOffset.UtcNow &&
                State.TeamAutomationLifecycleStatus != TeamAutomationLifecycleStatusState.NeedsAuthorization)
            {
                await PersistDomainEventAsync(new TeamAutomationAuthorizationRequiredEvent
                {
                    Owner = State.TeamAutomationOwner.Clone(),
                    ErrorCode = "credential_expired",
                    OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                }, ct);
            }
            if (command.Manual)
                throw new InvalidOperationException("team_automation_credential_not_active");

            Logger.LogWarning(
                "Scheduled dispatch {ActorId} skipped automatic fire because Team credential status is {LifecycleStatus}.",
                Id,
                State.TeamAutomationLifecycleStatus);
            await EnsureNextFireScheduledAsync(ResolveScheduledFireAt(command), ct);
            return;
        }
        if (!command.Manual && !State.Enabled)
        {
            Logger.LogInformation("Scheduled dispatch {ActorId} ignored fire because it is disabled.", Id);
            return;
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
            var receipt = await DispatchPreparedTargetAsync(prepared, ct);
            if (!receipt.Accepted)
            {
                await PersistFireFailedAsync(
                    scheduledFireAt,
                    idempotencyKey,
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
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Scheduled dispatch {ActorId} dispatch failed.", Id);
            await PersistFireFailedAsync(scheduledFireAt, idempotencyKey, ex.Message, command.Manual, CancellationToken.None);
        }

        if (!command.Manual)
        {
            if (IsOneShot())
                await CompleteOneShotAsync(CancellationToken.None);
            else
                await EnsureNextFireScheduledAsync(scheduledFireAt, CancellationToken.None);
        }
    }

    private async Task<ScheduledDispatchReceipt> DispatchPreparedTargetAsync(
        ScheduledDispatchEnvelope prepared,
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
                    AuthorizationFact: ToRuntimeAuthorizationFact(effectiveAuthorizationFact)),
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
                auth.Durable.SecretReference?.Clone() ?? new SecretReference()));
        }

        if (auth.SourceCase == ScheduledServiceInvocationAuthState.SourceOneofCase.ScheduledInvocationAgentKey)
        {
            return new ScheduledServiceInvocationAuth(new ScheduledInvocationAgentKeyCredentialReference(
                auth.ScheduledInvocationAgentKey.SecretReference?.Clone() ?? new SecretReference(),
                auth.ScheduledInvocationAgentKey.ApiKeyId ?? string.Empty,
                auth.ScheduledInvocationAgentKey.KeyExpiresAtUnixMs));
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
            ToRuntimeRole(nyxId.Role)));
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
        if (!State.Enabled || State.Completed)
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

    private bool MatchesNextFireLease(EventEnvelope? envelope)
    {
        if (envelope == null)
            return false;

        var lease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
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

    private void EnsureTeamAutomationOwnerAccess(TeamMemberAutomationOwnerState? supplied, string operation)
    {
        if (State.TeamAutomationOwner == null)
        {
            if (supplied != null)
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
            throw new InvalidOperationException("team_automation_owner_conflict");
        }
    }

    private bool IsExactTeamAutomationOperation(
        TeamMemberAutomationOwnerState owner,
        string operationId,
        string idempotencyKey,
        string permissionDigest,
        string policyVersion,
        TeamAutomationOperationKindState operationKind) =>
        TeamAutomationOwnerEquals(State.TeamAutomationOwner, owner) &&
        string.Equals(State.TeamAutomationOperationId, operationId, StringComparison.Ordinal) &&
        string.Equals(State.TeamAutomationIdempotencyKey, idempotencyKey, StringComparison.Ordinal) &&
        string.Equals(State.TeamAutomationPermissionDigest, permissionDigest, StringComparison.Ordinal) &&
        string.Equals(State.TeamAutomationPolicyVersion, policyVersion, StringComparison.Ordinal) &&
        State.TeamAutomationOperationKind == operationKind;

    private void EnsureCurrentTeamAutomationOperation(string? operationId, string? idempotencyKey)
    {
        if (!string.Equals(State.TeamAutomationOperationId,
                NormalizeRequired(operationId, nameof(operationId)), StringComparison.Ordinal) ||
            !string.Equals(State.TeamAutomationIdempotencyKey,
                NormalizeRequired(idempotencyKey, nameof(idempotencyKey)), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("team_automation_operation_conflict");
        }
    }

    private bool IsSameCompletedDeleteOperation(ScheduledDispatchDeleteCommand command) =>
        State.TeamAutomationOwner != null &&
        command.TeamAutomationOwner != null &&
        TeamAutomationOwnerEquals(State.TeamAutomationOwner, command.TeamAutomationOwner) &&
        string.Equals(State.TeamAutomationOperationId, command.OperationId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(State.TeamAutomationIdempotencyKey, command.IdempotencyKey?.Trim(), StringComparison.Ordinal);

    private static TeamMemberAutomationOwnerState NormalizeTeamAutomationOwner(TeamMemberAutomationOwnerState? owner)
    {
        if (owner == null)
            throw new InvalidOperationException("team_automation_owner_required");

        return new TeamMemberAutomationOwnerState
        {
            ScopeId = NormalizeRequired(owner.ScopeId, nameof(owner.ScopeId)),
            MemberId = NormalizeRequired(owner.MemberId, nameof(owner.MemberId)),
        };
    }

    private static bool TeamAutomationOwnerEquals(
        TeamMemberAutomationOwnerState? left,
        TeamMemberAutomationOwnerState? right)
    {
        if (left == null || right == null)
            return left == null && right == null;

        return string.Equals(left.ScopeId, right.ScopeId, StringComparison.Ordinal) &&
               string.Equals(left.MemberId, right.MemberId, StringComparison.Ordinal);
    }

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

    private static ScheduledInvocationAgentKeyCredentialReferenceState NormalizeTeamCredential(
        ScheduledInvocationAgentKeyCredentialReferenceState? credential)
    {
        if (credential?.SecretReference == null ||
            string.IsNullOrWhiteSpace(credential.SecretReference.Ref) ||
            string.IsNullOrWhiteSpace(credential.SecretReference.OwnerScopeKey) ||
            string.IsNullOrWhiteSpace(credential.ApiKeyId) ||
            credential.KeyExpiresAtUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            throw new InvalidOperationException("team_automation_credential_invalid_or_expired");
        }

        return NormalizeScheduledInvocationAgentKey(credential);
    }

    private static bool CredentialEquals(
        ScheduledInvocationAgentKeyCredentialReferenceState? left,
        ScheduledInvocationAgentKeyCredentialReferenceState? right) =>
        left != null && right != null &&
        string.Equals(left.ApiKeyId, right.ApiKeyId, StringComparison.Ordinal) &&
        string.Equals(left.SecretReference?.Ref, right.SecretReference?.Ref, StringComparison.Ordinal) &&
        left.KeyExpiresAtUnixMs == right.KeyExpiresAtUnixMs;

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
            throw new InvalidOperationException("team_automation_owner_conflict");
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
            string.IsNullOrWhiteSpace(authorizationFact.Owner.OwnerSubject) ||
            !string.Equals(
                authorizationFact.PermissionDigest,
                State.TeamAutomationPermissionDigest,
                StringComparison.Ordinal) ||
            !string.Equals(
                authorizationFact.PolicyVersion,
                State.TeamAutomationPolicyVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("team_automation_configuration_not_applied");
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
        return configured;
    }

    private async Task PersistTeamAutomationObservationAsync(
        string stage,
        bool ownsEffectAttempt,
        CancellationToken ct,
        string? errorCode = null,
        string? errorMessage = null)
    {
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
            ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            PendingRevocationCredential = State.PendingRevocationTeamCredential?.Clone(),
            PendingRevocationOwner = State.PendingRevocationTeamCredentialOwner?.Clone(),
            NyxidRevocationPending = State.PendingRevocationTeamCredential != null &&
                                     State.NyxidRevocationStatus != TeamAutomationEffectTrackStatusState.Completed,
            VaultRevocationPending = State.PendingRevocationTeamCredential != null &&
                                     State.VaultRevocationStatus != TeamAutomationEffectTrackStatusState.Completed,
        };
        await PersistDomainEventAsync(observed, ct);
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
        _ = NormalizeTarget(target, scheduleKind);
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
                fact.Authority?.CatalogExternalRevision ?? string.Empty,
                fact.Authority?.CatalogContentDigest ?? string.Empty))
        {
            NodeGrants = fact.NodeGrants.Select(static grant =>
                new ScheduledInvocationAuthorizationNodeGrant(
                    grant.UserServiceId,
                    grant.NodeId,
                    grant.DisplayName,
                    grant.Role,
                    grant.EdgeKind,
                    grant.BindingId,
                    grant.RoutePriority)).ToArray(),
        };
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
        if (hasLegacyDurableToken)
        {
            return new ScheduledServiceInvocationAuthState
            {
                LegacyDurableSenderBearerBlocked = true,
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
                };
        }

        if (auth.SourceCase == ScheduledServiceInvocationAuthState.SourceOneofCase.ScheduledInvocationAgentKey)
        {
            return auth.ScheduledInvocationAgentKey == null
                ? null
                : new ScheduledServiceInvocationAuthState
                {
                    ScheduledInvocationAgentKey = NormalizeScheduledInvocationAgentKey(auth.ScheduledInvocationAgentKey),
                };
        }

        var nyxId = ResolveNyxIdSource(auth);
        if (nyxId == null)
            return null;

        var normalized = new ScheduledServiceInvocationAuthState
        {
            NyxId = NormalizeNyxIdSource(nyxId),
        };

        return normalized;
    }

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
        next.TeamAutomationOwner = evt.Owner?.Clone();
        next.TeamAutomationOperationId = evt.OperationId ?? string.Empty;
        next.TeamAutomationIdempotencyKey = evt.IdempotencyKey ?? string.Empty;
        next.TeamAutomationPermissionDigest = evt.PermissionDigest ?? string.Empty;
        next.TeamAutomationPolicyVersion = evt.PolicyVersion ?? string.Empty;
        next.TeamAutomationOperationKind = evt.OperationKind;
        next.TeamAutomationLifecycleStatus = evt.OperationKind == TeamAutomationOperationKindState.Reauthorize
            ? TeamAutomationLifecycleStatusState.ReplacementPending
            : TeamAutomationLifecycleStatusState.ProvisioningPending;
        next.CandidateTeamCredential = null;
        next.TeamAutomationEffectAttemptClaimed = false;
        next.LastAuthorizationErrorCode = string.Empty;
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
        next.TeamAutomationEffectAttemptClaimed = false;
        next.UpdatedAt = evt.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private static ScheduledDispatchState ApplyTeamAutomationCredentialOperationFailed(
        ScheduledDispatchState current,
        TeamAutomationCredentialOperationFailedEvent evt)
    {
        var next = current.Clone();
        next.TeamAutomationOwner = evt.Owner?.Clone();
        next.CandidateTeamCredential = null;
        next.TeamAutomationEffectAttemptClaimed = false;
        next.TeamAutomationLifecycleStatus = evt.ActiveCredentialPreserved
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
        next.TeamAutomationLifecycleStatus = TeamAutomationLifecycleStatusState.Deleting;
        next.PendingRevocationTeamCredential = evt.PendingRevocationCredential?.Clone();
        next.PendingRevocationTeamCredentialOwner = evt.PendingRevocationCredentialOwner?.Clone();
        next.TeamAutomationEffectAttemptClaimed = false;
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
                next.TeamAutomationLifecycleStatus = TeamAutomationLifecycleStatusState.Active;
            }
        }
        else
        {
            next.TeamAutomationLifecycleStatus =
                next.TeamAutomationOperationKind == TeamAutomationOperationKindState.Delete
                    ? TeamAutomationLifecycleStatusState.RevocationPending
                    : TeamAutomationLifecycleStatusState.Active;
        }
        next.TeamAutomationEffectAttemptClaimed = false;
        next.UpdatedAt = evt.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private static ScheduledDispatchState ApplyTeamAutomationOperationObserved(
        ScheduledDispatchState current,
        TeamAutomationOperationObservedEvent evt)
    {
        var next = current.Clone();
        if (evt.OwnsEffectAttempt)
            next.TeamAutomationEffectAttemptClaimed = true;
        return next;
    }

    private static ScheduledDispatchState ApplyTeamAutomationAuthorizationRequired(
        ScheduledDispatchState current,
        TeamAutomationAuthorizationRequiredEvent evt)
    {
        var next = current.Clone();
        next.TeamAutomationLifecycleStatus = TeamAutomationLifecycleStatusState.NeedsAuthorization;
        next.LastAuthorizationErrorCode = evt.ErrorCode ?? string.Empty;
        next.UpdatedAt = evt.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
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
        next.FireCount++;
        next.FailureCount++;
        next.UpdatedAt = evt.FailedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        UpsertFireRecord(next, evt.IdempotencyKey, new ScheduledDispatchFireRecordState
        {
            ScheduledFireAt = evt.ScheduledFireAt?.Clone(),
            CompletedAt = evt.FailedAt?.Clone(),
            IdempotencyKey = evt.IdempotencyKey ?? string.Empty,
            Error = evt.Error ?? string.Empty,
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
            if (string.Equals(
                    normalizedKey,
                    ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey,
                    StringComparison.Ordinal))
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
