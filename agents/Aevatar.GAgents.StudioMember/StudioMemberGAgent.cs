using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Per-member actor that owns the canonical StudioMember authority state.
///
/// Actor ID convention: <c>studio-member:{scopeId}:{memberId}</c>.
/// The actor is the only writer of <c>published_service_id</c>, which is
/// generated once at creation from the immutable <c>member_id</c> and never
/// recomputed on rename. The convention is re-derived inside the actor in
/// <see cref="ApplyCreated"/> so a stale or hand-crafted event payload
/// cannot break the rename-safe invariant.
/// </summary>
public sealed class StudioMemberGAgent : GAgentBase<StudioMemberState>, IProjectedActor
{
    public static string ProjectionKind => "studio-member";

    [EventHandler(EndpointName = "createMember")]
    public async Task HandleCreated(StudioMemberCreatedEvent evt)
    {
        if (!string.IsNullOrEmpty(State.MemberId))
        {
            // First-write-wins on identity: a re-create with a different
            // memberId is a hard conflict (someone is reusing an existing
            // actor id for a different member). A re-create with the same
            // memberId but mismatched non-identity fields is also rejected
            // so a stray duplicate cannot silently overwrite the persisted
            // displayName / kind / description and leave callers confused
            // about which version persisted.
            if (!string.Equals(State.MemberId, evt.MemberId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"member already initialized with id '{State.MemberId}'.");
            }

            if (!string.Equals(State.DisplayName, evt.DisplayName, StringComparison.Ordinal)
                || !string.Equals(State.Description, evt.Description, StringComparison.Ordinal)
                || State.ImplementationKind != evt.ImplementationKind)
            {
                throw new InvalidOperationException(
                    $"member '{State.MemberId}' already exists with different displayName / description / implementationKind. " +
                    "First-write-wins on member identity; use rename / updateImplementation to change later.");
            }

            // Same memberId + same identity-stable fields = idempotent no-op.
            return;
        }

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "renameMember")]
    public async Task HandleRenamed(StudioMemberRenamedEvent evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "updateImplementation")]
    public async Task HandleImplementationUpdated(StudioMemberImplementationUpdatedEvent evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }

        // ImplementationKind is locked at create. Reject mismatched kinds so
        // a Script member can't be silently mutated into a Workflow member by
        // dispatching an UpdatedEvent with a different kind. Unspecified is
        // accepted as "carry the existing kind" (defensive default).
        if (evt.ImplementationKind != StudioMemberImplementationKind.Unspecified
            && evt.ImplementationKind != State.ImplementationKind)
        {
            throw new InvalidOperationException(
                $"member '{State.MemberId}' implementationKind is locked at create. " +
                $"Was {State.ImplementationKind}, attempted {evt.ImplementationKind}. " +
                "Use create with the correct kind, or rename / impl-update with the same kind.");
        }

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "requestBindingAdmission")]
    public async Task HandleBindingAdmissionRequested(StudioMemberBindAdmissionRequested evt)
    {
        var runActorId = StudioMemberConventions.BuildBindingRunActorId(evt.BindingRunId);
        var failedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        if (string.IsNullOrEmpty(State.MemberId))
        {
            await SendToAsync(runActorId, BuildRejected(evt, "STUDIO_MEMBER_NOT_FOUND", "member not yet created.", failedAt));
            return;
        }

        if (!string.Equals(State.ScopeId, evt.ScopeId, StringComparison.Ordinal)
            || !string.Equals(State.MemberId, evt.MemberId, StringComparison.Ordinal))
        {
            await SendToAsync(runActorId, BuildRejected(evt, "STUDIO_MEMBER_TARGET_MISMATCH", "binding admission target does not match member authority state.", failedAt));
            return;
        }

        var requestedKind = GetRequestImplementationKind(evt.Request);
        if (requestedKind != State.ImplementationKind)
        {
            var rejected = BuildRejected(
                evt,
                "STUDIO_MEMBER_IMPLEMENTATION_KIND_MISMATCH",
                $"binding request kind '{requestedKind}' does not match member kind '{State.ImplementationKind}'.",
                failedAt);
            await PersistDomainEventsAsync([evt, rejected]);
            await SendToAsync(runActorId, rejected);
            return;
        }

        var admitted = new StudioMemberBindingAdmittedEvent
        {
            BindingRunId = evt.BindingRunId,
            ScopeId = State.ScopeId,
            MemberId = State.MemberId,
            PublishedServiceId = State.PublishedServiceId,
            ImplementationKind = State.ImplementationKind,
            DisplayName = State.DisplayName,
            AdmittedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        await PersistDomainEventsAsync([evt, admitted]);
        await SendToAsync(runActorId, admitted);
    }

    [EventHandler(EndpointName = "markBindingPlatformPending")]
    public async Task HandleBindingPlatformPending(StudioMemberBindingPlatformPendingEvent evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "completeBinding")]
    public async Task HandleBindingCompleted(StudioMemberBindingCompletedEvent evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "failBinding")]
    public async Task HandleBindingFailed(StudioMemberBindingFailedEvent evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }

        await PersistDomainEventAsync(evt);
    }

    /// <summary>
    /// Mutates the member's team assignment (ADR-0017 Locked Rule 3).
    /// The single event shape covers assign / unassign / move; from/to are
    /// proto3 <c>optional string</c> so absence means "unassigned".
    ///
    /// from_team_id must agree with the current state.team_id — this guards
    /// against stale or hand-crafted events committing against a roster the
    /// member no longer claims to be on. The destination TeamGAgents are
    /// dispatched the same event by the application command port and apply
    /// idempotent set operations to their own roster.
    /// </summary>
    [EventHandler(EndpointName = "reassignTeam")]
    public async Task HandleReassigned(StudioMemberReassignedEvent evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }

        if (!string.Equals(State.ScopeId, evt.ScopeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"member '{State.MemberId}' (scope {State.ScopeId}) cannot accept reassignment in scope {evt.ScopeId}.");
        }

        // At least one side must be present; otherwise the event has no semantic effect.
        if (!evt.HasFromTeamId && !evt.HasToTeamId)
        {
            throw new InvalidOperationException(
                "reassign event must carry at least one of from_team_id / to_team_id.");
        }

        // Both present and equal is a no-op move — reject so the wire never carries it.
        if (evt.HasFromTeamId && evt.HasToTeamId
            && string.Equals(evt.FromTeamId, evt.ToTeamId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "from_team_id and to_team_id must differ when both are present.");
        }

        // Empty-string check (defensive — wire layer should already reject).
        if (evt.HasFromTeamId && string.IsNullOrEmpty(evt.FromTeamId))
        {
            throw new InvalidOperationException(
                "from_team_id must not be empty when present.");
        }
        if (evt.HasToTeamId && string.IsNullOrEmpty(evt.ToTeamId))
        {
            throw new InvalidOperationException(
                "to_team_id must not be empty when present.");
        }

        // from_team_id must reflect the current assignment so the event is
        // a real transition relative to this actor's authority. Idempotent
        // replays of the same transition are accepted (state already matches
        // the to_team_id).
        var currentTeam = State.HasTeamId ? State.TeamId : null;
        var fromTeam = evt.HasFromTeamId ? evt.FromTeamId : null;
        var toTeam = evt.HasToTeamId ? evt.ToTeamId : null;

        if (!string.Equals(currentTeam, fromTeam, StringComparison.Ordinal))
        {
            // Allow idempotent replay: if the state already matches the
            // destination, swallow the event without persisting.
            if (string.Equals(currentTeam, toTeam, StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException(
                $"member '{State.MemberId}' current team_id is '{currentTeam ?? "<unassigned>"}' but " +
                $"reassign event names from_team_id '{fromTeam ?? "<unassigned>"}'.");
        }

        await PersistDomainEventAsync(evt);
    }

    protected override StudioMemberState TransitionState(
        StudioMemberState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<StudioMemberCreatedEvent>(ApplyCreated)
            .On<StudioMemberRenamedEvent>(ApplyRenamed)
            .On<StudioMemberImplementationUpdatedEvent>(ApplyImplementationUpdated)
            .On<StudioMemberBindAdmissionRequested>(ApplyBindingAdmissionRequested)
            .On<StudioMemberBindingAdmittedEvent>(ApplyBindingAdmitted)
            .On<StudioMemberBindingRejectedEvent>(ApplyBindingRejected)
            .On<StudioMemberBindingPlatformPendingEvent>(ApplyBindingPlatformPending)
            .On<StudioMemberBindingCompletedEvent>(ApplyBindingCompleted)
            .On<StudioMemberBindingFailedEvent>(ApplyBindingFailed)
            .On<StudioMemberReassignedEvent>(ApplyReassigned)
            .OrCurrent();
    }

    private static StudioMemberState ApplyCreated(
        StudioMemberState state, StudioMemberCreatedEvent evt)
    {
        // Re-derive publishedServiceId from the immutable memberId rather
        // than trusting evt.PublishedServiceId. The dispatcher today already
        // builds it via the same convention; deriving here keeps the
        // single-source-of-truth on the actor and protects against a
        // historical or hand-rolled event whose derivation rule drifted.
        var derivedPublishedServiceId = StudioMemberConventions.BuildPublishedServiceId(evt.MemberId);

        return new StudioMemberState
        {
            MemberId = evt.MemberId,
            ScopeId = evt.ScopeId,
            DisplayName = evt.DisplayName,
            Description = evt.Description,
            ImplementationKind = evt.ImplementationKind,
            ImplementationRef = null,
            PublishedServiceId = derivedPublishedServiceId,
            LifecycleStage = StudioMemberLifecycleStage.Created,
            CreatedAtUtc = evt.CreatedAtUtc,
            UpdatedAtUtc = evt.CreatedAtUtc,
            LastBinding = null,
        };
    }

    private static StudioMemberState ApplyRenamed(
        StudioMemberState state, StudioMemberRenamedEvent evt)
    {
        var next = state.Clone();
        next.DisplayName = evt.DisplayName;
        next.Description = evt.Description;
        next.UpdatedAtUtc = evt.UpdatedAtUtc;
        return next;
    }

    private static StudioMemberState ApplyBindingAdmissionRequested(
        StudioMemberState state,
        StudioMemberBindAdmissionRequested evt)
    {
        var next = state.Clone();
        next.Binding = new StudioMemberBindingAuthorityState
        {
            CurrentBindingRunId = evt.BindingRunId,
            CurrentStatus = StudioMemberBindingRunStatus.AdmissionPending,
            LastTerminalBindingRunId = next.Binding?.LastTerminalBindingRunId ?? string.Empty,
            LastFailure = next.Binding?.LastFailure?.Clone(),
            UpdatedAtUtc = evt.RequestedAtUtc,
        };
        next.UpdatedAtUtc = evt.RequestedAtUtc;
        return next;
    }

    private static StudioMemberState ApplyBindingAdmitted(
        StudioMemberState state,
        StudioMemberBindingAdmittedEvent evt)
    {
        if (IsStaleBindingRun(state, evt.BindingRunId))
            return state;

        var next = state.Clone();
        next.Binding = new StudioMemberBindingAuthorityState
        {
            CurrentBindingRunId = evt.BindingRunId,
            CurrentStatus = StudioMemberBindingRunStatus.Admitted,
            LastTerminalBindingRunId = next.Binding?.LastTerminalBindingRunId ?? string.Empty,
            LastFailure = next.Binding?.LastFailure?.Clone(),
            UpdatedAtUtc = evt.AdmittedAtUtc,
        };
        next.UpdatedAtUtc = evt.AdmittedAtUtc;
        return next;
    }

    private static StudioMemberState ApplyBindingRejected(
        StudioMemberState state,
        StudioMemberBindingRejectedEvent evt)
    {
        if (IsStaleBindingRun(state, evt.BindingRunId))
            return state;

        var failedAt = evt.Failure?.FailedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var next = state.Clone();
        next.Binding = new StudioMemberBindingAuthorityState
        {
            CurrentBindingRunId = evt.BindingRunId,
            CurrentStatus = StudioMemberBindingRunStatus.Rejected,
            LastTerminalBindingRunId = evt.BindingRunId,
            LastFailure = evt.Failure?.Clone(),
            UpdatedAtUtc = failedAt,
        };
        next.UpdatedAtUtc = failedAt;
        return next;
    }

    private static StudioMemberState ApplyImplementationUpdated(
        StudioMemberState state, StudioMemberImplementationUpdatedEvent evt)
    {
        var next = state.Clone();
        // ImplementationKind is locked at create — see HandleImplementationUpdated.
        // Do not mutate it here even if the event payload disagrees, so the
        // invariant holds even on hand-rolled / replayed events.
        next.ImplementationRef = evt.ImplementationRef?.Clone();
        next.UpdatedAtUtc = evt.UpdatedAtUtc;

        // Lifecycle:
        //   Created       + resolved impl ref → BuildReady
        //   BindReady     + new impl event    → downgrade to BuildReady
        //                  (the published revision is now stale until next bind)
        //   BuildReady    + new impl event    → stays BuildReady
        //
        // The bind orchestration explicitly does (impl_updated → bound),
        // so the temporary downgrade is upgraded again by ApplyBound on
        // the same bind; only out-of-band impl updates leave the member
        // visibly non-bind-ready until rebind.
        var hasResolvedRef = HasResolvedImplementationRef(evt.ImplementationRef);
        if (hasResolvedRef)
        {
            next.LifecycleStage = StudioMemberLifecycleStage.BuildReady;
        }
        else if (next.LifecycleStage == StudioMemberLifecycleStage.BindReady)
        {
            // Cleared impl ref on a previously-bound member: still need to
            // surface that the bound revision is stale.
            next.LifecycleStage = StudioMemberLifecycleStage.BuildReady;
        }

        return next;
    }

    private static StudioMemberState ApplyBindingPlatformPending(
        StudioMemberState state,
        StudioMemberBindingPlatformPendingEvent evt)
    {
        var next = state.Clone();
        next.Binding = new StudioMemberBindingAuthorityState
        {
            CurrentBindingRunId = evt.BindingRunId,
            CurrentStatus = StudioMemberBindingRunStatus.PlatformBindingPending,
            LastTerminalBindingRunId = next.Binding?.LastTerminalBindingRunId ?? string.Empty,
            LastFailure = next.Binding?.LastFailure?.Clone(),
            UpdatedAtUtc = evt.PendingAtUtc,
        };
        next.UpdatedAtUtc = evt.PendingAtUtc;
        return next;
    }

    private static StudioMemberState ApplyBindingCompleted(
        StudioMemberState state, StudioMemberBindingCompletedEvent evt)
    {
        if (IsStaleBindingRun(state, evt.BindingRunId))
            return state;

        var next = state.Clone();
        next.LastBinding = new StudioMemberBindingContract
        {
            PublishedServiceId = evt.PublishedServiceId,
            RevisionId = evt.RevisionId,
            ImplementationKind = evt.ImplementationKind,
            BoundAtUtc = evt.CompletedAtUtc,
        };
        if (HasResolvedImplementationRef(evt.ImplementationRef))
        {
            next.ImplementationRef = evt.ImplementationRef.Clone();
        }
        next.Binding = new StudioMemberBindingAuthorityState
        {
            CurrentBindingRunId = evt.BindingRunId,
            CurrentStatus = StudioMemberBindingRunStatus.Succeeded,
            LastTerminalBindingRunId = evt.BindingRunId,
            LastFailure = null,
            UpdatedAtUtc = evt.CompletedAtUtc,
        };
        next.LifecycleStage = StudioMemberLifecycleStage.BindReady;
        next.UpdatedAtUtc = evt.CompletedAtUtc;
        return next;
    }

    private static StudioMemberState ApplyBindingFailed(
        StudioMemberState state,
        StudioMemberBindingFailedEvent evt)
    {
        if (IsStaleBindingRun(state, evt.BindingRunId))
            return state;

        var failedAt = evt.Failure?.FailedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var next = state.Clone();
        next.Binding = new StudioMemberBindingAuthorityState
        {
            CurrentBindingRunId = evt.BindingRunId,
            CurrentStatus = StudioMemberBindingRunStatus.Failed,
            LastTerminalBindingRunId = evt.BindingRunId,
            LastFailure = evt.Failure?.Clone(),
            UpdatedAtUtc = failedAt,
        };
        next.UpdatedAtUtc = failedAt;
        return next;
    }

    private static bool IsStaleBindingRun(StudioMemberState state, string bindingRunId)
    {
        var currentRun = state.Binding?.CurrentBindingRunId;
        return !string.IsNullOrEmpty(currentRun)
               && !string.Equals(currentRun, bindingRunId, StringComparison.Ordinal);
    }

    private static StudioMemberState ApplyReassigned(
        StudioMemberState state, StudioMemberReassignedEvent evt)
    {
        var next = state.Clone();
        if (evt.HasToTeamId)
        {
            next.TeamId = evt.ToTeamId;
        }
        else
        {
            next.ClearTeamId();
        }
        next.UpdatedAtUtc = evt.ReassignedAtUtc;
        return next;
    }

    private static bool HasResolvedImplementationRef(StudioMemberImplementationRef? implRef)
    {
        if (implRef == null)
            return false;

        if (implRef.Workflow != null && !string.IsNullOrEmpty(implRef.Workflow.WorkflowId))
            return true;
        if (implRef.Script != null && !string.IsNullOrEmpty(implRef.Script.ScriptId))
            return true;
        if (implRef.Gagent != null && !string.IsNullOrEmpty(implRef.Gagent.ActorTypeName))
            return true;

        return false;
    }

    private static StudioMemberImplementationKind GetRequestImplementationKind(StudioMemberBindingRequest request) =>
        request.ImplementationCase switch
        {
            StudioMemberBindingRequest.ImplementationOneofCase.Workflow => StudioMemberImplementationKind.Workflow,
            StudioMemberBindingRequest.ImplementationOneofCase.Script => StudioMemberImplementationKind.Script,
            StudioMemberBindingRequest.ImplementationOneofCase.Gagent => StudioMemberImplementationKind.Gagent,
            _ => StudioMemberImplementationKind.Unspecified,
        };

    private static StudioMemberBindingRejectedEvent BuildRejected(
        StudioMemberBindAdmissionRequested evt,
        string code,
        string message,
        Timestamp failedAt) =>
        new()
        {
            BindingRunId = evt.BindingRunId,
            ScopeId = evt.ScopeId,
            MemberId = evt.MemberId,
            Failure = new StudioMemberBindingFailure
            {
                Code = code,
                Message = message,
                FailedAtUtc = failedAt,
            },
        };
}
