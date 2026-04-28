using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.StudioMember;
using Google.Protobuf;

namespace Aevatar.GAgents.StudioTeam;

/// <summary>
/// Per-team actor that owns the canonical StudioTeam authority state and the
/// persisted member roster (ADR-0017).
///
/// Actor ID convention: <c>studio-team:{scopeId}:{teamId}</c>.
///
/// The actor handles three command-style events directly
/// (<see cref="StudioTeamCreatedEvent"/>, <see cref="StudioTeamUpdatedEvent"/>,
/// <see cref="StudioTeamArchivedEvent"/>) and one cross-actor signal
/// (<see cref="StudioMemberReassignedEvent"/>) emitted by
/// <see cref="StudioMemberGAgent"/>. The application command port is
/// responsible for delivering reassign events to both source and destination
/// TeamGAgents; this actor applies idempotent set operations on
/// <c>member_ids</c> and persists a <see cref="StudioTeamMemberRosterChangedEvent"/>
/// reflecting the resulting effect (ADDED / REMOVED / NOOP).
///
/// Lifecycle is monotonic: <c>ACTIVE -> ARCHIVED</c>. Archive is irreversible
/// and is a metadata signal only — it does <em>not</em> reject member
/// reassignments at the actor layer (ADR-0017 §Q5 / Locked Rule 5).
/// </summary>
public sealed class StudioTeamGAgent : GAgentBase<StudioTeamState>, IProjectedActor
{
    public static string ProjectionKind => "studio-team";

    [EventHandler(EndpointName = "createTeam")]
    public async Task HandleCreated(StudioTeamCreatedEvent evt)
    {
        if (!string.IsNullOrEmpty(State.TeamId))
        {
            // First-write-wins on identity (mirrors StudioMemberGAgent).
            if (!string.Equals(State.TeamId, evt.TeamId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"team already initialized with id '{State.TeamId}'.");
            }

            if (!string.Equals(State.DisplayName, evt.DisplayName, StringComparison.Ordinal)
                || !string.Equals(State.Description, evt.Description, StringComparison.Ordinal)
                || !string.Equals(State.ScopeId, evt.ScopeId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"team '{State.TeamId}' already exists with different scope / displayName / description. " +
                    "First-write-wins on team identity; use updateTeam to change later.");
            }

            // Same teamId + same identity-stable fields = idempotent no-op.
            return;
        }

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "updateTeam")]
    public async Task HandleUpdated(StudioTeamUpdatedEvent evt)
    {
        if (string.IsNullOrEmpty(State.TeamId))
        {
            throw new InvalidOperationException("team not yet created.");
        }

        if (State.LifecycleStage == StudioTeamLifecycleStage.Archived)
        {
            throw new InvalidOperationException(
                $"team '{State.TeamId}' is archived; updates are not allowed.");
        }

        // Reject empty display_name when present — see ADR-0017 §Q6 / proto Note.
        if (evt.HasDisplayName && string.IsNullOrEmpty(evt.DisplayName))
        {
            throw new InvalidOperationException(
                "display_name must not be empty when present (use absence to mean 'no change').");
        }

        // No-op events: if neither field is present, nothing to persist.
        if (!evt.HasDisplayName && !evt.HasDescription)
        {
            return;
        }

        await PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "archiveTeam")]
    public async Task HandleArchived(StudioTeamArchivedEvent evt)
    {
        if (string.IsNullOrEmpty(State.TeamId))
        {
            throw new InvalidOperationException("team not yet created.");
        }

        if (State.LifecycleStage == StudioTeamLifecycleStage.Archived)
        {
            // Already archived — irreversible, but idempotent against
            // duplicate archive commands.
            return;
        }

        await PersistDomainEventAsync(evt);
    }

    /// <summary>
    /// Reacts to a <see cref="StudioMemberReassignedEvent"/> dispatched to
    /// this team because the team is named on either the from or the to side.
    /// Applies the standard idempotent set operation against <c>member_ids</c>
    /// and persists a <see cref="StudioTeamMemberRosterChangedEvent"/>
    /// reflecting the resulting effect.
    /// </summary>
    [EventHandler(EndpointName = "applyMemberReassignment")]
    public async Task HandleMemberReassigned(StudioMemberReassignedEvent evt)
    {
        if (string.IsNullOrEmpty(State.TeamId))
        {
            // Team must exist before it can host members. A stray reassign
            // should not auto-create the team via a roster mutation.
            throw new InvalidOperationException(
                "team not yet created; cannot apply member reassignment.");
        }

        if (!string.Equals(State.ScopeId, evt.ScopeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"team '{State.TeamId}' (scope {State.ScopeId}) cannot apply reassignment in scope {evt.ScopeId}.");
        }

        var isFromTeam = evt.HasFromTeamId
            && string.Equals(evt.FromTeamId, State.TeamId, StringComparison.Ordinal);
        var isToTeam = evt.HasToTeamId
            && string.Equals(evt.ToTeamId, State.TeamId, StringComparison.Ordinal);

        if (!isFromTeam && !isToTeam)
        {
            throw new InvalidOperationException(
                $"team '{State.TeamId}' is neither the from nor to side of the reassignment for member '{evt.MemberId}'.");
        }

        var alreadyHasMember = ContainsMember(State.MemberIds, evt.MemberId);

        StudioTeamRosterEffect effect;
        int memberCountAfter;
        if (isFromTeam)
        {
            // remove if present
            effect = alreadyHasMember
                ? StudioTeamRosterEffect.Removed
                : StudioTeamRosterEffect.Noop;
            memberCountAfter = alreadyHasMember
                ? State.MemberIds.Count - 1
                : State.MemberIds.Count;
        }
        else
        {
            // add if not present
            effect = alreadyHasMember
                ? StudioTeamRosterEffect.Noop
                : StudioTeamRosterEffect.Added;
            memberCountAfter = alreadyHasMember
                ? State.MemberIds.Count
                : State.MemberIds.Count + 1;
        }

        var rosterEvent = new StudioTeamMemberRosterChangedEvent
        {
            TeamId = State.TeamId,
            ScopeId = State.ScopeId,
            MemberId = evt.MemberId,
            Effect = effect,
            MemberCount = memberCountAfter,
            ChangedAtUtc = evt.ReassignedAtUtc,
        };
        await PersistDomainEventAsync(rosterEvent);
    }

    protected override StudioTeamState TransitionState(
        StudioTeamState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<StudioTeamCreatedEvent>(ApplyCreated)
            .On<StudioTeamUpdatedEvent>(ApplyUpdated)
            .On<StudioTeamArchivedEvent>(ApplyArchived)
            .On<StudioTeamMemberRosterChangedEvent>(ApplyRosterChanged)
            .OrCurrent();
    }

    private static StudioTeamState ApplyCreated(
        StudioTeamState state, StudioTeamCreatedEvent evt)
    {
        return new StudioTeamState
        {
            TeamId = evt.TeamId,
            ScopeId = evt.ScopeId,
            DisplayName = evt.DisplayName,
            Description = evt.Description,
            LifecycleStage = StudioTeamLifecycleStage.Active,
            CreatedAtUtc = evt.CreatedAtUtc,
            UpdatedAtUtc = evt.CreatedAtUtc,
        };
    }

    private static StudioTeamState ApplyUpdated(
        StudioTeamState state, StudioTeamUpdatedEvent evt)
    {
        var next = state.Clone();
        if (evt.HasDisplayName)
            next.DisplayName = evt.DisplayName;
        if (evt.HasDescription)
            next.Description = evt.Description;
        next.UpdatedAtUtc = evt.UpdatedAtUtc;
        return next;
    }

    private static StudioTeamState ApplyArchived(
        StudioTeamState state, StudioTeamArchivedEvent evt)
    {
        var next = state.Clone();
        next.LifecycleStage = StudioTeamLifecycleStage.Archived;
        next.UpdatedAtUtc = evt.ArchivedAtUtc;
        return next;
    }

    private static StudioTeamState ApplyRosterChanged(
        StudioTeamState state, StudioTeamMemberRosterChangedEvent evt)
    {
        var next = state.Clone();
        switch (evt.Effect)
        {
            case StudioTeamRosterEffect.Added:
                if (!ContainsMember(next.MemberIds, evt.MemberId))
                    next.MemberIds.Add(evt.MemberId);
                break;
            case StudioTeamRosterEffect.Removed:
                RemoveMember(next.MemberIds, evt.MemberId);
                break;
            case StudioTeamRosterEffect.Noop:
            case StudioTeamRosterEffect.Unspecified:
            default:
                // No roster change (UpdatedAtUtc still advances).
                break;
        }
        next.UpdatedAtUtc = evt.ChangedAtUtc;
        return next;
    }

    private static bool ContainsMember(
        Google.Protobuf.Collections.RepeatedField<string> members, string memberId)
    {
        for (var i = 0; i < members.Count; i++)
        {
            if (string.Equals(members[i], memberId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void RemoveMember(
        Google.Protobuf.Collections.RepeatedField<string> members, string memberId)
    {
        for (var i = 0; i < members.Count; i++)
        {
            if (string.Equals(members[i], memberId, StringComparison.Ordinal))
            {
                members.RemoveAt(i);
                return;
            }
        }
    }
}
