using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GroupChat.Abstractions;
using Google.Protobuf;

namespace Aevatar.GroupChat.Core.GAgents;

public sealed class AgentFeedGAgent : GAgentBase<AgentFeedState>
{
    public AgentFeedGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleAcceptSignalAsync(AcceptSignalToFeedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateAcceptCommand(command);
        EnsureAgentBinding(command.AgentId);

        if (State.SeenSignalIdEntries.Contains(command.SignalId) ||
            State.NextItemEntries.Any(x => string.Equals(x.SignalId, command.SignalId, StringComparison.Ordinal)))
        {
            return;
        }

        await PersistDomainEventAsync(new FeedSignalAcceptedEvent
        {
            AgentId = command.AgentId,
            SignalId = command.SignalId,
            GroupId = command.GroupId,
            ThreadId = command.ThreadId,
            TopicId = command.TopicId,
            SenderKind = command.SenderKind,
            SenderId = command.SenderId,
            SignalKind = command.SignalKind,
            SourceEventId = command.SourceEventId,
            SourceStateVersion = command.SourceStateVersion,
            TimelineCursor = command.TimelineCursor,
            AcceptReason = command.AcceptReason,
            RankScore = command.RankScore,
        });
    }

    [EventHandler]
    public async Task HandleAdvanceCursorAsync(AdvanceFeedCursorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SignalId);
        EnsureAgentBinding(command.AgentId);

        if (!State.NextItemEntries.Any(x => string.Equals(x.SignalId, command.SignalId, StringComparison.Ordinal)))
            return;

        await PersistDomainEventAsync(new FeedCursorAdvancedEvent
        {
            AgentId = command.AgentId,
            SignalId = command.SignalId,
        });
    }

    protected override AgentFeedState TransitionState(AgentFeedState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<FeedSignalAcceptedEvent>(ApplyAccepted)
            .On<FeedCursorAdvancedEvent>(ApplyAdvanced)
            .OrCurrent();

    private static AgentFeedState ApplyAccepted(AgentFeedState state, FeedSignalAcceptedEvent evt)
    {
        var next = state.Clone();
        next.AgentId = evt.AgentId;
        next.NextItemEntries.Add(new AgentFeedItemState
        {
            SignalId = evt.SignalId,
            GroupId = evt.GroupId,
            ThreadId = evt.ThreadId,
            TopicId = evt.TopicId,
            SenderKind = evt.SenderKind,
            SenderId = evt.SenderId,
            SignalKind = evt.SignalKind,
            SourceEventId = evt.SourceEventId,
            SourceStateVersion = evt.SourceStateVersion,
            TimelineCursor = evt.TimelineCursor,
            AcceptReason = evt.AcceptReason,
            RankScore = evt.RankScore,
        });
        SortNextItems(next.NextItemEntries);
        next.SeenSignalIdEntries.Add(evt.SignalId);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.AgentId, $"accepted:{evt.SignalId}");
        return next;
    }

    private static AgentFeedState ApplyAdvanced(AgentFeedState state, FeedCursorAdvancedEvent evt)
    {
        var next = state.Clone();
        var item = next.NextItemEntries.FirstOrDefault(x => string.Equals(x.SignalId, evt.SignalId, StringComparison.Ordinal));
        if (item != null)
            next.NextItemEntries.Remove(item);

        next.FeedCursor = state.FeedCursor + 1;
        next.SeenSignalIdEntries.Add(evt.SignalId);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.AgentId, $"advanced:{evt.SignalId}");
        return next;
    }

    private void EnsureAgentBinding(string agentId)
    {
        if (string.IsNullOrWhiteSpace(State.AgentId))
            return;

        if (!string.Equals(State.AgentId, agentId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Agent feed actor '{Id}' is bound to '{State.AgentId}', but got '{agentId}'.");
    }

    private static void ValidateAcceptCommand(AcceptSignalToFeedCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SignalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.GroupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TopicId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SenderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SourceEventId);
    }

    private static void SortNextItems(IList<AgentFeedItemState> items)
    {
        var ordered = items
            .OrderByDescending(x => x.RankScore)
            .ThenBy(x => x.TimelineCursor)
            .ThenBy(x => x.SignalId, StringComparer.Ordinal)
            .ToList();
        items.Clear();
        foreach (var item in ordered)
            items.Add(item);
    }

    private static string BuildEventId(string agentId, string suffix) => $"feed:{agentId}:{suffix}";
}
