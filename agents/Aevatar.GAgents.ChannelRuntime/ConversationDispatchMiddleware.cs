using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChannelRuntime;

internal sealed class ConversationDispatchMiddleware : IChannelMiddleware
{
    private readonly IActorRuntime _actorRuntime;

    public ConversationDispatchMiddleware(IActorRuntime actorRuntime)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
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
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute
                {
                    TargetActorId = actor.Id,
                },
            },
        };

        await actor.HandleEventAsync(envelope, ct);
        await next();
    }
}
