using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Short-lived actor for one StudioMember binding attempt.
/// </summary>
[GAgent("studio.member-binding-run")]
public sealed class StudioMemberBindingRunGAgent : GAgentBase<StudioMemberBindingRunState>, IProjectedActor
{
    private static readonly TimeSpan PlatformBindingExecuteInitialDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PlatformBindingWatchdogDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PlatformBindingExecutionStaleAfter = TimeSpan.FromMinutes(2);
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
                await SendAdmissionRequestAsync(ct);
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

            return;
        }

        await PersistDomainEventAsync(evt);
        await SendAdmissionRequestAsync();
    }

    [EventHandler(EndpointName = "admitBindingRun")]
    public async Task HandleAdmitted(StudioMemberBindingAdmittedEvent evt)
    {
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
    public async Task HandlePlatformBindingStartRequested(StudioMemberPlatformBindingStartRequested evt)
    {
        if (!CanAcceptPlatformBindingStart(evt.BindingRunId))
            return;

        await PersistDomainEventAsync(evt);

        if (_platformBindingPort == null)
        {
            await SendToAsync(Id, new StudioMemberPlatformBindingFailed
            {
                BindingRunId = evt.BindingRunId,
                PlatformBindingCommandId = evt.PlatformBindingCommandId,
                Failure = new StudioMemberBindingFailure
                {
                    Code = "STUDIO_MEMBER_PLATFORM_BINDING_PORT_UNAVAILABLE",
                    Message = "studio member platform binding command port is not registered.",
                    FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            });
            return;
        }

        var accepted = await _platformBindingPort.StartAsync(Id, evt);
        await SendToAsync(Id, accepted);
    }

    [EventHandler(EndpointName = "acceptPlatformBinding", AllowSelfHandling = true)]
    public async Task HandlePlatformBindingAccepted(StudioMemberPlatformBindingAccepted evt)
    {
        if (!CanAcceptPlatformBindingAccepted(evt.BindingRunId, evt.PlatformBindingCommandId))
            return;

        await PersistDomainEventAsync(evt);
        await SendPlatformBindingPendingAndExecuteAsync(evt.AcceptedAtUtc);
    }

    [EventHandler(EndpointName = "executePlatformBinding", AllowSelfHandling = true)]
    public async Task HandlePlatformBindingExecuteRequested(StudioMemberPlatformBindingExecuteRequested evt)
    {
        if (!CanAcceptPlatformBindingCommand(evt.BindingRunId, evt.PlatformBindingCommandId))
            return;

        if (State.PlatformExecutionInFlight && !evt.RecoveryExecution)
            return;

        await PersistDomainEventAsync(new StudioMemberPlatformBindingExecutionStarted
        {
            BindingRunId = evt.BindingRunId,
            PlatformBindingCommandId = evt.PlatformBindingCommandId,
            StartedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        if (_platformBindingPort == null)
        {
            await SendToAsync(Id, new StudioMemberPlatformBindingFailed
            {
                BindingRunId = evt.BindingRunId,
                PlatformBindingCommandId = evt.PlatformBindingCommandId,
                Failure = new StudioMemberBindingFailure
                {
                    Code = "STUDIO_MEMBER_PLATFORM_BINDING_PORT_UNAVAILABLE",
                    Message = "studio member platform binding command port is not registered.",
                    FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            });
            return;
        }

        await _platformBindingPort.ExecuteAsync(
            Id,
            evt.PlatformBindingCommandId,
            new StudioMemberPlatformBindingStartRequested
            {
                BindingRunId = State.BindingRunId,
                PlatformBindingCommandId = State.PlatformBindingCommandId,
                Request = State.Request.Clone(),
                Admitted = State.Admitted.Clone(),
                RequestedAtUtc = State.UpdatedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });

        await SchedulePlatformBindingWatchdogAsync();
    }

    [EventHandler(EndpointName = "platformBindingWatchdog", AllowSelfHandling = true)]
    public async Task HandlePlatformBindingWatchdogFired(StudioMemberPlatformBindingWatchdogFired evt)
    {
        if (!CanAcceptPlatformBindingCommand(evt.BindingRunId, evt.PlatformBindingCommandId))
            return;

        if (!State.PlatformExecutionInFlight)
        {
            await SendToAsync(Id, new StudioMemberPlatformBindingExecuteRequested
            {
                BindingRunId = evt.BindingRunId,
                PlatformBindingCommandId = evt.PlatformBindingCommandId,
            });
            return;
        }

        if (IsPlatformExecutionStale())
        {
            await SendToAsync(Id, new StudioMemberPlatformBindingExecuteRequested
            {
                BindingRunId = evt.BindingRunId,
                PlatformBindingCommandId = evt.PlatformBindingCommandId,
                RecoveryExecution = true,
            });
            return;
        }

        await SchedulePlatformBindingWatchdogAsync();
    }

    [EventHandler(EndpointName = "completePlatformBinding")]
    public async Task HandlePlatformBindingSucceeded(StudioMemberPlatformBindingSucceeded evt)
    {
        if (!CanAcceptPlatformBindingResult(evt.BindingRunId, evt.PlatformBindingCommandId))
            return;

        await PersistDomainEventAsync(evt);
        await SendMemberTerminalNotificationAsync();
    }

    [EventHandler(EndpointName = "failPlatformBinding", AllowSelfHandling = true)]
    public async Task HandlePlatformBindingFailed(StudioMemberPlatformBindingFailed evt)
    {
        if (!CanAcceptPlatformBindingResult(evt.BindingRunId, evt.PlatformBindingCommandId))
            return;

        await PersistDomainEventAsync(evt);
        await SendMemberTerminalNotificationAsync();
    }

    [EventHandler(EndpointName = "acknowledgeMemberBindingTerminal", AllowSelfHandling = true)]
    public async Task HandleMemberBindingTerminalAcknowledged(StudioMemberBindingTerminalAcknowledged evt)
    {
        if (!CanAcceptMemberTerminalAcknowledgement(evt.BindingRunId, evt.Status))
            return;

        await PersistDomainEventAsync(evt);
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
            .On<StudioMemberPlatformBindingStartRequested>(ApplyPlatformBindingStartRequested)
            .On<StudioMemberPlatformBindingAccepted>(ApplyPlatformBindingAccepted)
            .On<StudioMemberPlatformBindingExecutionStarted>(ApplyPlatformBindingExecutionStarted)
            .On<StudioMemberPlatformBindingSucceeded>(ApplyPlatformBindingSucceeded)
            .On<StudioMemberPlatformBindingFailed>(ApplyPlatformBindingFailed)
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
        next.AttemptCount++;
        next.UpdatedAtUtc = evt.RequestedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyPlatformBindingAccepted(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingAccepted evt)
    {
        if (IsStale(state, evt.BindingRunId)
            || state.Status != StudioMemberBindingRunStatus.PlatformBindingPending
            || state.PlatformExecutionInFlight
            || !string.Equals(state.PlatformBindingCommandId, evt.PlatformBindingCommandId, StringComparison.Ordinal))
        {
            return state;
        }

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.PlatformBindingPending;
        next.PlatformBindingCommandId = evt.PlatformBindingCommandId;
        next.PlatformExecutionInFlight = false;
        next.PlatformExecutionStartedAtUtc = null;
        next.UpdatedAtUtc = evt.AcceptedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyPlatformBindingExecutionStarted(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingExecutionStarted evt)
    {
        if (IsStale(state, evt.BindingRunId)
            || state.Status != StudioMemberBindingRunStatus.PlatformBindingPending
            || !string.Equals(state.PlatformBindingCommandId, evt.PlatformBindingCommandId, StringComparison.Ordinal))
        {
            return state;
        }

        var next = state.Clone();
        next.PlatformExecutionInFlight = true;
        next.PlatformExecutionStartedAtUtc = evt.StartedAtUtc;
        next.UpdatedAtUtc = evt.StartedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyPlatformBindingSucceeded(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingSucceeded evt)
    {
        if (!CanApplyPlatformBindingResult(state, evt.BindingRunId, evt.PlatformBindingCommandId))
            return state;

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.MemberNotificationPending;
        next.PlatformResult = evt.Result?.Clone();
        next.PlatformExecutionInFlight = false;
        next.PlatformExecutionStartedAtUtc = null;
        next.UpdatedAtUtc = evt.CompletedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyPlatformBindingFailed(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingFailed evt)
    {
        if (!CanApplyPlatformBindingResult(state, evt.BindingRunId, evt.PlatformBindingCommandId))
            return state;

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.MemberNotificationPending;
        next.Failure = evt.Failure?.Clone();
        next.PlatformExecutionInFlight = false;
        next.PlatformExecutionStartedAtUtc = null;
        if (evt.Failure?.FailedAtUtc != null)
            next.UpdatedAtUtc = evt.Failure.FailedAtUtc;
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

    private bool CanAcceptRunEvent(string bindingRunId) =>
        !string.IsNullOrEmpty(State.BindingRunId)
        && string.Equals(State.BindingRunId, bindingRunId, StringComparison.Ordinal)
        && !IsTerminal(State.Status);

    private bool CanAcceptAdmission(string bindingRunId) =>
        !string.IsNullOrEmpty(State.BindingRunId)
        && string.Equals(State.BindingRunId, bindingRunId, StringComparison.Ordinal)
        && State.Status == StudioMemberBindingRunStatus.AdmissionPending;

    private bool CanAcceptPlatformBindingStart(string bindingRunId) =>
        !string.IsNullOrEmpty(State.BindingRunId)
        && string.Equals(State.BindingRunId, bindingRunId, StringComparison.Ordinal)
        && State.Status == StudioMemberBindingRunStatus.Admitted;

    private bool CanAcceptPlatformBindingAccepted(string bindingRunId, string platformBindingCommandId) =>
        !string.IsNullOrEmpty(State.BindingRunId)
        && string.Equals(State.BindingRunId, bindingRunId, StringComparison.Ordinal)
        && State.Status == StudioMemberBindingRunStatus.PlatformBindingPending
        && !State.PlatformExecutionInFlight
        && string.Equals(State.PlatformBindingCommandId, platformBindingCommandId, StringComparison.Ordinal);

    private bool CanAcceptPlatformBindingResult(string bindingRunId, string platformBindingCommandId) =>
        CanAcceptPlatformBindingCommand(bindingRunId, platformBindingCommandId);

    private bool CanAcceptPlatformBindingCommand(string bindingRunId, string platformBindingCommandId) =>
        !string.IsNullOrEmpty(State.BindingRunId)
        && string.Equals(State.BindingRunId, bindingRunId, StringComparison.Ordinal)
        && State.Status == StudioMemberBindingRunStatus.PlatformBindingPending
        && string.Equals(State.PlatformBindingCommandId, platformBindingCommandId, StringComparison.Ordinal);

    private bool CanAcceptMemberTerminalAcknowledgement(string bindingRunId, StudioMemberBindingRunStatus status) =>
        !string.IsNullOrEmpty(State.BindingRunId)
        && string.Equals(State.BindingRunId, bindingRunId, StringComparison.Ordinal)
        && State.Status == StudioMemberBindingRunStatus.MemberNotificationPending
        && (status == StudioMemberBindingRunStatus.Succeeded || status == StudioMemberBindingRunStatus.Failed)
        && ((status == StudioMemberBindingRunStatus.Succeeded && State.PlatformResult != null)
            || (status == StudioMemberBindingRunStatus.Failed && State.Failure != null));

    private bool IsPlatformExecutionStale()
    {
        if (!State.PlatformExecutionInFlight || State.PlatformExecutionStartedAtUtc == null)
            return false;

        var startedAt = State.PlatformExecutionStartedAtUtc.ToDateTimeOffset();
        return DateTimeOffset.UtcNow - startedAt >= PlatformBindingExecutionStaleAfter;
    }

    private static bool CanApplyPlatformBindingResult(
        StudioMemberBindingRunState state,
        string bindingRunId,
        string platformBindingCommandId) =>
        !string.IsNullOrEmpty(state.BindingRunId)
        && string.Equals(state.BindingRunId, bindingRunId, StringComparison.Ordinal)
        && state.Status == StudioMemberBindingRunStatus.PlatformBindingPending
        && string.Equals(state.PlatformBindingCommandId, platformBindingCommandId, StringComparison.Ordinal);

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
                State.Admitted != null
                && !string.IsNullOrEmpty(State.PlatformBindingCommandId)
                && (State.PlatformResult != null || State.Failure != null),
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

    private Task SendPlatformBindingStartRequestedAsync(Timestamp? requestedAtUtc = null, CancellationToken ct = default)
    {
        if (State.Admitted == null)
            return Task.CompletedTask;

        return SendToAsync(
            Id,
            new StudioMemberPlatformBindingStartRequested
            {
                BindingRunId = State.BindingRunId,
                PlatformBindingCommandId = StudioMemberConventions.BuildPlatformBindingCommandId(
                    State.BindingRunId,
                    State.AttemptCount + 1),
                Request = State.Request.Clone(),
                Admitted = State.Admitted.Clone(),
                RequestedAtUtc = requestedAtUtc ?? State.UpdatedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            ct);
    }

    private async Task SendPlatformBindingPendingAndExecuteAsync(
        Timestamp pendingAtUtc,
        bool recoveryExecution = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(State.PlatformBindingCommandId) || State.Admitted == null)
            return;

        await SchedulePlatformBindingExecuteRequestedAsync(
            PlatformBindingExecuteInitialDelay,
            recoveryExecution,
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
        if (State.PlatformExecutionInFlight && !IsPlatformExecutionStale())
        {
            await SchedulePlatformBindingWatchdogAsync(ct);
            return;
        }

        await SendPlatformBindingPendingAndExecuteAsync(
            pendingAtUtc,
            recoveryExecution: true,
            ct);
    }

    private Task SchedulePlatformBindingExecuteRequestedAsync(
        TimeSpan dueTime,
        bool recoveryExecution = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(State.PlatformBindingCommandId) || State.Admitted == null)
            return Task.CompletedTask;

        return ScheduleSelfDurableTimeoutAsync(
            BuildPlatformBindingExecuteCallbackId(State.BindingRunId, State.PlatformBindingCommandId),
            dueTime,
            new StudioMemberPlatformBindingExecuteRequested
            {
                BindingRunId = State.BindingRunId,
                PlatformBindingCommandId = State.PlatformBindingCommandId,
                RecoveryExecution = recoveryExecution,
            },
            ct: ct);
    }

    private Task SendMemberTerminalNotificationAsync(CancellationToken ct = default)
    {
        if (State.PlatformResult != null)
        {
            return SendToAsync(
                StudioMemberConventions.BuildActorId(State.ScopeId, State.MemberId),
                new StudioMemberBindingCompletedEvent
                {
                    BindingRunId = State.BindingRunId,
                    PublishedServiceId = State.PlatformResult.PublishedServiceId,
                    RevisionId = State.PlatformResult.RevisionId,
                    ImplementationKind = State.PlatformResult.ImplementationKind,
                    ImplementationRef = State.PlatformResult.ImplementationRef?.Clone(),
                    ExpectedActorId = State.PlatformResult.ExpectedActorId,
                    CompletedAtUtc = State.UpdatedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
                ct);
        }

        if (State.Failure != null)
        {
            return SendToAsync(
                StudioMemberConventions.BuildActorId(State.ScopeId, State.MemberId),
                new StudioMemberBindingFailedEvent
                {
                    BindingRunId = State.BindingRunId,
                    Failure = State.Failure.Clone(),
                },
                ct);
        }

        return Task.CompletedTask;
    }

    private Task SchedulePlatformBindingWatchdogAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(State.PlatformBindingCommandId) || State.Admitted == null)
            return Task.CompletedTask;

        return ScheduleSelfDurableTimeoutAsync(
            BuildPlatformBindingWatchdogCallbackId(State.BindingRunId, State.PlatformBindingCommandId),
            PlatformBindingWatchdogDelay,
            new StudioMemberPlatformBindingWatchdogFired
            {
                BindingRunId = State.BindingRunId,
                PlatformBindingCommandId = State.PlatformBindingCommandId,
            },
            ct: ct);
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
        string platformBindingCommandId) =>
        $"studio-member-binding-execute:{bindingRunId}:{platformBindingCommandId}";

    private static string BuildPlatformBindingWatchdogCallbackId(
        string bindingRunId,
        string platformBindingCommandId) =>
        $"studio-member-binding-watchdog:{bindingRunId}:{platformBindingCommandId}";
}
