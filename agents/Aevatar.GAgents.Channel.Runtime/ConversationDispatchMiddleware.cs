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

        var actorId = ConversationGAgent.BuildActorId(canonicalKey);
        var actor = await _actorRuntime.CreateAsync<ConversationGAgent>(actorId, ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(context.Activity),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actor.Id),
        };

        await _actorDispatchPort.DispatchAsync(actor.Id, envelope, ct);
        await next();
    }
}
