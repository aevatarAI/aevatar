using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Runtime.ExceptionServices;

namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Short-lived actor for one StudioMember binding attempt.
/// </summary>
[GAgent("studio.member-binding-run")]
public sealed class StudioMemberBindingRunGAgent : GAgentBase<StudioMemberBindingRunState>, IProjectedActor
{
    private const string MemberDeletedFailureCode = "STUDIO_MEMBER_DELETED";
    private const string PlatformBindingCheckpointUnavailableFailureCode =
        "STUDIO_MEMBER_PLATFORM_BINDING_CHECKPOINT_UNAVAILABLE";
    private const string PlatformBindingReadinessTimeoutFailureCode =
        "STUDIO_MEMBER_PLATFORM_BINDING_READINESS_TIMEOUT";
    private const string PlatformBindingReadinessTimeoutFailureMessage =
        "platform binding readiness was not observed before the actor-owned deadline.";
    private static readonly TimeSpan AdmissionWatchdogDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PlatformBindingExecuteInitialDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PlatformBindingWatchdogDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PlatformBindingExecutionStaleAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PlatformBindingReadinessBudget = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan MemberTerminalNotificationWatchdogDelay = TimeSpan.FromSeconds(30);
    private static readonly Timestamp LegacyPlatformExecutionFenceAtUtc = Timestamp.FromDateTime(
        new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    private readonly IStudioMemberPlatformBindingCommandPort? _platformBindingPort;

    public static string ProjectionKind => "studio-member-binding-run";

    public StudioMemberBindingRunGAgent(IStudioMemberPlatformBindingCommandPort? platformBindingPort = null)
    {
        _platformBindingPort = platformBindingPort;
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);

        if (!CanRecoverRun())
            return;

        switch (State.Status)
        {
            case StudioMemberBindingRunStatus.AdmissionPending:
                await SendAdmissionRequestAndScheduleWatchdogAsync(ct);
                break;
            case StudioMemberBindingRunStatus.Admitted:
                await SendPlatformBindingStartRequestedAsync(State.UpdatedAtUtc, ct);
                break;
            case StudioMemberBindingRunStatus.PlatformBindingPending:
                await RecoverPlatformBindingPendingAsync(
                    State.UpdatedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    ct);
                break;
            case StudioMemberBindingRunStatus.MemberNotificationPending:
                await SendMemberTerminalNotificationAsync(ct);
                break;
        }
    }

    [EventHandler(EndpointName = "requestBindingRun")]
    public async Task HandleRequested(StudioMemberBindingRunRequested evt)
    {
        if (!string.IsNullOrEmpty(State.BindingRunId))
        {
            if (!string.Equals(State.BindingRunId, evt.Request.BindingRunId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"binding run already initialized with id '{State.BindingRunId}'.");
            }

            if (!IsSameRequest(State.Request, evt.Request, State.RequestHash))
            {
                throw new InvalidOperationException(
                    $"binding run '{State.BindingRunId}' already exists with a different request payload.");
            }

            if (State.Status == StudioMemberBindingRunStatus.AdmissionPending)
                await SendAdmissionRequestAndScheduleWatchdogAsync();

            return;
        }

        await PersistDomainEventAsync(evt);
        await SendAdmissionRequestAndScheduleWatchdogAsync();
    }

    [EventHandler(EndpointName = "bindingAdmissionWatchdog", AllowSelfHandling = true)]
    public async Task HandleAdmissionWatchdogFired(StudioMemberBindingAdmissionWatchdogFired evt)
    {
        if (!CanAcceptAdmission(evt.BindingRunId))
            return;

        await SendAdmissionRequestAndScheduleWatchdogAsync();
    }

    [EventHandler(EndpointName = "admitBindingRun")]
    public async Task HandleAdmitted(StudioMemberBindingAdmittedEvent evt)
    {
        if (IsRuntimeEnvelopeRedelivery()
            && IsExactCommittedAdmission(evt))
        {
            await SendPlatformBindingStartRequestedAsync(evt.AdmittedAtUtc);
            return;
        }

        if (!CanAcceptAdmission(evt.BindingRunId))
            return;

        await PersistDomainEventAsync(evt);
        await SendPlatformBindingStartRequestedAsync(evt.AdmittedAtUtc);
    }

    [EventHandler(EndpointName = "rejectBindingRun")]
    public async Task HandleRejected(StudioMemberBindingRejectedEvent evt)
    {
        if (!CanAcceptRunEvent(evt.BindingRunId))
            return;

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "startPlatformBinding", AllowSelfHandling = true)]
    public async Task HandlePlatformBindingStartRequested(StudioMemberPlatformBindingExecutionStartRequested evt)
    {
        if (!CanAcceptPlatformBindingStart(evt))
            return;

        if (State.Status == StudioMemberBindingRunStatus.Admitted)
        {
            await PersistDomainEventsAsync(
            [
                evt,
                BuildLegacyPlatformBindingStartFence(evt),
                BuildLegacyPlatformBindingExecutionFence(evt),
            ]);
        }

        await SchedulePlatformBindingWatchdogAsync();

        if (_platformBindingPort == null)
        {
            await SendToAsync(Id, new StudioMemberPlatformBindingExecutionFailed
            {
                BindingRunId = evt.BindingRunId,
                PlatformBindingCommandId = evt.PlatformBindingCommandId,
                Failure = new StudioMemberBindingFailure
                {
                    Code = "STUDIO_MEMBER_PLATFORM_BINDING_PORT_UNAVAILABLE",
                    Message = "studio member platform binding command port is not registered.",
                    FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
                ProtocolVersion = evt.ProtocolVersion,
                ExecutionAttempt = evt.ExecutionAttempt,
                ExecutionStage = StudioMemberPlatformBindingExecutionStage.AcceptancePending,
            });
            return;
        }

        var accepted = await _platformBindingPort.StartAsync(Id, evt);
        await SendToAsync(Id, accepted);
    }

    [EventHandler(EndpointName = "acceptPlatformBinding", AllowSelfHandling = true)]
    public async Task HandlePlatformBindingAccepted(StudioMemberPlatformBindingExecutionStartAccepted evt)
    {
        if (IsRuntimeEnvelopeRedelivery()
            && IsExactCommittedPlatformBindingAcceptance(evt))
        {
            await SendPlatformBindingPendingAndExecuteAsync(evt.AcceptedAtUtc);
            return;
        }

        if (!CanAcceptPlatformBindingAccepted(evt))
            return;

        await PersistDomainEventAsync(evt);
        await SendPlatformBindingPendingAndExecuteAsync(evt.AcceptedAtUtc);
    }

    [EventHandler(EndpointName = "executePlatformBinding", AllowSelfHandling = true)]
    public async Task HandlePlatformBindingExecuteRequested(StudioMemberPlatformBindingStageExecuteRequested evt)
    {
        if (CanRecoverMemberTerminalNotificationForExecuteRequested(evt))
        {
            await SendMemberTerminalNotificationAsync();
            return;
        }

        if (IsRuntimeEnvelopeRedelivery()
            && IsExactCommittedPlatformBindingStageStart(evt))
        {
            await SchedulePlatformBindingWatchdogAsync();
            return;
        }

        if (!CanAcceptPlatformBindingExecuteRequested(evt))
            return;

        if (!HasSupportedPlatformBindingProtocol(State))
        {
            await FailCheckpointUnavailableAsync();
            return;
        }

        if (IsReadinessExecutionStage(State.PlatformExecutionStage)
            && HasPlatformReadinessDeadlineExpired(DateTimeOffset.UtcNow))
        {
            await FailPlatformReadinessTimeoutAsync();
            return;
        }

        var executionStage = State.PlatformExecutionStage switch
        {
            StudioMemberPlatformBindingExecutionStage.CommandPending =>
                StudioMemberPlatformBindingExecutionStage.CommandInFlight,
            StudioMemberPlatformBindingExecutionStage.ReadinessPending or
                StudioMemberPlatformBindingExecutionStage.ReadinessInFlight =>
                StudioMemberPlatformBindingExecutionStage.ReadinessInFlight,
            _ => StudioMemberPlatformBindingExecutionStage.Unspecified,
        };
        if (executionStage == StudioMemberPlatformBindingExecutionStage.Unspecified)
        {
            await FailCheckpointUnavailableAsync();
            return;
        }

        var executionAttempt = State.PlatformExecutionAttempt + 1;
        var stageStartedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        await PersistDomainEventAsync(new StudioMemberPlatformBindingStageStarted
        {
            BindingRunId = evt.BindingRunId,
            PlatformBindingCommandId = evt.PlatformBindingCommandId,
            ProtocolVersion = evt.ProtocolVersion,
            ExecutionAttempt = executionAttempt,
            ExecutionStage = executionStage,
            StageStartedAtUtc = stageStartedAtUtc,
        });

        if (_platformBindingPort == null)
        {
            await RunWithPlatformBindingWatchdogAsync(
                () => SendToAsync(Id, new StudioMemberPlatformBindingExecutionFailed
                {
                    BindingRunId = evt.BindingRunId,
                    PlatformBindingCommandId = evt.PlatformBindingCommandId,
                    Failure = new StudioMemberBindingFailure
                    {
                        Code = "STUDIO_MEMBER_PLATFORM_BINDING_PORT_UNAVAILABLE",
                        Message = "studio member platform binding command port is not registered.",
                        FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    },
                    ProtocolVersion = State.PlatformBindingProtocolVersion,
                    ExecutionAttempt = State.PlatformExecutionAttempt,
                    ExecutionStage = State.PlatformExecutionStage,
                }),
                ct: default);
            return;
        }

        var executionRequest = new StudioMemberPlatformBindingExecutionRequest
        {
            BindingRunId = State.BindingRunId,
            PlatformBindingCommandId = State.PlatformBindingCommandId,
            Request = State.Request.Clone(),
            Admitted = State.Admitted.Clone(),
            ProtocolVersion = State.PlatformBindingProtocolVersion,
            ExecutionAttempt = State.PlatformExecutionAttempt,
            ExecutionStage = State.PlatformExecutionStage,
        };
        if (State.PlatformBindingRecoverySnapshot != null)
            executionRequest.RecoverySnapshot = State.PlatformBindingRecoverySnapshot.Clone();

        await RunWithPlatformBindingWatchdogAsync(
            () => _platformBindingPort.ExecuteAsync(Id, executionRequest),
            ct: default);
    }

    [EventHandler(EndpointName = "platformBindingWatchdog", AllowSelfHandling = true)]
    public async Task HandlePlatformBindingWatchdogFired(StudioMemberPlatformBindingExecutionWatchdogFired evt)
    {
        if (CanRecoverMemberTerminalNotification(
                evt.BindingRunId,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExpectedExecutionAttempt))
        {
            await SendMemberTerminalNotificationAsync();
            return;
        }

        if (!CanAcceptPlatformBindingWatchdog(evt))
            return;

        if (!HasSupportedPlatformBindingProtocol(State))
        {
            await FailCheckpointUnavailableAsync();
            return;
        }

        if (IsReadinessExecutionStage(State.PlatformExecutionStage)
            && HasPlatformReadinessDeadlineExpired(DateTimeOffset.UtcNow))
        {
            await FailPlatformReadinessTimeoutAsync();
            return;
        }

        switch (State.PlatformExecutionStage)
        {
            case StudioMemberPlatformBindingExecutionStage.AcceptancePending:
                await SendPlatformBindingStartRequestedAsync();
                return;
            case StudioMemberPlatformBindingExecutionStage.CommandPending:
            case StudioMemberPlatformBindingExecutionStage.ReadinessPending:
                await SendPlatformBindingExecuteRequestedAsync();
                return;
            case StudioMemberPlatformBindingExecutionStage.CommandInFlight when IsPlatformExecutionStale():
                await FailCheckpointUnavailableAsync();
                return;
            case StudioMemberPlatformBindingExecutionStage.ReadinessInFlight when IsPlatformExecutionStale():
                await SendPlatformBindingExecuteRequestedAsync();
                return;
            case StudioMemberPlatformBindingExecutionStage.CommandInFlight:
            case StudioMemberPlatformBindingExecutionStage.ReadinessInFlight:
                await SchedulePlatformBindingWatchdogAsync();
                return;
            default:
                await FailCheckpointUnavailableAsync();
                return;
        }
    }

    [EventHandler(EndpointName = "platformBindingCommandsCompleted", AllowSelfHandling = true)]
    public async Task HandlePlatformBindingCommandsCompleted(StudioMemberPlatformBindingCommandsCompleted evt)
    {
        if (CanRecoverMemberTerminalNotification(
                evt.BindingRunId,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExecutionAttempt))
        {
            await SendMemberTerminalNotificationAsync();
            return;
        }

        if (IsRuntimeEnvelopeRedelivery()
            && IsExactCommittedPlatformBindingCommandsCompleted(evt))
        {
            await SchedulePlatformBindingExecuteRequestedAsync(PlatformBindingExecuteInitialDelay);
            return;
        }

        if (!CanAcceptPlatformBindingCommandsCompleted(evt))
            return;

        var committed = evt.Clone();
        committed.ReadinessDeadlineAtUtc = Timestamp.FromDateTimeOffset(
            DateTimeOffset.UtcNow.Add(PlatformBindingReadinessBudget));
        await PersistDomainEventAsync(committed);
        await SchedulePlatformBindingExecuteRequestedAsync(PlatformBindingExecuteInitialDelay);
    }

    [EventHandler(EndpointName = "platformBindingReadinessObservationTimedOut", AllowSelfHandling = true)]
    public async Task HandlePlatformBindingReadinessObservationTimedOut(
        StudioMemberPlatformBindingReadinessObservationTimedOut evt)
    {
        if (CanRecoverMemberTerminalNotification(
                evt.BindingRunId,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExecutionAttempt))
        {
            await SendMemberTerminalNotificationAsync();
            return;
        }

        if (IsRuntimeEnvelopeRedelivery()
            && IsExactCommittedPlatformBindingReadinessObservationTimeout(evt))
        {
            await SchedulePlatformBindingWatchdogAsync();
            return;
        }

        if (!CanAcceptPlatformBindingReadinessResult(
                evt.BindingRunId,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExecutionAttempt))
            return;

        await PersistDomainEventAsync(evt);
        if (HasPlatformReadinessDeadlineExpired(DateTimeOffset.UtcNow))
        {
            await FailPlatformReadinessTimeoutAsync();
            return;
        }

        await SchedulePlatformBindingWatchdogAsync();
    }

    [EventHandler(EndpointName = "completePlatformBinding")]
    public async Task HandlePlatformBindingExecutionSucceeded(StudioMemberPlatformBindingExecutionSucceeded evt)
    {
        if (CanRecoverMemberTerminalNotification(
                evt.BindingRunId,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExecutionAttempt))
        {
            await SendMemberTerminalNotificationAsync();
            return;
        }

        if (IsRuntimeEnvelopeRedelivery()
            && IsExactCommittedPlatformBindingSuccess(evt))
        {
            await SendMemberTerminalNotificationAsync();
            return;
        }

        if (!CanAcceptPlatformBindingReadinessResult(
                evt.BindingRunId,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExecutionAttempt))
            return;

        if (HasPlatformReadinessDeadlineExpired(DateTimeOffset.UtcNow))
        {
            await FailPlatformReadinessTimeoutAsync();
            return;
        }

        await PersistDomainEventAsync(evt);
        await SendMemberTerminalNotificationAsync();
    }

    [EventHandler(EndpointName = "failPlatformBinding", AllowSelfHandling = true)]
    public async Task HandlePlatformBindingExecutionFailed(StudioMemberPlatformBindingExecutionFailed evt)
    {
        if (CanRecoverMemberTerminalNotification(
                evt.BindingRunId,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExecutionAttempt))
        {
            await SendMemberTerminalNotificationAsync();
            return;
        }

        if (IsRuntimeEnvelopeRedelivery()
            && IsExactCommittedPlatformBindingFailure(evt))
        {
            await SendMemberTerminalNotificationAsync();
            return;
        }

        if (!CanAcceptPlatformBindingFailure(evt))
            return;

        await PersistDomainEventAsync(evt);
        await SendMemberTerminalNotificationAsync();
    }

    [EventHandler(EndpointName = "terminateMemberBindingAuthority")]
    public async Task HandleMemberBindingAuthorityTerminated(StudioMemberBindingAuthorityTerminated evt)
    {
        if (!HasCanonicalMemberAuthorityPublisher()
            || !CanApplyMemberBindingAuthorityTermination(State, evt))
        {
            return;
        }

        if (IsSameMemberBindingAuthorityTermination(State, evt))
        {
            if (!IsRuntimeEnvelopeRedelivery())
                return;
        }
        else
        {
            await PersistDomainEventAsync(evt);
        }

        await SendMemberTerminalNotificationAsync();
    }

    [EventHandler(EndpointName = "acknowledgeMemberBindingTerminal", AllowSelfHandling = true)]
    public async Task HandleMemberBindingTerminalAcknowledged(StudioMemberBindingTerminalAcknowledged evt)
    {
        if (!HasCanonicalMemberAuthorityPublisher()
            || !CanAcceptMemberTerminalAcknowledgement(evt.BindingRunId, evt.Status))
        {
            return;
        }

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(
        EndpointName = "memberTerminalNotificationWatchdog",
        AllowSelfHandling = true,
        OnlySelfHandling = true)]
    public async Task HandleMemberTerminalNotificationWatchdogFired(
        StudioMemberBindingTerminalNotificationWatchdogFired evt)
    {
        if (!CanAcceptMemberTerminalNotificationWatchdog(evt))
            return;

        await SendMemberTerminalNotificationAsync();
    }

    protected override StudioMemberBindingRunState TransitionState(
        StudioMemberBindingRunState current,
        IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<StudioMemberBindingRunRequested>(ApplyRequested)
            .On<StudioMemberBindingAdmittedEvent>(ApplyAdmitted)
            .On<StudioMemberBindingRejectedEvent>(ApplyRejected)
            .On<StudioMemberPlatformBindingExecutionStartRequested>(ApplyPlatformBindingStartRequested)
            .On<StudioMemberPlatformBindingExecutionStartAccepted>(ApplyPlatformBindingAccepted)
            .On<StudioMemberPlatformBindingStageStarted>(ApplyPlatformBindingExecutionStarted)
            .On<StudioMemberPlatformBindingStartRequested>(ApplyLegacyPlatformBindingStartRequested)
            .On<StudioMemberPlatformBindingAccepted>(ApplyLegacyPlatformBindingAccepted)
            .On<StudioMemberPlatformBindingExecutionStarted>(ApplyLegacyPlatformBindingExecutionStarted)
            .On<StudioMemberPlatformBindingCommandsCompleted>(ApplyPlatformBindingCommandsCompleted)
            .On<StudioMemberPlatformBindingReadinessTimedOut>(ApplyLegacyPlatformBindingReadinessTimedOut)
            .On<StudioMemberPlatformBindingReadinessObservationTimedOut>(
                ApplyPlatformBindingReadinessObservationTimedOut)
            .On<StudioMemberPlatformBindingSucceeded>(ApplyLegacyPlatformBindingSucceeded)
            .On<StudioMemberPlatformBindingFailed>(ApplyLegacyPlatformBindingFailed)
            .On<StudioMemberPlatformBindingExecutionSucceeded>(ApplyPlatformBindingExecutionSucceeded)
            .On<StudioMemberPlatformBindingExecutionFailed>(ApplyPlatformBindingExecutionFailed)
            .On<StudioMemberBindingAuthorityTerminated>(ApplyMemberBindingAuthorityTerminated)
            .On<StudioMemberBindingTerminalNotificationAttemptStarted>(
                ApplyMemberTerminalNotificationAttemptStarted)
            .On<StudioMemberBindingTerminalAcknowledged>(ApplyMemberBindingTerminalAcknowledged)
            .OrCurrent();
    }

    private static StudioMemberBindingRunState ApplyRequested(
        StudioMemberBindingRunState state,
        StudioMemberBindingRunRequested evt)
    {
        if (!string.IsNullOrEmpty(state.BindingRunId))
            return state;

        return new StudioMemberBindingRunState
        {
            BindingRunId = evt.Request.BindingRunId,
            ScopeId = evt.Request.ScopeId,
            MemberId = evt.Request.MemberId,
            RequestHash = evt.Request.RequestHash,
            Request = evt.Request.Clone(),
            Status = StudioMemberBindingRunStatus.AdmissionPending,
            AcceptedAtUtc = evt.RequestedAtUtc,
            UpdatedAtUtc = evt.RequestedAtUtc,
            AttemptCount = 0,
        };
    }

    private static StudioMemberBindingRunState ApplyAdmitted(
        StudioMemberBindingRunState state,
        StudioMemberBindingAdmittedEvent evt)
    {
        if (IsStale(state, evt.BindingRunId)
            || state.Status != StudioMemberBindingRunStatus.AdmissionPending)
        {
            return state;
        }

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.Admitted;
        next.Admitted = new StudioMemberBindingAdmittedSnapshot
        {
            MemberId = evt.MemberId,
            ScopeId = evt.ScopeId,
            PublishedServiceId = evt.PublishedServiceId,
            ImplementationKind = evt.ImplementationKind,
            DisplayName = evt.DisplayName,
        };
        next.UpdatedAtUtc = evt.AdmittedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyRejected(
        StudioMemberBindingRunState state,
        StudioMemberBindingRejectedEvent evt)
    {
        if (IsStale(state, evt.BindingRunId) || IsTerminal(state.Status))
            return state;

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.Rejected;
        next.Failure = evt.Failure?.Clone();
        if (evt.Failure?.FailedAtUtc != null)
            next.UpdatedAtUtc = evt.Failure.FailedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyPlatformBindingStartRequested(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingExecutionStartRequested evt)
    {
        if (evt.ProtocolVersion != StudioMemberConventions.PlatformBindingProtocolVersion
            || evt.ExecutionAttempt != 0)
        {
            return ApplyUnsupportedPlatformBindingStartRequested(state, evt);
        }

        if (IsStale(state, evt.BindingRunId)
            || state.Status != StudioMemberBindingRunStatus.Admitted
            || evt.ProtocolVersion != StudioMemberConventions.PlatformBindingProtocolVersion)
        {
            return state;
        }

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.PlatformBindingPending;
        next.PlatformBindingCommandId = evt.PlatformBindingCommandId;
        ApplyLegacyPlatformExecutionFence(next);
        next.PlatformExecutionStageStartedAtUtc = null;
        next.PlatformBindingRecoverySnapshot = null;
        next.PlatformReadinessDeadlineAtUtc = null;
        next.LastPlatformReadinessStatus = StudioMemberPlatformBindingReadinessStatus.Unspecified;
        next.MemberNotificationAttempt = 0;
        next.MemberNotificationAttemptedAtUtc = null;
        next.PlatformBindingProtocolVersion = evt.ProtocolVersion;
        next.PlatformExecutionAttempt = evt.ExecutionAttempt;
        next.PlatformExecutionStage = StudioMemberPlatformBindingExecutionStage.AcceptancePending;
        next.AttemptCount++;
        next.UpdatedAtUtc = evt.RequestedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyPlatformBindingAccepted(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingExecutionStartAccepted evt)
    {
        if (IsStale(state, evt.BindingRunId)
            || state.Status != StudioMemberBindingRunStatus.PlatformBindingPending
            || !HasSupportedPlatformBindingProtocol(state)
            || state.PlatformExecutionStage != StudioMemberPlatformBindingExecutionStage.AcceptancePending
            || !HasMatchingPlatformFence(
                state,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExecutionAttempt))
        {
            return state;
        }

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.PlatformBindingPending;
        next.PlatformBindingCommandId = evt.PlatformBindingCommandId;
        ApplyLegacyPlatformExecutionFence(next);
        next.PlatformExecutionStageStartedAtUtc = null;
        next.PlatformExecutionStage = StudioMemberPlatformBindingExecutionStage.CommandPending;
        next.UpdatedAtUtc = evt.AcceptedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyPlatformBindingExecutionStarted(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingStageStarted evt)
    {
        if (IsStale(state, evt.BindingRunId)
            || state.Status != StudioMemberBindingRunStatus.PlatformBindingPending
            || !HasSupportedPlatformBindingProtocol(state)
            || !HasMatchingPlatformProtocolAndCommand(
                state,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion)
            || evt.ExecutionAttempt != state.PlatformExecutionAttempt + 1
            || evt.StageStartedAtUtc == null
            || !IsValidExecutionStageTransition(state.PlatformExecutionStage, evt.ExecutionStage))
        {
            return state;
        }

        var next = state.Clone();
        next.PlatformExecutionAttempt = evt.ExecutionAttempt;
        next.PlatformExecutionStage = evt.ExecutionStage;
        ApplyLegacyPlatformExecutionFence(next);
        next.PlatformExecutionStageStartedAtUtc = evt.StageStartedAtUtc;
        next.UpdatedAtUtc = evt.StageStartedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyPlatformBindingCommandsCompleted(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingCommandsCompleted evt)
    {
        if (!CanApplyPlatformBindingStageResult(
                state,
                evt.BindingRunId,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExecutionAttempt,
                StudioMemberPlatformBindingExecutionStage.CommandInFlight)
            || evt.RecoverySnapshot == null)
        {
            return state;
        }

        var next = state.Clone();
        next.PlatformBindingRecoverySnapshot = evt.RecoverySnapshot.Clone();
        next.PlatformExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessPending;
        ApplyLegacyPlatformExecutionFence(next);
        next.PlatformExecutionStageStartedAtUtc = null;
        next.PlatformReadinessDeadlineAtUtc = evt.ReadinessDeadlineAtUtc?.Clone()
            ?? BuildPlatformReadinessDeadline(state, evt.CompletedAtUtc);
        next.LastPlatformReadinessStatus = StudioMemberPlatformBindingReadinessStatus.Unspecified;
        next.UpdatedAtUtc = evt.CompletedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyLegacyPlatformBindingStartRequested(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingStartRequested evt)
    {
        if (IsStale(state, evt.BindingRunId)
            || state.Status != StudioMemberBindingRunStatus.Admitted)
        {
            return state;
        }

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.PlatformBindingPending;
        next.PlatformBindingCommandId = evt.PlatformBindingCommandId;
        next.PlatformExecutionInFlight = false;
        next.PlatformExecutionStartedAtUtc = null;
        next.PlatformBindingRecoverySnapshot = null;
        next.PlatformBindingProtocolVersion = 0;
        next.PlatformExecutionAttempt = 0;
        next.PlatformExecutionStage = StudioMemberPlatformBindingExecutionStage.Unspecified;
        next.PlatformExecutionStageStartedAtUtc = null;
        next.AttemptCount++;
        next.UpdatedAtUtc = evt.RequestedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyUnsupportedPlatformBindingStartRequested(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingExecutionStartRequested evt)
    {
        if (IsStale(state, evt.BindingRunId)
            || state.Status != StudioMemberBindingRunStatus.Admitted)
        {
            return state;
        }

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.PlatformBindingPending;
        next.PlatformBindingCommandId = evt.PlatformBindingCommandId;
        ApplyLegacyPlatformExecutionFence(next);
        next.PlatformBindingRecoverySnapshot = null;
        next.PlatformBindingProtocolVersion = evt.ProtocolVersion;
        next.PlatformExecutionAttempt = evt.ExecutionAttempt;
        next.PlatformExecutionStage = StudioMemberPlatformBindingExecutionStage.Unspecified;
        next.PlatformExecutionStageStartedAtUtc = null;
        next.AttemptCount++;
        next.UpdatedAtUtc = evt.RequestedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyLegacyPlatformBindingAccepted(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingAccepted evt)
    {
        if (!CanApplyLegacyPlatformBindingResult(state, evt.BindingRunId, evt.PlatformBindingCommandId))
            return state;

        var next = state.Clone();
        next.PlatformExecutionInFlight = false;
        next.PlatformExecutionStartedAtUtc = null;
        next.PlatformExecutionStageStartedAtUtc = null;
        next.UpdatedAtUtc = evt.AcceptedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyLegacyPlatformBindingExecutionStarted(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingExecutionStarted evt)
    {
        if (!CanApplyLegacyPlatformBindingResult(state, evt.BindingRunId, evt.PlatformBindingCommandId))
            return state;

        var next = state.Clone();
        next.PlatformExecutionInFlight = true;
        next.PlatformExecutionStartedAtUtc = evt.StartedAtUtc;
        next.PlatformExecutionStageStartedAtUtc = null;
        next.UpdatedAtUtc = evt.StartedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyLegacyPlatformBindingReadinessTimedOut(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingReadinessTimedOut evt)
    {
        if (!CanApplyLegacyPlatformBindingResult(state, evt.BindingRunId, evt.PlatformBindingCommandId))
            return state;

        var next = state.Clone();
        next.PlatformExecutionInFlight = false;
        next.PlatformExecutionStartedAtUtc = null;
        next.PlatformExecutionStageStartedAtUtc = null;
        next.LastPlatformReadinessStatus = evt.ReadinessStatus;
        next.UpdatedAtUtc = evt.TimedOutAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyLegacyPlatformBindingSucceeded(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingSucceeded evt)
    {
        if (!CanApplyLegacyPlatformBindingResult(state, evt.BindingRunId, evt.PlatformBindingCommandId))
            return state;

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.MemberNotificationPending;
        next.PlatformResult = evt.Result?.Clone();
        next.PlatformExecutionInFlight = false;
        next.PlatformExecutionStartedAtUtc = null;
        next.PlatformExecutionStageStartedAtUtc = null;
        next.PlatformBindingRecoverySnapshot = null;
        next.UpdatedAtUtc = evt.CompletedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyLegacyPlatformBindingFailed(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingFailed evt)
    {
        if (!CanApplyLegacyPlatformBindingResult(state, evt.BindingRunId, evt.PlatformBindingCommandId))
            return state;

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.MemberNotificationPending;
        next.Failure = evt.Failure?.Clone();
        next.PlatformExecutionInFlight = false;
        next.PlatformExecutionStartedAtUtc = null;
        next.PlatformExecutionStageStartedAtUtc = null;
        next.PlatformBindingRecoverySnapshot = null;
        if (evt.Failure?.FailedAtUtc != null)
            next.UpdatedAtUtc = evt.Failure.FailedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyPlatformBindingReadinessObservationTimedOut(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingReadinessObservationTimedOut evt)
    {
        if (!CanApplyPlatformBindingStageResult(
                state,
                evt.BindingRunId,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExecutionAttempt,
                StudioMemberPlatformBindingExecutionStage.ReadinessInFlight))
            return state;

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.PlatformBindingPending;
        next.PlatformExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessPending;
        ApplyLegacyPlatformExecutionFence(next);
        next.PlatformExecutionStageStartedAtUtc = null;
        next.LastPlatformReadinessStatus = evt.ReadinessStatus;
        next.PlatformReadinessDeadlineAtUtc ??= BuildPlatformReadinessDeadline(state, evt.TimedOutAtUtc);
        next.UpdatedAtUtc = evt.TimedOutAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyPlatformBindingExecutionSucceeded(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingExecutionSucceeded evt)
    {
        if (!CanApplyPlatformBindingStageResult(
                state,
                evt.BindingRunId,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExecutionAttempt,
                StudioMemberPlatformBindingExecutionStage.ReadinessInFlight))
            return state;

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.MemberNotificationPending;
        next.PlatformResult = evt.Result?.Clone();
        next.PlatformExecutionStage = StudioMemberPlatformBindingExecutionStage.Unspecified;
        next.PlatformExecutionInFlight = false;
        next.PlatformExecutionStartedAtUtc = null;
        next.PlatformExecutionStageStartedAtUtc = null;
        next.PlatformBindingRecoverySnapshot = null;
        next.LastPlatformReadinessStatus = StudioMemberPlatformBindingReadinessStatus.Ready;
        next.MemberNotificationAttempt = 0;
        next.MemberNotificationAttemptedAtUtc = null;
        next.UpdatedAtUtc = evt.CompletedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyPlatformBindingExecutionFailed(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingExecutionFailed evt)
    {
        if (!CanApplyPlatformBindingFailure(state, evt))
            return state;

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.MemberNotificationPending;
        next.Failure = evt.Failure?.Clone();
        next.PlatformExecutionInFlight = false;
        next.PlatformExecutionStartedAtUtc = null;
        next.PlatformExecutionStageStartedAtUtc = null;
        next.PlatformBindingRecoverySnapshot = null;
        next.MemberNotificationAttempt = 0;
        next.MemberNotificationAttemptedAtUtc = null;
        if (evt.Failure?.FailedAtUtc != null)
            next.UpdatedAtUtc = evt.Failure.FailedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyMemberBindingAuthorityTerminated(
        StudioMemberBindingRunState state,
        StudioMemberBindingAuthorityTerminated evt)
    {
        if (!CanApplyMemberBindingAuthorityTermination(state, evt)
            || IsSameMemberBindingAuthorityTermination(state, evt))
        {
            return state;
        }

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.MemberNotificationPending;
        next.Failure = evt.Failure.Clone();
        next.PlatformResult = null;
        next.PlatformExecutionInFlight = false;
        next.PlatformExecutionStartedAtUtc = null;
        next.PlatformExecutionStageStartedAtUtc = null;
        next.PlatformBindingRecoverySnapshot = null;
        next.UpdatedAtUtc = evt.Failure.FailedAtUtc.Clone();
        return next;
    }

    private static StudioMemberBindingRunState ApplyMemberTerminalNotificationAttemptStarted(
        StudioMemberBindingRunState state,
        StudioMemberBindingTerminalNotificationAttemptStarted evt)
    {
        if (IsStale(state, evt.BindingRunId)
            || state.Status != StudioMemberBindingRunStatus.MemberNotificationPending
            || evt.NotificationAttempt != state.MemberNotificationAttempt + 1
            || evt.AttemptedAtUtc == null)
        {
            return state;
        }

        var next = state.Clone();
        next.MemberNotificationAttempt = evt.NotificationAttempt;
        next.MemberNotificationAttemptedAtUtc = evt.AttemptedAtUtc.Clone();
        return next;
    }

    private static StudioMemberBindingRunState ApplyMemberBindingTerminalAcknowledged(
        StudioMemberBindingRunState state,
        StudioMemberBindingTerminalAcknowledged evt)
    {
        if (IsStale(state, evt.BindingRunId)
            || state.Status != StudioMemberBindingRunStatus.MemberNotificationPending)
        {
            return state;
        }

        var next = state.Clone();
        next.Status = evt.Status switch
        {
            StudioMemberBindingRunStatus.Succeeded => StudioMemberBindingRunStatus.Succeeded,
            StudioMemberBindingRunStatus.Failed => StudioMemberBindingRunStatus.Failed,
            _ => next.Status,
        };
        next.UpdatedAtUtc = evt.AcknowledgedAtUtc;
        return next;
    }

    private static bool IsStale(StudioMemberBindingRunState state, string bindingRunId) =>
        !string.IsNullOrEmpty(state.BindingRunId)
        && !string.Equals(state.BindingRunId, bindingRunId, StringComparison.Ordinal);

    private static bool IsTerminal(StudioMemberBindingRunStatus status) =>
        status is StudioMemberBindingRunStatus.Succeeded
            or StudioMemberBindingRunStatus.Failed
            or StudioMemberBindingRunStatus.Rejected;

    private static bool CanApplyMemberBindingAuthorityTermination(
        StudioMemberBindingRunState state,
        StudioMemberBindingAuthorityTerminated evt) =>
        !string.IsNullOrEmpty(state.BindingRunId)
        && string.Equals(state.BindingRunId, evt.BindingRunId, StringComparison.Ordinal)
        && string.Equals(state.ScopeId, evt.ScopeId, StringComparison.Ordinal)
        && string.Equals(state.MemberId, evt.MemberId, StringComparison.Ordinal)
        && evt.Failure != null
        && string.Equals(evt.Failure.Code, MemberDeletedFailureCode, StringComparison.Ordinal)
        && evt.Failure.FailedAtUtc != null
        && !IsTerminal(state.Status);

    private static bool IsSameMemberBindingAuthorityTermination(
        StudioMemberBindingRunState state,
        StudioMemberBindingAuthorityTerminated evt) =>
        state.Status == StudioMemberBindingRunStatus.MemberNotificationPending
        && state.PlatformResult == null
        && state.Failure != null
        && state.Failure.Equals(evt.Failure);

    private bool HasCanonicalMemberAuthorityPublisher()
    {
        if (ActiveInboundEnvelope == null)
            return true;

        var publisherActorId = ActiveInboundEnvelope.Route?.PublisherActorId?.Trim() ?? string.Empty;
        return string.Equals(
            publisherActorId,
            StudioMemberConventions.BuildActorId(State.ScopeId, State.MemberId),
            StringComparison.Ordinal);
    }

    private bool CanAcceptRunEvent(string bindingRunId) =>
        !string.IsNullOrEmpty(State.BindingRunId)
        && string.Equals(State.BindingRunId, bindingRunId, StringComparison.Ordinal)
        && !IsTerminal(State.Status);

    private bool CanAcceptAdmission(string bindingRunId) =>
        !string.IsNullOrEmpty(State.BindingRunId)
        && string.Equals(State.BindingRunId, bindingRunId, StringComparison.Ordinal)
        && State.Status == StudioMemberBindingRunStatus.AdmissionPending;

    private bool IsExactCommittedAdmission(StudioMemberBindingAdmittedEvent evt) =>
        State.Status == StudioMemberBindingRunStatus.Admitted
        && State.Admitted != null
        && string.Equals(State.BindingRunId, evt.BindingRunId, StringComparison.Ordinal)
        && string.Equals(State.ScopeId, evt.ScopeId, StringComparison.Ordinal)
        && string.Equals(State.MemberId, evt.MemberId, StringComparison.Ordinal)
        && string.Equals(State.Admitted.ScopeId, evt.ScopeId, StringComparison.Ordinal)
        && string.Equals(State.Admitted.MemberId, evt.MemberId, StringComparison.Ordinal)
        && string.Equals(
            State.Admitted.PublishedServiceId,
            evt.PublishedServiceId,
            StringComparison.Ordinal)
        && State.Admitted.ImplementationKind == evt.ImplementationKind
        && string.Equals(State.Admitted.DisplayName, evt.DisplayName, StringComparison.Ordinal)
        && Equals(State.UpdatedAtUtc, evt.AdmittedAtUtc);

    private bool CanAcceptPlatformBindingStart(StudioMemberPlatformBindingExecutionStartRequested evt)
    {
        if (string.IsNullOrEmpty(State.BindingRunId)
            || !string.Equals(State.BindingRunId, evt.BindingRunId, StringComparison.Ordinal)
            || evt.ProtocolVersion != StudioMemberConventions.PlatformBindingProtocolVersion
            || evt.ExecutionAttempt != 0)
        {
            return false;
        }

        if (State.Status == StudioMemberBindingRunStatus.Admitted)
            return true;

        return State.Status == StudioMemberBindingRunStatus.PlatformBindingPending
            && State.PlatformExecutionStage == StudioMemberPlatformBindingExecutionStage.AcceptancePending
            && HasMatchingPlatformFence(
                State,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExecutionAttempt);
    }

    private bool CanAcceptPlatformBindingAccepted(StudioMemberPlatformBindingExecutionStartAccepted evt) =>
        !string.IsNullOrEmpty(State.BindingRunId)
        && string.Equals(State.BindingRunId, evt.BindingRunId, StringComparison.Ordinal)
        && State.Status == StudioMemberBindingRunStatus.PlatformBindingPending
        && HasSupportedPlatformBindingProtocol(State)
        && State.PlatformExecutionStage == StudioMemberPlatformBindingExecutionStage.AcceptancePending
        && HasMatchingPlatformFence(
            State,
            evt.PlatformBindingCommandId,
            evt.ProtocolVersion,
            evt.ExecutionAttempt);

    private bool IsExactCommittedPlatformBindingAcceptance(
        StudioMemberPlatformBindingExecutionStartAccepted evt) =>
        State.Status == StudioMemberBindingRunStatus.PlatformBindingPending
        && State.PlatformExecutionStage == StudioMemberPlatformBindingExecutionStage.CommandPending
        && HasSupportedPlatformBindingProtocol(State)
        && HasMatchingPlatformFence(
            State,
            evt.PlatformBindingCommandId,
            evt.ProtocolVersion,
            evt.ExecutionAttempt)
        && Equals(State.UpdatedAtUtc, evt.AcceptedAtUtc);

    private bool CanAcceptPlatformBindingExecuteRequested(StudioMemberPlatformBindingStageExecuteRequested evt)
    {
        if (!CanAcceptPlatformBindingFence(
                evt.BindingRunId,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExpectedExecutionAttempt))
        {
            return false;
        }

        if (!HasSupportedPlatformBindingProtocol(State))
            return true;

        return State.PlatformExecutionStage switch
        {
            StudioMemberPlatformBindingExecutionStage.CommandPending => true,
            StudioMemberPlatformBindingExecutionStage.ReadinessPending => true,
            StudioMemberPlatformBindingExecutionStage.ReadinessInFlight => IsPlatformExecutionStale(),
            _ => false,
        };
    }

    private bool IsExactCommittedPlatformBindingStageStart(
        StudioMemberPlatformBindingStageExecuteRequested evt) =>
        State.Status == StudioMemberBindingRunStatus.PlatformBindingPending
        && HasSupportedPlatformBindingProtocol(State)
        && string.Equals(State.BindingRunId, evt.BindingRunId, StringComparison.Ordinal)
        && HasMatchingPlatformProtocolAndCommand(
            State,
            evt.PlatformBindingCommandId,
            evt.ProtocolVersion)
        && (long)State.PlatformExecutionAttempt == (long)evt.ExpectedExecutionAttempt + 1
        && State.PlatformExecutionStage is (
            StudioMemberPlatformBindingExecutionStage.CommandInFlight
            or StudioMemberPlatformBindingExecutionStage.ReadinessInFlight);

    private bool CanAcceptPlatformBindingWatchdog(StudioMemberPlatformBindingExecutionWatchdogFired evt) =>
        CanAcceptPlatformBindingFence(
            evt.BindingRunId,
            evt.PlatformBindingCommandId,
            evt.ProtocolVersion,
            evt.ExpectedExecutionAttempt);

    private bool CanAcceptPlatformBindingCommandsCompleted(StudioMemberPlatformBindingCommandsCompleted evt) =>
        CanAcceptPlatformBindingStageResult(
            evt.BindingRunId,
            evt.PlatformBindingCommandId,
            evt.ProtocolVersion,
            evt.ExecutionAttempt,
            StudioMemberPlatformBindingExecutionStage.CommandInFlight)
        && evt.RecoverySnapshot != null;

    private bool IsExactCommittedPlatformBindingCommandsCompleted(
        StudioMemberPlatformBindingCommandsCompleted evt) =>
        evt.RecoverySnapshot != null
        && State.Status == StudioMemberBindingRunStatus.PlatformBindingPending
        && State.PlatformExecutionStage == StudioMemberPlatformBindingExecutionStage.ReadinessPending
        && HasMatchingPlatformFence(
            State,
            evt.PlatformBindingCommandId,
            evt.ProtocolVersion,
            evt.ExecutionAttempt)
        && Equals(State.PlatformBindingRecoverySnapshot, evt.RecoverySnapshot);

    private bool IsExactCommittedPlatformBindingReadinessObservationTimeout(
        StudioMemberPlatformBindingReadinessObservationTimedOut evt) =>
        State.Status == StudioMemberBindingRunStatus.PlatformBindingPending
        && State.PlatformExecutionStage == StudioMemberPlatformBindingExecutionStage.ReadinessPending
        && HasMatchingPlatformFence(
            State,
            evt.PlatformBindingCommandId,
            evt.ProtocolVersion,
            evt.ExecutionAttempt)
        && State.LastPlatformReadinessStatus == evt.ReadinessStatus
        && Equals(State.UpdatedAtUtc, evt.TimedOutAtUtc);

    private bool CanRecoverMemberTerminalNotification(
        string bindingRunId,
        string platformBindingCommandId,
        int protocolVersion,
        int executionAttempt) =>
        IsRuntimeEnvelopeRedelivery()
        && State.Status == StudioMemberBindingRunStatus.MemberNotificationPending
        && string.Equals(State.BindingRunId, bindingRunId, StringComparison.Ordinal)
        && HasMatchingPlatformFence(
            State,
            platformBindingCommandId,
            protocolVersion,
            executionAttempt);

    private bool CanRecoverMemberTerminalNotificationForExecuteRequested(
        StudioMemberPlatformBindingStageExecuteRequested evt)
    {
        if (!IsRuntimeEnvelopeRedelivery())
            return false;

        if (CanRecoverMemberTerminalNotification(
                evt.BindingRunId,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExpectedExecutionAttempt))
        {
            return true;
        }

        return State.Status == StudioMemberBindingRunStatus.MemberNotificationPending
            && string.Equals(State.BindingRunId, evt.BindingRunId, StringComparison.Ordinal)
            && HasMatchingPlatformProtocolAndCommand(
                State,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion)
            && (long)State.PlatformExecutionAttempt ==
                (long)evt.ExpectedExecutionAttempt + 1;
    }

    private bool IsRuntimeEnvelopeRedelivery() =>
        ActiveInboundEnvelope?.Runtime?.Retry?.Attempt > 0;

    private bool CanAcceptPlatformBindingReadinessResult(
        string bindingRunId,
        string platformBindingCommandId,
        int protocolVersion,
        int executionAttempt) =>
        CanAcceptPlatformBindingStageResult(
            bindingRunId,
            platformBindingCommandId,
            protocolVersion,
            executionAttempt,
            StudioMemberPlatformBindingExecutionStage.ReadinessInFlight);

    private bool CanAcceptPlatformBindingFailure(StudioMemberPlatformBindingExecutionFailed evt) =>
        CanApplyPlatformBindingFailure(State, evt);

    private bool CanAcceptPlatformBindingStageResult(
        string bindingRunId,
        string platformBindingCommandId,
        int protocolVersion,
        int executionAttempt,
        StudioMemberPlatformBindingExecutionStage expectedStage) =>
        CanApplyPlatformBindingStageResult(
            State,
            bindingRunId,
            platformBindingCommandId,
            protocolVersion,
            executionAttempt,
            expectedStage);

    private bool CanAcceptPlatformBindingFence(
        string bindingRunId,
        string platformBindingCommandId,
        int protocolVersion,
        int executionAttempt) =>
        !string.IsNullOrEmpty(State.BindingRunId)
        && string.Equals(State.BindingRunId, bindingRunId, StringComparison.Ordinal)
        && State.Status == StudioMemberBindingRunStatus.PlatformBindingPending
        && HasMatchingPlatformFence(State, platformBindingCommandId, protocolVersion, executionAttempt);

    private bool CanAcceptMemberTerminalAcknowledgement(string bindingRunId, StudioMemberBindingRunStatus status) =>
        !string.IsNullOrEmpty(State.BindingRunId)
        && string.Equals(State.BindingRunId, bindingRunId, StringComparison.Ordinal)
        && State.Status == StudioMemberBindingRunStatus.MemberNotificationPending
        && (status == StudioMemberBindingRunStatus.Succeeded || status == StudioMemberBindingRunStatus.Failed)
        && ((status == StudioMemberBindingRunStatus.Succeeded && State.PlatformResult != null)
            || (status == StudioMemberBindingRunStatus.Failed && State.Failure != null));

    private bool IsExactCommittedPlatformBindingSuccess(
        StudioMemberPlatformBindingExecutionSucceeded evt) =>
        State.Status == StudioMemberBindingRunStatus.MemberNotificationPending
        && State.Failure == null
        && State.PlatformResult != null
        && string.Equals(State.BindingRunId, evt.BindingRunId, StringComparison.Ordinal)
        && HasMatchingPlatformFence(
            State,
            evt.PlatformBindingCommandId,
            evt.ProtocolVersion,
            evt.ExecutionAttempt)
        && State.PlatformResult.Equals(evt.Result)
        && Equals(State.UpdatedAtUtc, evt.CompletedAtUtc);

    private bool IsExactCommittedPlatformBindingFailure(
        StudioMemberPlatformBindingExecutionFailed evt) =>
        State.Status == StudioMemberBindingRunStatus.MemberNotificationPending
        && State.PlatformResult == null
        && State.Failure != null
        && string.Equals(State.BindingRunId, evt.BindingRunId, StringComparison.Ordinal)
        && HasMatchingPlatformFence(
            State,
            evt.PlatformBindingCommandId,
            evt.ProtocolVersion,
            evt.ExecutionAttempt)
        && State.PlatformExecutionStage == evt.ExecutionStage
        && State.Failure.Equals(evt.Failure);

    private bool CanAcceptMemberTerminalNotificationWatchdog(
        StudioMemberBindingTerminalNotificationWatchdogFired evt)
    {
        if (string.IsNullOrEmpty(State.BindingRunId)
            || !string.Equals(State.BindingRunId, evt.BindingRunId, StringComparison.Ordinal)
            || State.Status != StudioMemberBindingRunStatus.MemberNotificationPending
            || evt.ExpectedNotificationAttempt <= 0)
        {
            return false;
        }

        // The watchdog is scheduled before its attempt-start event is committed.
        // It can therefore observe either side of that commit boundary, but no
        // older or further-future attempt.
        var attemptDelta = (long)evt.ExpectedNotificationAttempt - State.MemberNotificationAttempt;
        return attemptDelta is 0 or 1;
    }

    private bool IsPlatformExecutionStale()
    {
        if (State.PlatformExecutionStage is not (
                StudioMemberPlatformBindingExecutionStage.CommandInFlight
                or StudioMemberPlatformBindingExecutionStage.ReadinessInFlight)
            || State.PlatformExecutionStageStartedAtUtc == null)
            return false;

        var startedAt = State.PlatformExecutionStageStartedAtUtc.ToDateTimeOffset();
        return DateTimeOffset.UtcNow - startedAt >= PlatformBindingExecutionStaleAfter;
    }

    private static void ApplyLegacyPlatformExecutionFence(StudioMemberBindingRunState state)
    {
        state.PlatformExecutionInFlight = true;
        state.PlatformExecutionStartedAtUtc = LegacyPlatformExecutionFenceAtUtc.Clone();
    }

    private static StudioMemberPlatformBindingStartRequested BuildLegacyPlatformBindingStartFence(
        StudioMemberPlatformBindingExecutionStartRequested evt)
    {
        var legacy = new StudioMemberPlatformBindingStartRequested
        {
            BindingRunId = evt.BindingRunId,
            PlatformBindingCommandId = evt.PlatformBindingCommandId,
            RequestedAtUtc = evt.RequestedAtUtc?.Clone(),
        };
        if (evt.Request != null)
            legacy.Request = evt.Request.Clone();
        if (evt.Admitted != null)
            legacy.Admitted = evt.Admitted.Clone();
        return legacy;
    }

    private static StudioMemberPlatformBindingExecutionStarted BuildLegacyPlatformBindingExecutionFence(
        StudioMemberPlatformBindingExecutionStartRequested evt) =>
        new()
        {
            BindingRunId = evt.BindingRunId,
            PlatformBindingCommandId = evt.PlatformBindingCommandId,
            StartedAtUtc = LegacyPlatformExecutionFenceAtUtc.Clone(),
        };

    private StudioMemberPlatformBindingStartRequested BuildLegacyPlatformBindingStartFence()
    {
        var legacy = new StudioMemberPlatformBindingStartRequested
        {
            BindingRunId = State.BindingRunId,
            PlatformBindingCommandId = State.PlatformBindingCommandId,
            RequestedAtUtc = State.UpdatedAtUtc?.Clone(),
        };
        if (State.Request != null)
            legacy.Request = State.Request.Clone();
        if (State.Admitted != null)
            legacy.Admitted = State.Admitted.Clone();
        return legacy;
    }

    private StudioMemberPlatformBindingExecutionStarted BuildLegacyPlatformBindingExecutionFence() =>
        new()
        {
            BindingRunId = State.BindingRunId,
            PlatformBindingCommandId = State.PlatformBindingCommandId,
            StartedAtUtc = LegacyPlatformExecutionFenceAtUtc.Clone(),
        };

    private static bool CanApplyPlatformBindingStageResult(
        StudioMemberBindingRunState state,
        string bindingRunId,
        string platformBindingCommandId,
        int protocolVersion,
        int executionAttempt,
        StudioMemberPlatformBindingExecutionStage expectedStage) =>
        !string.IsNullOrEmpty(state.BindingRunId)
        && string.Equals(state.BindingRunId, bindingRunId, StringComparison.Ordinal)
        && state.Status == StudioMemberBindingRunStatus.PlatformBindingPending
        && HasSupportedPlatformBindingProtocol(state)
        && state.PlatformExecutionStage == expectedStage
        && HasMatchingPlatformFence(state, platformBindingCommandId, protocolVersion, executionAttempt);

    private static bool CanApplyLegacyPlatformBindingResult(
        StudioMemberBindingRunState state,
        string bindingRunId,
        string platformBindingCommandId) =>
        !string.IsNullOrEmpty(state.BindingRunId)
        && string.Equals(state.BindingRunId, bindingRunId, StringComparison.Ordinal)
        && state.Status == StudioMemberBindingRunStatus.PlatformBindingPending
        && state.PlatformBindingProtocolVersion == 0
        && state.PlatformExecutionAttempt == 0
        && state.PlatformExecutionStage == StudioMemberPlatformBindingExecutionStage.Unspecified
        && string.Equals(state.PlatformBindingCommandId, platformBindingCommandId, StringComparison.Ordinal);

    private static bool CanApplyPlatformBindingFailure(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingExecutionFailed evt)
    {
        if (string.IsNullOrEmpty(state.BindingRunId)
            || !string.Equals(state.BindingRunId, evt.BindingRunId, StringComparison.Ordinal)
            || state.Status != StudioMemberBindingRunStatus.PlatformBindingPending
            || !HasMatchingPlatformFence(
                state,
                evt.PlatformBindingCommandId,
                evt.ProtocolVersion,
                evt.ExecutionAttempt))
        {
            return false;
        }

        if (evt.ExecutionStage != state.PlatformExecutionStage)
            return false;

        if (string.Equals(
                evt.Failure?.Code,
                PlatformBindingCheckpointUnavailableFailureCode,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(
                evt.Failure?.Code,
                PlatformBindingReadinessTimeoutFailureCode,
                StringComparison.Ordinal))
        {
            return HasSupportedPlatformBindingProtocol(state)
                && IsReadinessExecutionStage(state.PlatformExecutionStage);
        }

        if (!HasSupportedPlatformBindingProtocol(state))
            return false;

        return state.PlatformExecutionStage is (
                StudioMemberPlatformBindingExecutionStage.AcceptancePending
                or StudioMemberPlatformBindingExecutionStage.CommandInFlight
                or StudioMemberPlatformBindingExecutionStage.ReadinessInFlight);
    }

    private static bool HasMatchingPlatformFence(
        StudioMemberBindingRunState state,
        string platformBindingCommandId,
        int protocolVersion,
        int executionAttempt) =>
        HasMatchingPlatformProtocolAndCommand(state, platformBindingCommandId, protocolVersion)
        && state.PlatformExecutionAttempt == executionAttempt;

    private static bool HasMatchingPlatformProtocolAndCommand(
        StudioMemberBindingRunState state,
        string platformBindingCommandId,
        int protocolVersion) =>
        string.Equals(state.PlatformBindingCommandId, platformBindingCommandId, StringComparison.Ordinal)
        && state.PlatformBindingProtocolVersion == protocolVersion;

    private static bool HasSupportedPlatformBindingProtocol(StudioMemberBindingRunState state) =>
        state.PlatformBindingProtocolVersion == StudioMemberConventions.PlatformBindingProtocolVersion;

    private static bool IsReadinessExecutionStage(StudioMemberPlatformBindingExecutionStage stage) =>
        stage is StudioMemberPlatformBindingExecutionStage.ReadinessPending
            or StudioMemberPlatformBindingExecutionStage.ReadinessInFlight;

    private static bool IsValidExecutionStageTransition(
        StudioMemberPlatformBindingExecutionStage current,
        StudioMemberPlatformBindingExecutionStage next) =>
        (current == StudioMemberPlatformBindingExecutionStage.CommandPending
            && next == StudioMemberPlatformBindingExecutionStage.CommandInFlight)
        || (current is (
                StudioMemberPlatformBindingExecutionStage.ReadinessPending
                or StudioMemberPlatformBindingExecutionStage.ReadinessInFlight)
            && next == StudioMemberPlatformBindingExecutionStage.ReadinessInFlight);

    private bool CanRecoverRun() =>
        !string.IsNullOrEmpty(State.BindingRunId)
        && State.Request != null
        && State.Status switch
        {
            StudioMemberBindingRunStatus.AdmissionPending => true,
            StudioMemberBindingRunStatus.Admitted => State.Admitted != null,
            StudioMemberBindingRunStatus.PlatformBindingPending =>
                State.Admitted != null && !string.IsNullOrEmpty(State.PlatformBindingCommandId),
            StudioMemberBindingRunStatus.MemberNotificationPending =>
                State.PlatformResult != null || State.Failure != null,
            _ => false,
        };

    private Task SendAdmissionRequestAsync(CancellationToken ct = default) =>
        SendToAsync(
            StudioMemberConventions.BuildActorId(State.ScopeId, State.MemberId),
            new StudioMemberBindAdmissionRequested
            {
                BindingRunId = State.BindingRunId,
                ScopeId = State.ScopeId,
                MemberId = State.MemberId,
                RequestHash = State.RequestHash,
                Request = State.Request.Clone(),
                RequestedAtUtc = State.UpdatedAtUtc ?? State.AcceptedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            ct);

    private async Task SendAdmissionRequestAndScheduleWatchdogAsync(CancellationToken ct = default)
    {
        await ScheduleAdmissionWatchdogAsync(ct);
        await SendAdmissionRequestAsync(ct);
    }

    private Task ScheduleAdmissionWatchdogAsync(CancellationToken ct = default) =>
        ScheduleBindingRunRecoveryTimeoutAsync(
            BuildAdmissionWatchdogCallbackId(State.BindingRunId),
            AdmissionWatchdogDelay,
            new StudioMemberBindingAdmissionWatchdogFired
            {
                BindingRunId = State.BindingRunId,
            },
            ct: ct);

    private async Task SendPlatformBindingStartRequestedAsync(
        Timestamp? requestedAtUtc = null,
        CancellationToken ct = default)
    {
        if (State.Admitted == null)
            return;

        var platformBindingCommandId = State.Status == StudioMemberBindingRunStatus.PlatformBindingPending
            ? State.PlatformBindingCommandId
            : StudioMemberConventions.BuildPlatformBindingCommandId(
                State.BindingRunId,
                State.AttemptCount + 1);

        try
        {
            await SendToAsync(
                Id,
                new StudioMemberPlatformBindingExecutionStartRequested
                {
                    BindingRunId = State.BindingRunId,
                    PlatformBindingCommandId = platformBindingCommandId,
                    Request = State.Request.Clone(),
                    Admitted = State.Admitted.Clone(),
                    RequestedAtUtc = requestedAtUtc ?? State.UpdatedAtUtc
                        ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                    ExecutionAttempt = 0,
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new StudioMemberBindingRunRecoveryPublicationPendingException(
                "Committed admission still requires its platform-binding self continuation.",
                exception);
        }
    }

    private async Task SendPlatformBindingPendingAndExecuteAsync(
        Timestamp pendingAtUtc,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(State.PlatformBindingCommandId) || State.Admitted == null)
            return;

        await SchedulePlatformBindingExecuteRequestedAsync(
            PlatformBindingExecuteInitialDelay,
            ct);

        await SendToAsync(
            StudioMemberConventions.BuildActorId(State.ScopeId, State.MemberId),
            new StudioMemberBindingPlatformPendingEvent
            {
                BindingRunId = State.BindingRunId,
                PlatformBindingCommandId = State.PlatformBindingCommandId,
                PendingAtUtc = pendingAtUtc,
            },
            ct);
    }

    private async Task RecoverPlatformBindingPendingAsync(
        Timestamp pendingAtUtc,
        CancellationToken ct)
    {
        if (!HasSupportedPlatformBindingProtocol(State))
        {
            await FailCheckpointUnavailableAsync(ct);
            return;
        }

        if (IsReadinessExecutionStage(State.PlatformExecutionStage)
            && HasPlatformReadinessDeadlineExpired(DateTimeOffset.UtcNow))
        {
            await SendPlatformBindingWatchdogFiredAsync(ct);
            return;
        }

        switch (State.PlatformExecutionStage)
        {
            case StudioMemberPlatformBindingExecutionStage.AcceptancePending:
                await SendPlatformBindingStartRequestedAsync(pendingAtUtc, ct);
                return;
            case StudioMemberPlatformBindingExecutionStage.CommandPending:
            case StudioMemberPlatformBindingExecutionStage.ReadinessPending:
                await SchedulePlatformBindingExecuteRequestedAsync(PlatformBindingExecuteInitialDelay, ct);
                return;
            case StudioMemberPlatformBindingExecutionStage.CommandInFlight when IsPlatformExecutionStale():
                await FailCheckpointUnavailableAsync(ct);
                return;
            case StudioMemberPlatformBindingExecutionStage.ReadinessInFlight when IsPlatformExecutionStale():
                await SendPlatformBindingExecuteRequestedAsync(ct);
                return;
            case StudioMemberPlatformBindingExecutionStage.CommandInFlight:
            case StudioMemberPlatformBindingExecutionStage.ReadinessInFlight:
                await SchedulePlatformBindingWatchdogAsync(ct);
                return;
            default:
                await FailCheckpointUnavailableAsync(ct);
                return;
        }
    }

    private Task SchedulePlatformBindingExecuteRequestedAsync(
        TimeSpan dueTime,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(State.PlatformBindingCommandId) || State.Admitted == null)
            return Task.CompletedTask;

        return ScheduleBindingRunRecoveryTimeoutAsync(
            BuildPlatformBindingExecuteCallbackId(
                State.BindingRunId,
                State.PlatformBindingCommandId,
                State.PlatformBindingProtocolVersion,
                State.PlatformExecutionAttempt),
            dueTime,
            new StudioMemberPlatformBindingStageExecuteRequested
            {
                BindingRunId = State.BindingRunId,
                PlatformBindingCommandId = State.PlatformBindingCommandId,
                ProtocolVersion = State.PlatformBindingProtocolVersion,
                ExpectedExecutionAttempt = State.PlatformExecutionAttempt,
            },
            ct: ct);
    }

    private Task SendPlatformBindingExecuteRequestedAsync(CancellationToken ct = default) =>
        SendToAsync(
            Id,
            new StudioMemberPlatformBindingStageExecuteRequested
            {
                BindingRunId = State.BindingRunId,
                PlatformBindingCommandId = State.PlatformBindingCommandId,
                ProtocolVersion = State.PlatformBindingProtocolVersion,
                ExpectedExecutionAttempt = State.PlatformExecutionAttempt,
            },
            ct);

    private Task SendPlatformBindingWatchdogFiredAsync(CancellationToken ct = default) =>
        SendToAsync(
            Id,
            new StudioMemberPlatformBindingExecutionWatchdogFired
            {
                BindingRunId = State.BindingRunId,
                PlatformBindingCommandId = State.PlatformBindingCommandId,
                ProtocolVersion = State.PlatformBindingProtocolVersion,
                ExpectedExecutionAttempt = State.PlatformExecutionAttempt,
            },
            ct);

    private async Task FailPlatformReadinessTimeoutAsync(
        Timestamp? failedAtUtc = null,
        CancellationToken ct = default)
    {
        await PersistDomainEventAsync(new StudioMemberPlatformBindingExecutionFailed
        {
            BindingRunId = State.BindingRunId,
            PlatformBindingCommandId = State.PlatformBindingCommandId,
            ProtocolVersion = State.PlatformBindingProtocolVersion,
            ExecutionAttempt = State.PlatformExecutionAttempt,
            ExecutionStage = State.PlatformExecutionStage,
            Failure = new StudioMemberBindingFailure
            {
                Code = PlatformBindingReadinessTimeoutFailureCode,
                Message = PlatformBindingReadinessTimeoutFailureMessage,
                FailedAtUtc = failedAtUtc?.Clone()
                    ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        }, ct);
        await SendMemberTerminalNotificationAsync(ct);
    }

    private async Task FailCheckpointUnavailableAsync(CancellationToken ct = default)
    {
        var failure = new StudioMemberBindingFailure
        {
            Code = PlatformBindingCheckpointUnavailableFailureCode,
            Message = "platform binding command completion cannot be proven from committed state.",
            FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        if (State.PlatformBindingProtocolVersion == 0
            && State.PlatformExecutionAttempt == 0
            && State.PlatformExecutionStage == StudioMemberPlatformBindingExecutionStage.Unspecified)
        {
            await PersistDomainEventAsync(new StudioMemberPlatformBindingFailed
            {
                BindingRunId = State.BindingRunId,
                PlatformBindingCommandId = State.PlatformBindingCommandId,
                Failure = failure,
            }, ct);
            await SendMemberTerminalNotificationAsync(ct);
            return;
        }

        await PersistDomainEventsAsync(
        [
            BuildLegacyPlatformBindingStartFence(),
            BuildLegacyPlatformBindingExecutionFence(),
            new StudioMemberPlatformBindingExecutionFailed
            {
                BindingRunId = State.BindingRunId,
                PlatformBindingCommandId = State.PlatformBindingCommandId,
                ProtocolVersion = State.PlatformBindingProtocolVersion,
                ExecutionAttempt = State.PlatformExecutionAttempt,
                ExecutionStage = State.PlatformExecutionStage,
                Failure = failure,
            },
        ],
        ct);
        await SendMemberTerminalNotificationAsync(ct);
    }

    private async Task SendMemberTerminalNotificationAsync(CancellationToken ct = default)
    {
        if (State.Status != StudioMemberBindingRunStatus.MemberNotificationPending
            || (State.PlatformResult == null && State.Failure == null))
        {
            return;
        }

        var notificationAttempt = State.MemberNotificationAttempt + 1;
        await ScheduleMemberTerminalNotificationWatchdogAsync(notificationAttempt, ct);
        await PersistDomainEventAsync(new StudioMemberBindingTerminalNotificationAttemptStarted
        {
            BindingRunId = State.BindingRunId,
            NotificationAttempt = notificationAttempt,
            AttemptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        }, ct);

        if (State.PlatformResult != null)
        {
            await SendToAsync(
                StudioMemberConventions.BuildActorId(State.ScopeId, State.MemberId),
                new StudioMemberBindingCompletedEvent
                {
                    BindingRunId = State.BindingRunId,
                    MemberId = State.MemberId,
                    ScopeId = State.ScopeId,
                    PublishedServiceId = State.PlatformResult.PublishedServiceId,
                    RevisionId = State.PlatformResult.RevisionId,
                    ImplementationKind = State.PlatformResult.ImplementationKind,
                    ImplementationRef = State.PlatformResult.ImplementationRef?.Clone(),
                    ExpectedActorId = State.PlatformResult.ExpectedActorId,
                    CompletedAtUtc = State.UpdatedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
                ct);
            return;
        }

        if (State.Failure != null)
        {
            await SendToAsync(
                StudioMemberConventions.BuildActorId(State.ScopeId, State.MemberId),
                new StudioMemberBindingFailedEvent
                {
                    BindingRunId = State.BindingRunId,
                    MemberId = State.MemberId,
                    ScopeId = State.ScopeId,
                    Failure = State.Failure.Clone(),
                },
                ct);
        }
    }

    private Task ScheduleMemberTerminalNotificationWatchdogAsync(
        int notificationAttempt,
        CancellationToken ct = default) =>
        ScheduleBindingRunRecoveryTimeoutAsync(
            BuildMemberTerminalNotificationWatchdogCallbackId(
                State.BindingRunId,
                notificationAttempt),
            MemberTerminalNotificationWatchdogDelay,
            new StudioMemberBindingTerminalNotificationWatchdogFired
            {
                BindingRunId = State.BindingRunId,
                ExpectedNotificationAttempt = notificationAttempt,
            },
            ct: ct);

    private Task SchedulePlatformBindingWatchdogAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(State.PlatformBindingCommandId) || State.Admitted == null)
            return Task.CompletedTask;

        return ScheduleBindingRunRecoveryTimeoutAsync(
            BuildPlatformBindingWatchdogCallbackId(
                State.BindingRunId,
                State.PlatformBindingCommandId,
                State.PlatformBindingProtocolVersion,
                State.PlatformExecutionAttempt),
            PlatformBindingWatchdogDelay,
            new StudioMemberPlatformBindingExecutionWatchdogFired
            {
                BindingRunId = State.BindingRunId,
                PlatformBindingCommandId = State.PlatformBindingCommandId,
                ProtocolVersion = State.PlatformBindingProtocolVersion,
                ExpectedExecutionAttempt = State.PlatformExecutionAttempt,
            },
            ct: ct);
    }

    private async Task RunWithPlatformBindingWatchdogAsync(
        Func<Task> operation,
        CancellationToken ct)
    {
        Exception? operationFailure = null;
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        try
        {
            await SchedulePlatformBindingWatchdogAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception scheduleFailure) when (operationFailure != null)
        {
            throw new StudioMemberBindingRunRecoverySchedulePendingException(
                "Platform binding execution failed and its durable recovery watchdog could not be scheduled.",
                new AggregateException(operationFailure, scheduleFailure));
        }

        if (operationFailure != null)
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
    }

    private async Task<RuntimeCallbackLease> ScheduleBindingRunRecoveryTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default)
    {
        try
        {
            return await ScheduleSelfDurableTimeoutAsync(
                callbackId,
                dueTime,
                evt,
                options,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new StudioMemberBindingRunRecoverySchedulePendingException(
                "Committed binding-run progress still requires a durable recovery callback.",
                exception);
        }
    }

    private static bool IsSameRequest(
        StudioMemberBindingRequest? current,
        StudioMemberBindingRequest incoming,
        string currentHash)
    {
        if (current == null)
            return false;

        if (!string.IsNullOrWhiteSpace(currentHash)
            && !string.IsNullOrWhiteSpace(incoming.RequestHash)
            && !string.Equals(currentHash, incoming.RequestHash, StringComparison.Ordinal))
        {
            return false;
        }

        var normalizedCurrent = current.Clone();
        normalizedCurrent.RequestHash = string.Empty;
        var normalizedIncoming = incoming.Clone();
        normalizedIncoming.RequestHash = string.Empty;
        return normalizedCurrent.Equals(normalizedIncoming);
    }

    private static string BuildPlatformBindingExecuteCallbackId(
        string bindingRunId,
        string platformBindingCommandId,
        int protocolVersion,
        int expectedExecutionAttempt) =>
        $"studio-member-binding-execute:v{protocolVersion}:a{expectedExecutionAttempt}:{bindingRunId}:{platformBindingCommandId}";

    private static Timestamp BuildPlatformReadinessDeadline(
        StudioMemberBindingRunState state,
        Timestamp? eventAtUtc)
    {
        var committedAnchor = eventAtUtc
            ?? state.PlatformExecutionStageStartedAtUtc
            ?? state.UpdatedAtUtc
            ?? state.AcceptedAtUtc;
        if (committedAnchor == null)
            return Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch);

        return Timestamp.FromDateTimeOffset(
            committedAnchor.ToDateTimeOffset().Add(PlatformBindingReadinessBudget));
    }

    private bool HasPlatformReadinessDeadlineExpired(DateTimeOffset observedAtUtc)
    {
        var deadline = State.PlatformReadinessDeadlineAtUtc?.ToDateTimeOffset()
            ?? State.UpdatedAtUtc?.ToDateTimeOffset().Add(PlatformBindingReadinessBudget)
            ?? State.PlatformExecutionStageStartedAtUtc?.ToDateTimeOffset().Add(PlatformBindingReadinessBudget)
            ?? State.AcceptedAtUtc?.ToDateTimeOffset().Add(PlatformBindingReadinessBudget)
            ?? DateTimeOffset.UnixEpoch;
        return observedAtUtc >= deadline;
    }

    private static string BuildAdmissionWatchdogCallbackId(string bindingRunId) =>
        $"studio-member-binding-admission-watchdog:{bindingRunId}";

    private static string BuildMemberTerminalNotificationWatchdogCallbackId(
        string bindingRunId,
        int notificationAttempt) =>
        $"studio-member-binding-terminal-notification-watchdog:a{notificationAttempt}:{bindingRunId}";

    private static string BuildPlatformBindingWatchdogCallbackId(
        string bindingRunId,
        string platformBindingCommandId,
        int protocolVersion,
        int expectedExecutionAttempt) =>
        $"studio-member-binding-watchdog:v{protocolVersion}:a{expectedExecutionAttempt}:{bindingRunId}:{platformBindingCommandId}";
}
