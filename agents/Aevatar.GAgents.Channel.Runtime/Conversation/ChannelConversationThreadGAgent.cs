using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.Channel.Runtime;

[GAgent("channel.runtime.conversation-thread")]
public sealed class ChannelConversationThreadGAgent : GAgentBase<ConversationThreadGAgentState>
{
    public static string BuildActorId(string sourceConversationCanonicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceConversationCanonicalKey);
        return $"channel-conversation-thread:{sourceConversationCanonicalKey.Trim()}";
    }

    public string ResolveActiveConversationCanonicalKey(string sourceConversationCanonicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceConversationCanonicalKey);
        return string.IsNullOrWhiteSpace(State.ActiveConversationCanonicalKey)
            ? sourceConversationCanonicalKey.Trim()
            : State.ActiveConversationCanonicalKey;
    }

    [EventHandler]
    public async Task HandleStartNewConversationAsync(StartNewConversationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var sourceConversationCanonicalKey = command.SourceConversationCanonicalKey.Trim();
        if (string.IsNullOrWhiteSpace(sourceConversationCanonicalKey))
            return;

        var generation = State.Generation + 1;
        var rotatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await PersistDomainEventAsync(new ConversationThreadRotatedEvent
        {
            SourceConversationCanonicalKey = sourceConversationCanonicalKey,
            Generation = generation,
            ActiveConversationCanonicalKey = BuildLogicalConversationCanonicalKey(sourceConversationCanonicalKey, generation),
            RequestedActivityId = command.RequestedActivityId.Trim(),
            RotatedAtUnixMs = rotatedAtUnixMs,
        });
    }

    protected override ConversationThreadGAgentState TransitionState(
        ConversationThreadGAgentState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ConversationThreadRotatedEvent>(ApplyRotated)
            .OrCurrent();

    private static ConversationThreadGAgentState ApplyRotated(
        ConversationThreadGAgentState current,
        ConversationThreadRotatedEvent evt)
    {
        var next = current.Clone();
        next.SourceConversationCanonicalKey = evt.SourceConversationCanonicalKey;
        next.Generation = evt.Generation;
        next.ActiveConversationCanonicalKey = evt.ActiveConversationCanonicalKey;
        next.LastUpdatedUnixMs = evt.RotatedAtUnixMs;
        return next;
    }

    private static string BuildLogicalConversationCanonicalKey(string sourceConversationCanonicalKey, long generation) =>
        $"{sourceConversationCanonicalKey.Trim()}#session-{generation:D8}";
}
