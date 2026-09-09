using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.Channel.Runtime;

[GAgent("channel.runtime.conversation-thread")]
public sealed class ChannelConversationThreadGAgent : GAgentBase<ConversationThreadGAgentState>
{
    private const string PublisherActorId = "channel-runtime.conversation-thread";

    public static string BuildActorId(string sourceConversationCanonicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceConversationCanonicalKey);
        return $"channel-conversation-thread:{sourceConversationCanonicalKey.Trim()}";
    }

    [EventHandler]
    public async Task HandleInboundActivityAsync(ChatActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var sourceConversationCanonicalKey = activity.Conversation?.CanonicalKey?.Trim();
        if (string.IsNullOrWhiteSpace(sourceConversationCanonicalKey))
            return;

        var activeConversationCanonicalKey = ResolveActiveConversationCanonicalKey(sourceConversationCanonicalKey);
        if (ShouldStartNewConversation(activity))
        {
            activeConversationCanonicalKey = await RotateAsync(
                    sourceConversationCanonicalKey,
                    activity.Id ?? string.Empty,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .ConfigureAwait(false);
        }

        await DispatchToConversationAsync(activity, activeConversationCanonicalKey, CancellationToken.None)
            .ConfigureAwait(false);
    }

    [EventHandler]
    public async Task HandleStartNewConversationAsync(StartNewConversationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var sourceConversationCanonicalKey = command.SourceConversationCanonicalKey.Trim();
        if (string.IsNullOrWhiteSpace(sourceConversationCanonicalKey))
            return;

        await RotateAsync(
                sourceConversationCanonicalKey,
                command.RequestedActivityId.Trim(),
                command.RequestedAtUnixMs)
            .ConfigureAwait(false);
    }

    protected override ConversationThreadGAgentState TransitionState(
        ConversationThreadGAgentState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ConversationThreadRotatedEvent>(ApplyRotated)
            .OrCurrent();

    private string ResolveActiveConversationCanonicalKey(string sourceConversationCanonicalKey) =>
        string.IsNullOrWhiteSpace(State.ActiveConversationCanonicalKey)
            ? sourceConversationCanonicalKey.Trim()
            : State.ActiveConversationCanonicalKey;

    private async Task<string> RotateAsync(
        string sourceConversationCanonicalKey,
        string requestedActivityId,
        long requestedAtUnixMs)
    {
        if (!string.IsNullOrWhiteSpace(requestedActivityId)
            && string.Equals(State.LastRotationActivityId, requestedActivityId, StringComparison.Ordinal))
        {
            return ResolveActiveConversationCanonicalKey(sourceConversationCanonicalKey);
        }

        var generation = State.Generation + 1;
        var rotatedAtUnixMs = requestedAtUnixMs > 0
            ? requestedAtUnixMs
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var activeConversationCanonicalKey = BuildLogicalConversationCanonicalKey(sourceConversationCanonicalKey, generation);
        await PersistDomainEventAsync(new ConversationThreadRotatedEvent
        {
            SourceConversationCanonicalKey = sourceConversationCanonicalKey,
            Generation = generation,
            ActiveConversationCanonicalKey = activeConversationCanonicalKey,
            RequestedActivityId = requestedActivityId,
            RotatedAtUnixMs = rotatedAtUnixMs,
        }).ConfigureAwait(false);
        return activeConversationCanonicalKey;
    }

    private async Task DispatchToConversationAsync(
        ChatActivity activity,
        string activeConversationCanonicalKey,
        CancellationToken ct)
    {
        var actorRuntime = Services.GetRequiredService<IActorRuntime>();
        var dispatchPort = Services.GetRequiredService<IActorDispatchPort>();
        var actorId = ConversationGAgent.BuildActorId(activeConversationCanonicalKey);
        var actor = await actorRuntime.CreateAsync<ConversationGAgent>(actorId, ct).ConfigureAwait(false);
        await dispatchPort.DispatchAsync(
                actor.Id,
                new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Payload = Any.Pack(activity),
                    Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actor.Id),
                    Propagation = new EnvelopePropagation { CorrelationId = activity.Id ?? string.Empty },
                },
                ct)
            .ConfigureAwait(false);
    }

    private static bool ShouldStartNewConversation(ChatActivity activity) =>
        activity.Conversation?.Scope == ConversationScope.DirectMessage
        && TryParseSlashCommand(activity.Content?.Text, out var commandName)
        && string.Equals(commandName, "new", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseSlashCommand(string? text, out string commandName)
    {
        commandName = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '/')
            return false;

        var firstSeparator = -1;
        for (var i = 1; i < trimmed.Length; i++)
        {
            if (!char.IsWhiteSpace(trimmed[i]))
                continue;

            firstSeparator = i;
            break;
        }

        commandName = firstSeparator < 0
            ? trimmed[1..]
            : trimmed[1..firstSeparator];
        return !string.IsNullOrWhiteSpace(commandName);
    }

    private static ConversationThreadGAgentState ApplyRotated(
        ConversationThreadGAgentState current,
        ConversationThreadRotatedEvent evt)
    {
        var next = current.Clone();
        next.SourceConversationCanonicalKey = evt.SourceConversationCanonicalKey;
        next.Generation = evt.Generation;
        next.ActiveConversationCanonicalKey = evt.ActiveConversationCanonicalKey;
        next.LastRotationActivityId = evt.RequestedActivityId;
        next.LastUpdatedUnixMs = evt.RotatedAtUnixMs;
        return next;
    }

    private static string BuildLogicalConversationCanonicalKey(string sourceConversationCanonicalKey, long generation) =>
        $"{sourceConversationCanonicalKey.Trim()}#session-{generation:D8}";
}
