using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Runtime;

internal sealed class ConversationDispatchMiddleware : IChannelMiddleware
{
    private const string PublisherActorId = "channel-runtime.conversation-dispatch";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;

    public ConversationDispatchMiddleware(
        IActorRuntime actorRuntime,
        IActorDispatchPort? actorDispatchPort = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort
            ?? actorRuntime as IActorDispatchPort
            ?? throw new ArgumentNullException(nameof(actorDispatchPort));
    }

    public async Task InvokeAsync(ITurnContext context, Func<Task> next, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var canonicalKey = context.Activity?.Conversation?.CanonicalKey;
        if (string.IsNullOrWhiteSpace(canonicalKey))
        {
            await next();
            return;
        }

        var threadActorId = ChannelConversationThreadGAgent.BuildActorId(canonicalKey);
        var threadActor = await _actorRuntime.CreateAsync<ChannelConversationThreadGAgent>(threadActorId, ct);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(context.Activity),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, threadActor.Id),
        };

        await _actorDispatchPort.DispatchAsync(threadActor.Id, envelope, ct);
        await next();
    }
}
