using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Short-lived actor for one StudioMember binding attempt.
/// </summary>
public sealed class StudioMemberBindingRunGAgent : GAgentBase<StudioMemberBindingRunState>, IProjectedActor
{
    public static string ProjectionKind => "studio-member-binding-run";

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);

        if (State.Status == StudioMemberBindingRunStatus.PlatformBindingPending
            && !string.IsNullOrEmpty(State.BindingRunId)
            && !string.IsNullOrEmpty(State.PlatformBindingCommandId)
            && State.Request != null
            && State.Admitted != null)
        {
            await SendToAsync(
                Id,
                new StudioMemberPlatformBindingExecuteRequested
                {
                    BindingRunId = State.BindingRunId,
                    PlatformBindingCommandId = State.PlatformBindingCommandId,
                },
                ct);
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

            return;
        }

        await PersistDomainEventAsync(evt);
        await SendToAsync(
            StudioMemberConventions.BuildActorId(evt.Request.ScopeId, evt.Request.MemberId),
            new StudioMemberBindAdmissionRequested
            {
                BindingRunId = evt.Request.BindingRunId,
                ScopeId = evt.Request.ScopeId,
                MemberId = evt.Request.MemberId,
                RequestHash = evt.Request.RequestHash,
                Request = evt.Request.Clone(),
                RequestedAtUtc = evt.RequestedAtUtc,
            });
    }

    [EventHandler(EndpointName = "admitBindingRun")]
    public async Task HandleAdmitted(StudioMemberBindingAdmittedEvent evt)
    {
        if (!CanAcceptRunEvent(evt.BindingRunId))
            return;

        await PersistDomainEventAsync(evt);
        await SendToAsync(
            Id,
            new StudioMemberPlatformBindingStartRequested
            {
                BindingRunId = evt.BindingRunId,
                PlatformBindingCommandId = BuildPlatformBindingCommandId(evt.BindingRunId, State.AttemptCount + 1),
                Request = State.Request.Clone(),
                Admitted = State.Admitted.Clone(),
                RequestedAtUtc = evt.AdmittedAtUtc,
            });
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
        if (!CanAcceptRunEvent(evt.BindingRunId))
            return;

        await PersistDomainEventAsync(evt);

        var platformBindingPort = Services.GetService<IStudioMemberPlatformBindingCommandPort>();
        if (platformBindingPort == null)
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

        var accepted = await platformBindingPort.StartAsync(Id, evt);
        await SendToAsync(Id, accepted);
    }

    [EventHandler(EndpointName = "acceptPlatformBinding", AllowSelfHandling = true)]
    public async Task HandlePlatformBindingAccepted(StudioMemberPlatformBindingAccepted evt)
    {
        if (!CanAcceptRunEvent(evt.BindingRunId))
            return;

        await PersistDomainEventAsync(evt);
        await SendToAsync(
            Id,
            new StudioMemberPlatformBindingExecuteRequested
            {
                BindingRunId = evt.BindingRunId,
                PlatformBindingCommandId = evt.PlatformBindingCommandId,
            });
    }

    [EventHandler(EndpointName = "executePlatformBinding", AllowSelfHandling = true)]
    public async Task HandlePlatformBindingExecuteRequested(StudioMemberPlatformBindingExecuteRequested evt)
    {
        if (!CanAcceptRunEvent(evt.BindingRunId))
            return;

        if (!string.Equals(State.PlatformBindingCommandId, evt.PlatformBindingCommandId, StringComparison.Ordinal))
            return;

        var platformBindingPort = Services.GetService<IStudioMemberPlatformBindingCommandPort>();
        if (platformBindingPort == null)
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

        await platformBindingPort.ExecuteAsync(
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
    }

    [EventHandler(EndpointName = "completePlatformBinding")]
    public async Task HandlePlatformBindingSucceeded(StudioMemberPlatformBindingSucceeded evt)
    {
        if (!CanAcceptRunEvent(evt.BindingRunId))
            return;

        await PersistDomainEventAsync(evt);
        await SendToAsync(
            StudioMemberConventions.BuildActorId(State.ScopeId, State.MemberId),
            new StudioMemberBindingCompletedEvent
            {
                BindingRunId = evt.BindingRunId,
                PublishedServiceId = evt.Result.PublishedServiceId,
                RevisionId = evt.Result.RevisionId,
                ImplementationKind = evt.Result.ImplementationKind,
                ImplementationRef = evt.Result.ImplementationRef?.Clone(),
                CompletedAtUtc = evt.CompletedAtUtc,
            });
    }

    [EventHandler(EndpointName = "failPlatformBinding", AllowSelfHandling = true)]
    public async Task HandlePlatformBindingFailed(StudioMemberPlatformBindingFailed evt)
    {
        if (!CanAcceptRunEvent(evt.BindingRunId))
            return;

        await PersistDomainEventAsync(evt);
        await SendToAsync(
            StudioMemberConventions.BuildActorId(State.ScopeId, State.MemberId),
            new StudioMemberBindingFailedEvent
            {
                BindingRunId = evt.BindingRunId,
                Failure = evt.Failure?.Clone(),
            });
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
            .On<StudioMemberPlatformBindingSucceeded>(ApplyPlatformBindingSucceeded)
            .On<StudioMemberPlatformBindingFailed>(ApplyPlatformBindingFailed)
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
        if (IsStale(state, evt.BindingRunId) || IsTerminal(state.Status))
            return state;

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
        if (IsStale(state, evt.BindingRunId) || IsTerminal(state.Status))
            return state;

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.PlatformBindingPending;
        next.PlatformBindingCommandId = evt.PlatformBindingCommandId;
        next.AttemptCount++;
        next.UpdatedAtUtc = evt.RequestedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyPlatformBindingAccepted(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingAccepted evt)
    {
        if (IsStale(state, evt.BindingRunId) || IsTerminal(state.Status))
            return state;

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.PlatformBindingPending;
        next.PlatformBindingCommandId = evt.PlatformBindingCommandId;
        next.UpdatedAtUtc = evt.AcceptedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyPlatformBindingSucceeded(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingSucceeded evt)
    {
        if (IsStale(state, evt.BindingRunId) || IsTerminal(state.Status))
            return state;

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.Succeeded;
        next.PlatformResult = evt.Result?.Clone();
        next.UpdatedAtUtc = evt.CompletedAtUtc;
        return next;
    }

    private static StudioMemberBindingRunState ApplyPlatformBindingFailed(
        StudioMemberBindingRunState state,
        StudioMemberPlatformBindingFailed evt)
    {
        if (IsStale(state, evt.BindingRunId) || IsTerminal(state.Status))
            return state;

        var next = state.Clone();
        next.Status = StudioMemberBindingRunStatus.Failed;
        next.Failure = evt.Failure?.Clone();
        if (evt.Failure?.FailedAtUtc != null)
            next.UpdatedAtUtc = evt.Failure.FailedAtUtc;
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

    private static string BuildPlatformBindingCommandId(string bindingRunId, int attempt) =>
        $"platform-{bindingRunId}-{attempt}";
}
