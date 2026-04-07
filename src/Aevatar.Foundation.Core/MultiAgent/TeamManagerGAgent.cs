using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.MultiAgent;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.MultiAgent;

/// <summary>
/// Manages multi-agent team lifecycle: member registration, status tracking, and broadcast messaging.
/// Per-team scope — actor ID should be the team name to avoid hot-spot singleton.
/// </summary>
public class TeamManagerGAgent : GAgentBase<TeamManagerState>
{
    [EventHandler]
    public async Task HandleRegisterMember(RegisterMemberCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (string.IsNullOrWhiteSpace(cmd.AgentId))
            return;

        if (State.Members.ContainsKey(cmd.AgentId))
            return;

        await PersistDomainEventAsync(new MemberRegisteredEvent
        {
            AgentId = cmd.AgentId,
            AgentName = cmd.AgentName ?? string.Empty,
            AgentType = cmd.AgentType ?? string.Empty,
            OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    [EventHandler]
    public async Task HandleUnregisterMember(UnregisterMemberCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (!State.Members.ContainsKey(cmd.AgentId))
            return;

        await PersistDomainEventAsync(new MemberUnregisteredEvent
        {
            AgentId = cmd.AgentId,
        });
    }

    [EventHandler]
    public async Task HandleUpdateStatus(UpdateMemberStatusCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (!State.Members.ContainsKey(cmd.AgentId))
            return;

        await PersistDomainEventAsync(new MemberStatusUpdatedEvent
        {
            AgentId = cmd.AgentId,
            Status = cmd.Status ?? string.Empty,
        });
    }

    [EventHandler]
    public async Task HandleBroadcast(BroadcastMessageCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // Count recipients first, persist fact before side-effects
        var recipientCount = 0;
        foreach (var member in State.Members.Values)
        {
            if (string.Equals(member.Status, "offline", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(member.AgentId, cmd.FromAgentId, StringComparison.Ordinal))
                continue;
            recipientCount++;
        }

        await PersistDomainEventAsync(new TeamBroadcastSentEvent
        {
            FromAgentId = cmd.FromAgentId ?? string.Empty,
            Content = cmd.Content ?? string.Empty,
            RecipientCount = recipientCount,
        });

        // Side-effects after committed fact
        var message = new AgentMessage
        {
            FromAgentId = cmd.FromAgentId ?? string.Empty,
            Content = cmd.Content ?? string.Empty,
            Summary = cmd.Summary ?? string.Empty,
            SentAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        foreach (var member in State.Members.Values)
        {
            if (string.Equals(member.Status, "offline", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(member.AgentId, cmd.FromAgentId, StringComparison.Ordinal))
                continue;

            await SendToAsync(member.AgentId, message);
        }
    }

    protected override TeamManagerState TransitionState(TeamManagerState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<MemberRegisteredEvent>(ApplyMemberRegistered)
            .On<MemberUnregisteredEvent>(ApplyMemberUnregistered)
            .On<MemberStatusUpdatedEvent>(ApplyMemberStatusUpdated)
            .OrCurrent();

    private static Timestamp ResolveTimestamp(Timestamp? eventTimestamp) =>
        eventTimestamp != null && eventTimestamp != new Timestamp()
            ? eventTimestamp
            : Timestamp.FromDateTime(DateTime.UtcNow);

    private static TeamManagerState ApplyMemberRegistered(TeamManagerState state, MemberRegisteredEvent evt)
    {
        var next = state.Clone();
        next.Members[evt.AgentId] = new TeamMember
        {
            AgentId = evt.AgentId,
            AgentName = evt.AgentName,
            AgentType = evt.AgentType,
            Status = "idle",
            JoinedAt = ResolveTimestamp(evt.OccurredAt),
        };
        return next;
    }

    private static TeamManagerState ApplyMemberUnregistered(TeamManagerState state, MemberUnregisteredEvent evt)
    {
        var next = state.Clone();
        next.Members.Remove(evt.AgentId);
        return next;
    }

    private static TeamManagerState ApplyMemberStatusUpdated(TeamManagerState state, MemberStatusUpdatedEvent evt)
    {
        var next = state.Clone();
        if (next.Members.TryGetValue(evt.AgentId, out var member))
        {
            var updated = member.Clone();
            updated.Status = evt.Status;
            next.Members[evt.AgentId] = updated;
        }
        return next;
    }
}
