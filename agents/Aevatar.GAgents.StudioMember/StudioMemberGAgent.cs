using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
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
[GAgent("studio.member")]
public sealed class StudioMemberGAgent : GAgentBase<StudioMemberState>, IProjectedActor
{
    private const string MemberDeletedFailureCode = "STUDIO_MEMBER_DELETED";
    private static readonly TimeSpan ScheduleProvisioningInitialDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ScheduleProvisioningRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScheduleProvisioningAttemptWatchdogDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScheduleProvisioningBudget = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ScheduleProvisioningOneShotMinimumLeadTime = TimeSpan.FromSeconds(10);
    private readonly IStudioMemberWorkflowScheduleProvisioningPort? _scheduleProvisioningPort;

    public static string ProjectionKind => "studio-member";

    public StudioMemberGAgent(
        IStudioMemberWorkflowScheduleProvisioningPort? scheduleProvisioningPort = null)
    {
        _scheduleProvisioningPort = scheduleProvisioningPort;
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        if (TryBuildCommittedDeleteBindingTermination(State, out var termination))
        {
            await SendBindingAuthorityTerminationAsync(termination, ct);
            return;
        }

        if (CanRecoverScheduleProvisioning())
            await ScheduleWorkflowScheduleProvisioningAttemptAsync(ScheduleProvisioningInitialDelay, ct);
    }

    // Refactor (iter1345/cluster-519-draft-member-authority):
    //   Old pattern: workflow draft saves could leave member authority creation
    //   to API-side orchestration or read-model freshness assumptions.
    //   New principle: committed draft facts enter projection fanout, then the
    //   standard actor command path asks this member actor to own creation
    //   idempotently from its authoritative state.
    [EventHandler(EndpointName = "ensureMember")]
    public async Task HandleEnsureStudioMember(EnsureStudioMember command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!string.IsNullOrEmpty(State.MemberId))
        {
            if (!string.Equals(State.MemberId, command.MemberId, StringComparison.Ordinal)
                || !string.Equals(State.ScopeId, command.ScopeId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"member already initialized as '{State.ScopeId}/{State.MemberId}'.");
            }

            return;
        }

        await PersistDomainEventAsync(new StudioMemberCreatedEvent
        {
            MemberId = command.MemberId,
            ScopeId = command.ScopeId,
            DisplayName = command.DisplayName,
            Description = command.Description,
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = StudioMemberConventions.BuildPublishedServiceId(command.MemberId),
            CreatedAtUtc = command.RequestedAtUtc
                ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
    }

    [EventHandler(EndpointName = "createMember")]
    public async Task HandleCreated(StudioMemberCreatedEvent evt)
    {
        if (State.Deleted)
        {
            throw new InvalidOperationException(
                $"member '{State.MemberId}' has been deleted and cannot be recreated.");
        }

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
        if (State.Deleted)
        {
            throw new InvalidOperationException("member has been deleted.");
        }

        var renamed = evt.Clone();
        renamed.MemberId = State.MemberId;
        renamed.ScopeId = State.ScopeId;
        if (string.IsNullOrEmpty(renamed.Description))
            renamed.Description = State.Description;
        if (renamed.UpdatedAtUtc == null)
            renamed.UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        await PersistDomainEventAsync(renamed);
    }

    [EventHandler(EndpointName = "updateImplementation")]
    public async Task HandleImplementationUpdated(StudioMemberImplementationUpdatedEvent evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }
        if (State.Deleted)
        {
            throw new InvalidOperationException("member has been deleted.");
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

        var updated = evt.Clone();
        updated.MemberId = State.MemberId;
        updated.ScopeId = State.ScopeId;
        await PersistDomainEventAsync(updated);
    }

    [EventHandler(EndpointName = "requestBindingAdmission")]
    public async Task HandleBindingAdmissionRequested(StudioMemberBindAdmissionRequested evt)
    {
        var runActorId = StudioMemberConventions.BuildBindingRunActorId(evt.BindingRunId);
        var failedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        if (string.IsNullOrEmpty(State.MemberId))
        {
            await SendBindingRejectionAsync(
                runActorId,
                BuildRejected(evt, "STUDIO_MEMBER_NOT_FOUND", "member not yet created.", failedAt));
            return;
        }
        if (State.Deleted)
        {
            await SendBindingRejectionAsync(
                runActorId,
                BuildRejected(evt, "STUDIO_MEMBER_NOT_FOUND", "member has been deleted.", failedAt));
            return;
        }

        if (!string.Equals(State.ScopeId, evt.ScopeId, StringComparison.Ordinal)
            || !string.Equals(State.MemberId, evt.MemberId, StringComparison.Ordinal))
        {
            await SendBindingRejectionAsync(
                runActorId,
                BuildRejected(
                    evt,
                    "STUDIO_MEMBER_TARGET_MISMATCH",
                    "binding admission target does not match member authority state.",
                    failedAt));
            return;
        }

        if (TryBuildTerminalBindingRunReplayResponse(State, evt, failedAt, out var terminalReplayResponse))
        {
            if (terminalReplayResponse is StudioMemberBindingRejectedEvent terminalRejection)
                await FailWorkflowScheduleProvisioningForBindingRejectionAsync(terminalRejection);
            await SendToAsync(runActorId, terminalReplayResponse);
            return;
        }

        if (IsTerminalBindingRunReplay(State, evt.BindingRunId))
        {
            return;
        }

        if (HasActiveBindingRun(State, evt.BindingRunId))
        {
            await SendBindingRejectionAsync(
                runActorId,
                BuildRejected(
                    evt,
                    "STUDIO_MEMBER_BINDING_RUN_ALREADY_ACTIVE",
                    "member already has an active binding run.",
                    failedAt));
            return;
        }

        if (IsSupersededBindingRun(State, evt.BindingRunId, evt.RequestedAtUtc))
        {
            await SendBindingRejectionAsync(
                runActorId,
                BuildRejected(
                    evt,
                    "STUDIO_MEMBER_BINDING_RUN_SUPERSEDED",
                    "binding run was superseded by a newer member binding run.",
                    failedAt));
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
            await SendBindingRejectionAsync(runActorId, rejected);
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
        if (State.Deleted)
        {
            throw new InvalidOperationException("member has been deleted.");
        }

        if (!CanAcceptBindingRunProgress(State, evt.BindingRunId))
        {
            return;
        }

        if (State.Binding?.CurrentStatus == StudioMemberBindingRunStatus.PlatformBindingPending
            && string.Equals(State.Binding.CurrentBindingRunId, evt.BindingRunId, StringComparison.Ordinal))
        {
            return;
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
        if (TryBuildCommittedDeleteBindingTermination(State, out var termination)
            && string.Equals(termination.BindingRunId, evt.BindingRunId, StringComparison.Ordinal))
        {
            await SendBindingAuthorityTerminationAsync(termination);
            return;
        }
        if (IsTerminalBindingRunReplay(State, evt.BindingRunId, StudioMemberBindingRunStatus.Succeeded))
        {
            await SendTerminalAcknowledgementAsync(evt.BindingRunId, StudioMemberBindingRunStatus.Succeeded);
            return;
        }

        if (State.Deleted)
        {
            throw new InvalidOperationException("member has been deleted.");
        }

        if (!CanAcceptBindingRunProgress(State, evt.BindingRunId))
        {
            return;
        }

        var completed = evt.Clone();
        completed.MemberId = State.MemberId;
        completed.ScopeId = State.ScopeId;
        await PersistDomainEventAsync(completed);
        await ScheduleWorkflowScheduleProvisioningIfReadyAsync();
        await SendTerminalAcknowledgementAsync(evt.BindingRunId, StudioMemberBindingRunStatus.Succeeded);
    }

    [EventHandler(EndpointName = "failBinding")]
    public async Task HandleBindingFailed(StudioMemberBindingFailedEvent evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }
        if (IsTerminalBindingRunReplay(State, evt.BindingRunId, StudioMemberBindingRunStatus.Failed))
        {
            if (TryBuildCommittedDeleteBindingTermination(State, out var termination)
                && !termination.Failure.Equals(evt.Failure))
            {
                await SendBindingAuthorityTerminationAsync(termination);
                return;
            }

            await SendTerminalAcknowledgementAsync(evt.BindingRunId, StudioMemberBindingRunStatus.Failed);
            return;
        }

        if (State.Deleted)
        {
            throw new InvalidOperationException("member has been deleted.");
        }

        if (!CanAcceptBindingRunProgress(State, evt.BindingRunId))
        {
            return;
        }

        var failed = evt.Clone();
        failed.MemberId = State.MemberId;
        failed.ScopeId = State.ScopeId;
        await PersistDomainEventAsync(failed);
        if (ShouldFailScheduleProvisioningForBindingRun(evt.BindingRunId))
        {
            await FailWorkflowScheduleProvisioningAsync(
                evt.Failure?.Code ?? "workflow_binding_failed",
                evt.Failure?.Message ?? "Workflow binding failed before schedule provisioning.");
        }
        await SendTerminalAcknowledgementAsync(evt.BindingRunId, StudioMemberBindingRunStatus.Failed);
    }

    [EventHandler(EndpointName = "recordPublishedBinding")]
    public async Task HandlePublishedBindingRecorded(StudioMemberPublishedBindingRecordedEvent evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }

        if (!string.Equals(State.PublishedServiceId, evt.PublishedServiceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"publishedServiceId '{evt.PublishedServiceId}' does not match member '{State.MemberId}' publishedServiceId '{State.PublishedServiceId}'.");
        }

        if (evt.ImplementationKind != State.ImplementationKind)
        {
            throw new InvalidOperationException(
                $"binding record kind '{evt.ImplementationKind}' does not match member kind '{State.ImplementationKind}'.");
        }

        if (!HasResolvedImplementationRef(evt.ImplementationRef, evt.ImplementationKind))
        {
            throw new InvalidOperationException("published binding record must include a resolved implementation reference for its implementation kind.");
        }

        await PersistDomainEventAsync(evt);
        await ScheduleWorkflowScheduleProvisioningIfReadyAsync();
    }

    [EventHandler(EndpointName = "requestWorkflowScheduleProvisioning")]
    public async Task HandleWorkflowScheduleProvisioningRequested(
        StudioMemberWorkflowScheduleProvisioningRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var intent = command.Intent ?? throw new InvalidOperationException("schedule provisioning intent is required.");
        ValidateScheduleProvisioningIntent(intent);

        var current = State.WorkflowScheduleProvisioning;
        if (current?.Intent != null &&
            string.Equals(current.Intent.ProvisioningId, intent.ProvisioningId, StringComparison.Ordinal))
        {
            if (!current.Intent.Equals(intent))
                throw new InvalidOperationException("schedule provisioning intent payload conflict.");

            if (CanRecoverScheduleProvisioning())
                await ScheduleWorkflowScheduleProvisioningIfReadyAsync();
            return;
        }

        var requested = command.Clone();
        requested.RequestedAtUtc ??= Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await PersistDomainEventAsync(requested);

        if (HasConflictingActiveBindingRun(intent.BindingRunId))
        {
            await FailWorkflowScheduleProvisioningAsync(
                "STUDIO_MEMBER_BINDING_RUN_ALREADY_ACTIVE",
                "member already has an active binding run.");
            return;
        }

        if (ShouldFailScheduleProvisioningForBindingRun(intent.BindingRunId))
        {
            await FailWorkflowScheduleProvisioningAsync(
                State.Binding?.LastFailure?.Code ?? "workflow_binding_failed",
                State.Binding?.LastFailure?.Message ?? "Workflow binding failed before schedule provisioning.");
            return;
        }

        await ScheduleWorkflowScheduleProvisioningIfReadyAsync();
    }

    [EventHandler(EndpointName = "attemptWorkflowScheduleProvisioning", AllowSelfHandling = true)]
    public async Task HandleWorkflowScheduleProvisioningAttemptRequested(
        StudioMemberWorkflowScheduleProvisioningAttemptRequested command)
    {
        if (!CanAcceptScheduleProvisioningContinuation(command.ProvisioningId) ||
            command.ObservedAttempt != State.WorkflowScheduleProvisioning!.AttemptCount ||
            !IsTargetScheduleBindingObserved())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (IsScheduleProvisioningDeadlineReached(now))
        {
            await FailWorkflowScheduleProvisioningAsync(
                "workflow_schedule_provisioning_timeout",
                "Workflow schedule provisioning did not complete before its deadline.");
            return;
        }

        var provisioning = State.WorkflowScheduleProvisioning!;
        if (ShouldRefreshScheduleProvisioningOneShotTiming(provisioning, now))
        {
            await PersistDomainEventAsync(new StudioMemberWorkflowScheduleProvisioningTimingResolved
            {
                ProvisioningId = command.ProvisioningId,
                OneShotFireAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(
                    ResolveScheduleProvisioningOneShotDelaySeconds(provisioning.Intent))),
                ResolvedAtUtc = Timestamp.FromDateTimeOffset(now),
            });
            provisioning = State.WorkflowScheduleProvisioning!;
        }

        if (_scheduleProvisioningPort == null)
        {
            await FailWorkflowScheduleProvisioningAsync(
                "workflow_schedule_provisioning_port_unavailable",
                "Workflow schedule provisioning port is not registered.");
            return;
        }

        var attempt = provisioning.AttemptCount + 1;
        await PersistDomainEventAsync(new StudioMemberWorkflowScheduleProvisioningAttemptStarted
        {
            ProvisioningId = command.ProvisioningId,
            Attempt = attempt,
            StartedAtUtc = Timestamp.FromDateTimeOffset(now),
        });

        StudioMemberWorkflowScheduleProvisioningExecutionAccepted accepted;
        try
        {
            accepted = await _scheduleProvisioningPort.ExecuteAsync(
                Id,
                State.WorkflowScheduleProvisioning!.Intent.Clone(),
                State.WorkflowScheduleProvisioning.ResolvedOneShotFireAtUtc?.ToDateTimeOffset(),
                attempt,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await PersistDomainEventAsync(new StudioMemberWorkflowScheduleProvisioningRetryDeferred
            {
                ProvisioningId = command.ProvisioningId,
                Attempt = attempt,
                FailureCode = "workflow_schedule_provisioning_dispatch_failed",
                Detail = ex.GetType().Name,
                DeferredAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });
            await ScheduleWorkflowScheduleProvisioningAttemptAsync(
                ScheduleProvisioningRetryDelay,
                CancellationToken.None);
            return;
        }

        if (!string.Equals(accepted.ProvisioningId, command.ProvisioningId, StringComparison.Ordinal) ||
            accepted.Attempt != attempt)
        {
            await FailWorkflowScheduleProvisioningAsync(
                "workflow_schedule_provisioning_receipt_invalid",
                "Workflow schedule provisioning execution receipt did not match the active attempt.");
            return;
        }
        await ScheduleWorkflowScheduleProvisioningAttemptAsync(
            ScheduleProvisioningAttemptWatchdogDelay,
            CancellationToken.None);
    }

    [EventHandler(EndpointName = "deferWorkflowScheduleProvisioning", AllowSelfHandling = true)]
    public async Task HandleWorkflowScheduleProvisioningRetryDeferred(
        StudioMemberWorkflowScheduleProvisioningRetryDeferred continuation)
    {
        if (!CanAcceptScheduleProvisioningExecutionContinuation(
                continuation.ProvisioningId,
                continuation.Attempt))
            return;

        var deferred = continuation.Clone();
        deferred.DeferredAtUtc ??= Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await PersistDomainEventAsync(deferred);
        if (IsScheduleProvisioningDeadlineReached(DateTimeOffset.UtcNow))
        {
            await FailWorkflowScheduleProvisioningAsync(
                "workflow_schedule_provisioning_timeout",
                continuation.Detail);
            return;
        }

        await ScheduleWorkflowScheduleProvisioningAttemptAsync(ScheduleProvisioningRetryDelay);
    }

    [EventHandler(EndpointName = "completeWorkflowScheduleProvisioning", AllowSelfHandling = true)]
    public async Task HandleWorkflowScheduleProvisioningSucceeded(
        StudioMemberWorkflowScheduleProvisioningSucceeded continuation)
    {
        if (!CanAcceptScheduleProvisioningExecutionContinuation(
                continuation.ProvisioningId,
                continuation.Attempt))
            return;
        if (string.IsNullOrWhiteSpace(continuation.ScheduleId))
            throw new InvalidOperationException("schedule_id is required for provisioning success.");

        var completed = continuation.Clone();
        completed.CompletedAtUtc ??= Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await PersistDomainEventAsync(completed);
    }

    [EventHandler(EndpointName = "failWorkflowScheduleProvisioning", AllowSelfHandling = true)]
    public async Task HandleWorkflowScheduleProvisioningFailed(
        StudioMemberWorkflowScheduleProvisioningFailed continuation)
    {
        if (!CanAcceptScheduleProvisioningExecutionContinuation(
                continuation.ProvisioningId,
                continuation.Attempt))
            return;
        await PersistDomainEventAsync(continuation);
    }

    /// <summary>
    /// Mutates the member's team assignment (ADR-0017 Locked Rule 3).
    /// The single event shape covers assign / unassign / move; from/to are
    /// proto3 <c>optional string</c> so absence means "unassigned".
    ///
    /// from_team_id must agree with the current state.team_id — this guards
    /// against stale or hand-crafted events committing against a roster the
    /// member no longer claims to be on. Durable TeamGAgent fanout is driven
    /// by this committed event through the projection materialization actor.
    /// </summary>
    [EventHandler(EndpointName = "reassignTeam")]
    public async Task HandleReassigned(StudioMemberReassignedEvent evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }
        if (State.Deleted)
        {
            throw new InvalidOperationException("member has been deleted.");
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

    /// <summary>
    /// Tombstones the member authority and emits a committed removal fact for
    /// any current team assignment. Published service artifacts and revisions
    /// remain untouched; their lifecycle belongs to the platform service
    /// authority, not this member resource delete path.
    /// </summary>
    [EventHandler(EndpointName = "deleteMember")]
    public async Task HandleDeleteRequested(StudioMemberDeleteRequested evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }

        if (!string.Equals(State.ScopeId, evt.ScopeId, StringComparison.Ordinal)
            || !string.Equals(State.MemberId, evt.MemberId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "delete target does not match member authority state.");
        }

        if (State.Deleted)
        {
            if (IsRuntimeEnvelopeRedelivery()
                && TryBuildCommittedDeleteBindingTermination(State, out var replayTermination))
            {
                await SendBindingAuthorityTerminationAsync(replayTermination);
            }
            return;
        }

        var deletedAt = evt.RequestedAtUtc
            ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var deleted = new StudioMemberDeletedEvent
        {
            MemberId = State.MemberId,
            ScopeId = State.ScopeId,
            PublishedServiceId = State.PublishedServiceId ?? string.Empty,
            DeletedAtUtc = deletedAt,
        };
        if (State.HasTeamId)
            deleted.PreviousTeamId = State.TeamId;

        if (TryBuildDeleteBindingFailure(State, deletedAt, out var bindingFailed))
        {
            await PersistDomainEventsAsync([bindingFailed, deleted]);
            await SendBindingAuthorityTerminationAsync(
                BuildBindingAuthorityTermination(State, bindingFailed));
            return;
        }

        await PersistDomainEventAsync(deleted);
    }

    /// <summary>
    /// Evaluates PATCH team-assignment intent inside the member authority
    /// boundary. Callers provide only the desired target; this actor derives
    /// the current source team from <see cref="State"/>, suppresses no-ops,
    /// commits the resulting reassignment event. Team roster fanout is driven
    /// later by the durable committed-state materializer.
    /// </summary>
    // Refactor (iter96/cluster-545):
    //   Old pattern: member actor 直发 team(部分失败不可 replay).
    //   New principle: 只 persist committed event,fanout 由 StudioTeamRosterFanoutMaterializer 物化(committed-state idempotent).
    [EventHandler(EndpointName = "patchTeamAssignment")]
    public async Task HandleTeamAssignmentPatchRequested(StudioMemberTeamAssignmentPatchRequested evt)
    {
        if (string.IsNullOrEmpty(State.MemberId))
        {
            throw new InvalidOperationException("member not yet created.");
        }
        if (State.Deleted)
        {
            throw new InvalidOperationException("member has been deleted.");
        }

        if (!string.Equals(State.ScopeId, evt.ScopeId, StringComparison.Ordinal)
            || !string.Equals(State.MemberId, evt.MemberId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "team assignment patch target does not match member authority state.");
        }

        if (evt.HasTargetTeamId && string.IsNullOrWhiteSpace(evt.TargetTeamId))
        {
            throw new InvalidOperationException(
                "target_team_id must not be empty when present.");
        }

        var currentTeam = State.HasTeamId ? State.TeamId : null;
        var targetTeam = evt.HasTargetTeamId ? NormalizeActorIdSegment(evt.TargetTeamId, "target_team_id") : null;
        if (string.Equals(currentTeam, targetTeam, StringComparison.Ordinal))
        {
            return;
        }

        var reassigned = new StudioMemberReassignedEvent
        {
            MemberId = State.MemberId,
            ScopeId = State.ScopeId,
            ReassignedAtUtc = evt.RequestedAtUtc
                ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        if (currentTeam != null)
            reassigned.FromTeamId = currentTeam;
        if (targetTeam != null)
            reassigned.ToTeamId = targetTeam;

        await PersistDomainEventAsync(reassigned);
    }

    protected override StudioMemberState TransitionState(
        StudioMemberState current, IMessage evt)
    {
        var next = StateTransitionMatcher
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
            .On<StudioMemberPublishedBindingRecordedEvent>(ApplyPublishedBindingRecorded)
            .On<StudioMemberWorkflowScheduleProvisioningRequested>(ApplyWorkflowScheduleProvisioningRequested)
            .On<StudioMemberWorkflowScheduleProvisioningTimingResolved>(ApplyWorkflowScheduleProvisioningTimingResolved)
            .On<StudioMemberWorkflowScheduleProvisioningAttemptStarted>(ApplyWorkflowScheduleProvisioningAttemptStarted)
            .On<StudioMemberWorkflowScheduleProvisioningRetryDeferred>(ApplyWorkflowScheduleProvisioningRetryDeferred)
            .On<StudioMemberWorkflowScheduleProvisioningSucceeded>(ApplyWorkflowScheduleProvisioningSucceeded)
            .On<StudioMemberWorkflowScheduleProvisioningFailed>(ApplyWorkflowScheduleProvisioningFailed)
            .On<StudioMemberReassignedEvent>(ApplyReassigned)
            .On<StudioMemberDeletedEvent>(ApplyDeleted)
            .OrCurrent();

        // Legacy actor states predate authorization_revision. A real state
        // transition upgrades their raw zero to the baseline epoch without
        // changing the effective authorization stamp used by readers.
        if (!ReferenceEquals(next, current))
        {
            if (next.AuthorizationRevision < 0)
                throw new InvalidOperationException("member authorization_revision is invalid.");
            if (next.AuthorizationRevision == 0)
                next.AuthorizationRevision = 1;
        }

        return next;
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
            ImplementationRef = evt.ImplementationRef?.Clone(),
            PublishedServiceId = derivedPublishedServiceId,
            LifecycleStage = HasResolvedImplementationRef(evt.ImplementationRef)
                ? StudioMemberLifecycleStage.BuildReady
                : StudioMemberLifecycleStage.Created,
            CreatedAtUtc = evt.CreatedAtUtc,
            UpdatedAtUtc = evt.CreatedAtUtc,
            LastBinding = null,
            AuthorizationRevision = 1,
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
        if (ShouldIgnoreBindingRunStart(state, evt.BindingRunId, evt.RequestedAtUtc))
            return state;

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
        if (!CanAcceptBindingRunProgress(state, evt.BindingRunId))
            return state;

        var currentStatus = state.Binding?.CurrentStatus ?? StudioMemberBindingRunStatus.Unspecified;
        if (currentStatus == StudioMemberBindingRunStatus.PlatformBindingPending)
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
        if (!CanAcceptBindingRunProgress(state, evt.BindingRunId))
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
        next.AuthorizationRevision = AdvanceAuthorizationRevision(state.AuthorizationRevision);

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
        if (!CanAcceptBindingRunProgress(state, evt.BindingRunId))
            return state;

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
        if (!CanAcceptBindingRunProgress(state, evt.BindingRunId))
            return state;

        var next = state.Clone();
        next.LastBinding = new StudioMemberBindingContract
        {
            PublishedServiceId = evt.PublishedServiceId,
            RevisionId = evt.RevisionId,
            ImplementationKind = evt.ImplementationKind,
            BoundAtUtc = evt.CompletedAtUtc,
            ExpectedActorId = ResolveExpectedActorId(evt),
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
        next.AuthorizationRevision = AdvanceAuthorizationRevision(state.AuthorizationRevision);
        return next;
    }

    private static StudioMemberState ApplyBindingFailed(
        StudioMemberState state,
        StudioMemberBindingFailedEvent evt)
    {
        if (!CanAcceptBindingRunProgress(state, evt.BindingRunId))
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

    private static StudioMemberState ApplyPublishedBindingRecorded(
        StudioMemberState state,
        StudioMemberPublishedBindingRecordedEvent evt)
    {
        if (string.IsNullOrEmpty(state.MemberId))
            return state;

        var recordedAt = evt.RecordedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var next = state.Clone();
        next.LastBinding = new StudioMemberBindingContract
        {
            PublishedServiceId = evt.PublishedServiceId,
            RevisionId = evt.RevisionId,
            ImplementationKind = evt.ImplementationKind,
            BoundAtUtc = recordedAt,
            ExpectedActorId = evt.ExpectedActorId ?? string.Empty,
        };
        next.ImplementationRef = evt.ImplementationRef?.Clone();
        next.Binding = new StudioMemberBindingAuthorityState
        {
            CurrentBindingRunId = string.Empty,
            CurrentStatus = StudioMemberBindingRunStatus.Unspecified,
            LastTerminalBindingRunId = state.Binding?.LastTerminalBindingRunId ?? string.Empty,
            LastFailure = null,
            UpdatedAtUtc = recordedAt,
        };
        next.LifecycleStage = StudioMemberLifecycleStage.BindReady;
        next.UpdatedAtUtc = recordedAt;
        next.AuthorizationRevision = AdvanceAuthorizationRevision(state.AuthorizationRevision);
        return next;
    }

    private static StudioMemberState ApplyWorkflowScheduleProvisioningRequested(
        StudioMemberState state,
        StudioMemberWorkflowScheduleProvisioningRequested evt)
    {
        var requestedAt = evt.RequestedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var next = state.Clone();
        next.WorkflowScheduleProvisioning = new StudioMemberWorkflowScheduleProvisioningState
        {
            Intent = evt.Intent?.Clone(),
            Status = StudioMemberWorkflowScheduleProvisioningStatus.PendingBinding,
            RequestedAtUtc = requestedAt,
            UpdatedAtUtc = requestedAt,
            DeadlineAtUtc = Timestamp.FromDateTimeOffset(
                requestedAt.ToDateTimeOffset().Add(ScheduleProvisioningBudget)),
        };
        next.UpdatedAtUtc = requestedAt;
        return next;
    }

    private static StudioMemberState ApplyWorkflowScheduleProvisioningTimingResolved(
        StudioMemberState state,
        StudioMemberWorkflowScheduleProvisioningTimingResolved evt)
    {
        if (!IsCurrentScheduleProvisioning(state, evt.ProvisioningId))
            return state;

        var next = state.Clone();
        next.WorkflowScheduleProvisioning.ResolvedOneShotFireAtUtc = evt.OneShotFireAtUtc;
        next.WorkflowScheduleProvisioning.UpdatedAtUtc = evt.ResolvedAtUtc;
        next.UpdatedAtUtc = evt.ResolvedAtUtc;
        return next;
    }

    private static StudioMemberState ApplyWorkflowScheduleProvisioningAttemptStarted(
        StudioMemberState state,
        StudioMemberWorkflowScheduleProvisioningAttemptStarted evt)
    {
        if (!IsCurrentScheduleProvisioning(state, evt.ProvisioningId))
            return state;

        var next = state.Clone();
        next.WorkflowScheduleProvisioning.Status = StudioMemberWorkflowScheduleProvisioningStatus.Provisioning;
        next.WorkflowScheduleProvisioning.AttemptCount = evt.Attempt;
        next.WorkflowScheduleProvisioning.AttemptInFlight = true;
        next.WorkflowScheduleProvisioning.Failure = null;
        next.WorkflowScheduleProvisioning.UpdatedAtUtc = evt.StartedAtUtc;
        next.UpdatedAtUtc = evt.StartedAtUtc;
        return next;
    }

    private static StudioMemberState ApplyWorkflowScheduleProvisioningRetryDeferred(
        StudioMemberState state,
        StudioMemberWorkflowScheduleProvisioningRetryDeferred evt)
    {
        if (!IsCurrentScheduleProvisioning(state, evt.ProvisioningId))
            return state;

        var deferredAt = evt.DeferredAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var next = state.Clone();
        next.WorkflowScheduleProvisioning.Status = StudioMemberWorkflowScheduleProvisioningStatus.RetryPending;
        next.WorkflowScheduleProvisioning.AttemptInFlight = false;
        next.WorkflowScheduleProvisioning.Failure = new StudioMemberWorkflowScheduleProvisioningFailure
        {
            Code = evt.FailureCode,
            Message = evt.Detail,
            FailedAtUtc = deferredAt,
        };
        next.WorkflowScheduleProvisioning.UpdatedAtUtc = deferredAt;
        next.UpdatedAtUtc = deferredAt;
        return next;
    }

    private static StudioMemberState ApplyWorkflowScheduleProvisioningSucceeded(
        StudioMemberState state,
        StudioMemberWorkflowScheduleProvisioningSucceeded evt)
    {
        if (!IsCurrentScheduleProvisioning(state, evt.ProvisioningId))
            return state;

        var completedAt = evt.CompletedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var next = state.Clone();
        next.WorkflowScheduleProvisioning.Status = StudioMemberWorkflowScheduleProvisioningStatus.Succeeded;
        next.WorkflowScheduleProvisioning.AttemptInFlight = false;
        next.WorkflowScheduleProvisioning.ScheduleId = evt.ScheduleId;
        next.WorkflowScheduleProvisioning.OperationId = evt.OperationId;
        next.WorkflowScheduleProvisioning.Failure = null;
        next.WorkflowScheduleProvisioning.UpdatedAtUtc = completedAt;
        next.UpdatedAtUtc = completedAt;
        return next;
    }

    private static StudioMemberState ApplyWorkflowScheduleProvisioningFailed(
        StudioMemberState state,
        StudioMemberWorkflowScheduleProvisioningFailed evt)
    {
        if (!IsCurrentScheduleProvisioning(state, evt.ProvisioningId))
            return state;

        var failedAt = evt.Failure?.FailedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var next = state.Clone();
        next.WorkflowScheduleProvisioning.Status = StudioMemberWorkflowScheduleProvisioningStatus.Failed;
        next.WorkflowScheduleProvisioning.AttemptInFlight = false;
        next.WorkflowScheduleProvisioning.Failure = evt.Failure?.Clone();
        next.WorkflowScheduleProvisioning.UpdatedAtUtc = failedAt;
        next.UpdatedAtUtc = failedAt;
        return next;
    }

    private static bool TryBuildDeleteBindingFailure(
        StudioMemberState state,
        Timestamp deletedAt,
        out StudioMemberBindingFailedEvent bindingFailed)
    {
        bindingFailed = new StudioMemberBindingFailedEvent();
        var binding = state.Binding;
        if (binding == null
            || string.IsNullOrEmpty(binding.CurrentBindingRunId)
            || IsTerminalBindingStatus(binding.CurrentStatus))
        {
            return false;
        }

        bindingFailed = new StudioMemberBindingFailedEvent
        {
            BindingRunId = binding.CurrentBindingRunId,
            Failure = new StudioMemberBindingFailure
            {
                Code = MemberDeletedFailureCode,
                Message = "member was deleted before binding completed.",
                FailedAtUtc = deletedAt,
            },
        };
        return true;
    }

    private static bool TryBuildCommittedDeleteBindingTermination(
        StudioMemberState state,
        out StudioMemberBindingAuthorityTerminated termination)
    {
        termination = new StudioMemberBindingAuthorityTerminated();
        var binding = state.Binding;
        if (!state.Deleted
            || binding == null
            || string.IsNullOrEmpty(binding.CurrentBindingRunId)
            || binding.CurrentStatus != StudioMemberBindingRunStatus.Failed
            || binding.LastFailure == null
            || !string.Equals(binding.LastFailure.Code, MemberDeletedFailureCode, StringComparison.Ordinal)
            || binding.LastFailure.FailedAtUtc == null)
        {
            return false;
        }

        termination = new StudioMemberBindingAuthorityTerminated
        {
            BindingRunId = binding.CurrentBindingRunId,
            ScopeId = state.ScopeId,
            MemberId = state.MemberId,
            Failure = binding.LastFailure.Clone(),
        };
        return true;
    }

    private static StudioMemberBindingAuthorityTerminated BuildBindingAuthorityTermination(
        StudioMemberState state,
        StudioMemberBindingFailedEvent bindingFailed) => new()
    {
        BindingRunId = bindingFailed.BindingRunId,
        ScopeId = state.ScopeId,
        MemberId = state.MemberId,
        Failure = bindingFailed.Failure.Clone(),
    };

    private static bool CanAcceptBindingRunProgress(StudioMemberState state, string bindingRunId)
    {
        var currentRun = state.Binding?.CurrentBindingRunId;
        return !string.IsNullOrEmpty(currentRun)
               && string.Equals(currentRun, bindingRunId, StringComparison.Ordinal)
               && !IsCurrentBindingTerminal(state);
    }

    private static string ResolveExpectedActorId(StudioMemberBindingCompletedEvent evt) =>
        evt.ExpectedActorId ?? string.Empty;

    private static bool HasActiveBindingRun(StudioMemberState state, string incomingBindingRunId)
    {
        var currentBinding = state.Binding;
        return currentBinding != null
               && !string.IsNullOrEmpty(currentBinding.CurrentBindingRunId)
               && !string.Equals(currentBinding.CurrentBindingRunId, incomingBindingRunId, StringComparison.Ordinal)
               && !IsTerminalBindingStatus(currentBinding.CurrentStatus);
    }

    private static bool ShouldIgnoreBindingRunStart(
        StudioMemberState state,
        string bindingRunId,
        Timestamp? requestedAtUtc)
    {
        var currentBinding = state.Binding;
        if (currentBinding == null || string.IsNullOrEmpty(currentBinding.CurrentBindingRunId))
            return false;

        if (string.Equals(currentBinding.CurrentBindingRunId, bindingRunId, StringComparison.Ordinal))
            return true;

        if (!IsTerminalBindingStatus(currentBinding.CurrentStatus))
            return true;

        if (currentBinding.UpdatedAtUtc == null)
            return false;

        return CompareTimestamp(requestedAtUtc, currentBinding.UpdatedAtUtc) <= 0;
    }

    private static bool IsSupersededBindingRun(
        StudioMemberState state,
        string bindingRunId,
        Timestamp? requestedAtUtc)
    {
        var currentBinding = state.Binding;
        if (currentBinding == null || string.IsNullOrEmpty(currentBinding.CurrentBindingRunId))
            return false;

        if (string.Equals(currentBinding.CurrentBindingRunId, bindingRunId, StringComparison.Ordinal))
            return false;

        if (currentBinding.UpdatedAtUtc == null)
            return false;

        return CompareTimestamp(requestedAtUtc, currentBinding.UpdatedAtUtc) <= 0;
    }

    private static bool IsTerminalBindingRunReplay(StudioMemberState state, string bindingRunId)
    {
        var currentBinding = state.Binding;
        return currentBinding != null
               && string.Equals(currentBinding.CurrentBindingRunId, bindingRunId, StringComparison.Ordinal)
               && IsTerminalBindingStatus(currentBinding.CurrentStatus);
    }

    private static bool IsTerminalBindingRunReplay(
        StudioMemberState state,
        string bindingRunId,
        StudioMemberBindingRunStatus expectedStatus)
    {
        var currentBinding = state.Binding;
        return currentBinding != null
               && string.Equals(currentBinding.CurrentBindingRunId, bindingRunId, StringComparison.Ordinal)
               && currentBinding.CurrentStatus == expectedStatus;
    }

    private static bool TryBuildTerminalBindingRunReplayResponse(
        StudioMemberState state,
        StudioMemberBindAdmissionRequested request,
        Timestamp failedAt,
        out StudioMemberBindingRejectedEvent response)
    {
        response = new StudioMemberBindingRejectedEvent();

        var currentBinding = state.Binding;
        if (currentBinding == null
            || !string.Equals(currentBinding.CurrentBindingRunId, request.BindingRunId, StringComparison.Ordinal)
            || currentBinding.CurrentStatus != StudioMemberBindingRunStatus.Rejected)
        {
            return false;
        }

        response = new StudioMemberBindingRejectedEvent
        {
            BindingRunId = request.BindingRunId,
            ScopeId = state.ScopeId,
            MemberId = state.MemberId,
            Failure = currentBinding.LastFailure?.Clone() ?? new StudioMemberBindingFailure
            {
                Code = "STUDIO_MEMBER_BINDING_RUN_REJECTED",
                Message = "binding run was already rejected.",
                FailedAtUtc = failedAt,
            },
        };
        return true;
    }

    private static bool IsCurrentBindingTerminal(StudioMemberState state) =>
        IsTerminalBindingStatus(state.Binding?.CurrentStatus ?? StudioMemberBindingRunStatus.Unspecified);

    private static bool IsTerminalBindingStatus(StudioMemberBindingRunStatus status) =>
        status is StudioMemberBindingRunStatus.Succeeded
            or StudioMemberBindingRunStatus.Failed
            or StudioMemberBindingRunStatus.Rejected;

    private Task SendTerminalAcknowledgementAsync(string bindingRunId, StudioMemberBindingRunStatus status) =>
        SendToAsync(
            StudioMemberConventions.BuildBindingRunActorId(bindingRunId),
            new StudioMemberBindingTerminalAcknowledged
            {
                BindingRunId = bindingRunId,
                Status = status,
                AcknowledgedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });

    private bool IsRuntimeEnvelopeRedelivery() =>
        ActiveInboundEnvelope?.Runtime?.Retry?.Attempt > 0;

    private async Task SendBindingAuthorityTerminationAsync(
        StudioMemberBindingAuthorityTerminated termination,
        CancellationToken ct = default)
    {
        try
        {
            await SendToAsync(
                StudioMemberConventions.BuildBindingRunActorId(termination.BindingRunId),
                termination,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new StudioMemberBindingAuthorityTerminationPublicationPendingException(
                "Committed member deletion still requires its binding-run termination publication.",
                exception);
        }
    }

    private async Task SendBindingRejectionAsync(
        string runActorId,
        StudioMemberBindingRejectedEvent rejection)
    {
        await FailWorkflowScheduleProvisioningForBindingRejectionAsync(rejection);
        await SendToAsync(runActorId, rejection);
    }

    private Task FailWorkflowScheduleProvisioningForBindingRejectionAsync(
        StudioMemberBindingRejectedEvent rejection)
    {
        var intent = State.WorkflowScheduleProvisioning?.Intent;
        if (!CanRecoverScheduleProvisioning() ||
            !string.Equals(intent?.BindingRunId, rejection.BindingRunId, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return FailWorkflowScheduleProvisioningAsync(
            rejection.Failure?.Code ?? "workflow_binding_rejected",
            rejection.Failure?.Message ?? "Workflow binding was rejected before schedule provisioning.");
    }

    private void ValidateScheduleProvisioningIntent(
        StudioMemberWorkflowScheduleProvisioningIntent intent)
    {
        if (State.Deleted)
            throw new InvalidOperationException("member has been deleted.");
        if (State.ImplementationKind != StudioMemberImplementationKind.Workflow)
            throw new InvalidOperationException("schedule provisioning requires a workflow member.");
        if (string.IsNullOrWhiteSpace(intent.ProvisioningId) ||
            !string.Equals(intent.ScopeId, State.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(intent.MemberId, State.MemberId, StringComparison.Ordinal) ||
            !string.Equals(intent.PublishedServiceId, State.PublishedServiceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("schedule provisioning target does not match member authority state.");
        }
        if (!State.HasTeamId || !string.Equals(intent.TeamId, State.TeamId, StringComparison.Ordinal))
            throw new InvalidOperationException("schedule provisioning team does not match member authority state.");
        if (string.IsNullOrWhiteSpace(intent.WorkflowId) ||
            string.IsNullOrWhiteSpace(intent.RevisionId) ||
            intent.Owner == null ||
            string.IsNullOrWhiteSpace(intent.Owner.Authority) ||
            string.IsNullOrWhiteSpace(intent.Owner.OwnerSubject) ||
            string.IsNullOrWhiteSpace(intent.SubjectPlatform) ||
            string.IsNullOrWhiteSpace(intent.SubjectExternalUserId) ||
            string.IsNullOrWhiteSpace(intent.VerifiedBindingId))
        {
            throw new InvalidOperationException("schedule provisioning intent is incomplete.");
        }
        if (intent.ScheduleMode == StudioMemberWorkflowScheduleMode.RecurringCron &&
            string.IsNullOrWhiteSpace(intent.CronExpression))
        {
            throw new InvalidOperationException("recurring schedule provisioning requires cron_expression.");
        }
        if (intent.ScheduleMode == StudioMemberWorkflowScheduleMode.OneShotAtUtc &&
            intent.OneShotDelaySeconds <= 0)
        {
            throw new InvalidOperationException("one-shot schedule provisioning requires a positive delay.");
        }
        if (intent.ScheduleMode == StudioMemberWorkflowScheduleMode.Unspecified)
            throw new InvalidOperationException("schedule provisioning mode is required.");
    }

    private bool CanRecoverScheduleProvisioning()
    {
        var provisioning = State.WorkflowScheduleProvisioning;
        return !State.Deleted &&
               provisioning?.Intent != null &&
               provisioning.Status is
                   StudioMemberWorkflowScheduleProvisioningStatus.PendingBinding or
                   StudioMemberWorkflowScheduleProvisioningStatus.Provisioning or
                   StudioMemberWorkflowScheduleProvisioningStatus.RetryPending;
    }

    private bool CanAcceptScheduleProvisioningContinuation(string provisioningId) =>
        CanRecoverScheduleProvisioning() &&
        string.Equals(
            State.WorkflowScheduleProvisioning!.Intent.ProvisioningId,
            provisioningId,
            StringComparison.Ordinal);

    private bool CanAcceptScheduleProvisioningExecutionContinuation(
        string provisioningId,
        int attempt) =>
        CanAcceptScheduleProvisioningContinuation(provisioningId) &&
        State.WorkflowScheduleProvisioning!.AttemptInFlight &&
        State.WorkflowScheduleProvisioning.AttemptCount == attempt;

    private static bool IsCurrentScheduleProvisioning(
        StudioMemberState state,
        string provisioningId) =>
        state.WorkflowScheduleProvisioning?.Intent != null &&
        string.Equals(
            state.WorkflowScheduleProvisioning.Intent.ProvisioningId,
            provisioningId,
            StringComparison.Ordinal);

    private bool IsTargetScheduleBindingObserved()
    {
        var intent = State.WorkflowScheduleProvisioning?.Intent;
        var binding = State.LastBinding;
        return intent != null &&
               binding != null &&
               string.Equals(binding.PublishedServiceId, intent.PublishedServiceId, StringComparison.Ordinal) &&
               string.Equals(binding.RevisionId, intent.RevisionId, StringComparison.Ordinal);
    }

    private bool ShouldFailScheduleProvisioningForBindingRun(string? bindingRunId)
    {
        var intent = State.WorkflowScheduleProvisioning?.Intent;
        var binding = State.Binding;
        return intent != null &&
               !string.IsNullOrWhiteSpace(intent.BindingRunId) &&
               string.Equals(intent.BindingRunId, bindingRunId, StringComparison.Ordinal) &&
               binding != null &&
               string.Equals(binding.CurrentBindingRunId, bindingRunId, StringComparison.Ordinal) &&
               binding.CurrentStatus is StudioMemberBindingRunStatus.Failed or StudioMemberBindingRunStatus.Rejected;
    }

    private bool HasConflictingActiveBindingRun(string? bindingRunId)
    {
        var binding = State.Binding;
        return binding != null &&
               !string.IsNullOrWhiteSpace(binding.CurrentBindingRunId) &&
               !string.Equals(binding.CurrentBindingRunId, bindingRunId, StringComparison.Ordinal) &&
               !IsTerminalBindingStatus(binding.CurrentStatus);
    }

    private async Task ScheduleWorkflowScheduleProvisioningIfReadyAsync(
        CancellationToken ct = default)
    {
        if (!CanRecoverScheduleProvisioning())
            return;
        if (!IsTargetScheduleBindingObserved())
            return;

        await ScheduleWorkflowScheduleProvisioningAttemptAsync(
            ScheduleProvisioningInitialDelay,
            ct);
    }

    private Task ScheduleWorkflowScheduleProvisioningAttemptAsync(
        TimeSpan dueTime,
        CancellationToken ct = default)
    {
        var provisioningId = State.WorkflowScheduleProvisioning?.Intent?.ProvisioningId;
        if (string.IsNullOrWhiteSpace(provisioningId))
            return Task.CompletedTask;

        return ScheduleSelfDurableTimeoutAsync(
            $"studio-member-workflow-schedule-provisioning:{provisioningId}",
            dueTime,
            new StudioMemberWorkflowScheduleProvisioningAttemptRequested
            {
                ProvisioningId = provisioningId,
                ObservedAttempt = State.WorkflowScheduleProvisioning!.AttemptCount,
            },
            ct: ct);
    }

    private bool IsScheduleProvisioningDeadlineReached(DateTimeOffset now)
    {
        var deadline = State.WorkflowScheduleProvisioning?.DeadlineAtUtc;
        return deadline != null && now >= deadline.ToDateTimeOffset();
    }

    private static bool ShouldRefreshScheduleProvisioningOneShotTiming(
        StudioMemberWorkflowScheduleProvisioningState provisioning,
        DateTimeOffset now)
    {
        if (provisioning.Intent.ScheduleMode != StudioMemberWorkflowScheduleMode.OneShotAtUtc)
            return false;

        var currentFireAt = provisioning.ResolvedOneShotFireAtUtc?.ToDateTimeOffset().ToUniversalTime();
        return currentFireAt == null ||
               currentFireAt.Value <= now.Add(ScheduleProvisioningOneShotMinimumLeadTime);
    }

    private static int ResolveScheduleProvisioningOneShotDelaySeconds(
        StudioMemberWorkflowScheduleProvisioningIntent intent) =>
        intent.OneShotDelaySeconds > 0 ? intent.OneShotDelaySeconds : 30;

    private Task FailWorkflowScheduleProvisioningAsync(string code, string message)
    {
        var provisioningId = State.WorkflowScheduleProvisioning?.Intent?.ProvisioningId;
        if (string.IsNullOrWhiteSpace(provisioningId))
            return Task.CompletedTask;

        return PersistDomainEventAsync(new StudioMemberWorkflowScheduleProvisioningFailed
        {
            ProvisioningId = provisioningId,
            Failure = new StudioMemberWorkflowScheduleProvisioningFailure
            {
                Code = string.IsNullOrWhiteSpace(code) ? "workflow_schedule_provisioning_failed" : code,
                Message = message ?? string.Empty,
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        });
    }

    private static int CompareTimestamp(Timestamp? left, Timestamp? right)
    {
        if (left == null && right == null)
            return 0;
        if (left == null)
            return -1;
        if (right == null)
            return 1;

        return left.ToDateTimeOffset().CompareTo(right.ToDateTimeOffset());
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
        next.AuthorizationRevision = AdvanceAuthorizationRevision(state.AuthorizationRevision);
        return next;
    }

    private static StudioMemberState ApplyDeleted(
        StudioMemberState state, StudioMemberDeletedEvent evt)
    {
        var next = state.Clone();
        next.Deleted = true;
        next.DeletedAtUtc = evt.DeletedAtUtc;
        next.UpdatedAtUtc = evt.DeletedAtUtc;
        next.ClearTeamId();
        next.AuthorizationRevision = AdvanceAuthorizationRevision(state.AuthorizationRevision);
        return next;
    }

    private static long AdvanceAuthorizationRevision(long currentRevision)
    {
        if (currentRevision < 0)
            throw new InvalidOperationException("member authorization_revision is invalid.");
        return currentRevision == 0 ? 2 : checked(currentRevision + 1);
    }

    private static string NormalizeActorIdSegment(string? value, string fieldName)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new InvalidOperationException($"{fieldName} is required.");
        if (trimmed.Contains(':'))
            throw new InvalidOperationException($"{fieldName} must not contain ':'.");
        return trimmed;
    }

    private static bool HasResolvedImplementationRef(StudioMemberImplementationRef? implRef) =>
        implRef != null &&
        (HasResolvedImplementationRef(implRef, StudioMemberImplementationKind.Workflow) ||
         HasResolvedImplementationRef(implRef, StudioMemberImplementationKind.Script) ||
         HasResolvedImplementationRef(implRef, StudioMemberImplementationKind.Gagent));

    private static bool HasResolvedImplementationRef(
        StudioMemberImplementationRef? implRef,
        StudioMemberImplementationKind implementationKind)
    {
        if (implRef == null)
            return false;

        return implementationKind switch
        {
            StudioMemberImplementationKind.Workflow =>
                implRef.Workflow != null && !string.IsNullOrEmpty(implRef.Workflow.WorkflowId),
            StudioMemberImplementationKind.Script =>
                implRef.Script != null && !string.IsNullOrEmpty(implRef.Script.ScriptId),
            StudioMemberImplementationKind.Gagent =>
                implRef.Gagent != null && !string.IsNullOrEmpty(implRef.Gagent.ActorTypeName),
            _ => false,
        };
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
