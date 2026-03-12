using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Abstractions;

public interface IEventContext
{
    EventEnvelope InboundEnvelope { get; }

    string AgentId { get; }

    IServiceProvider Services { get; }

    ILogger Logger { get; }

    Task PublishAsync<TEvent>(
        TEvent evt,
        BroadcastDirection direction = BroadcastDirection.Down,
        CancellationToken ct = default,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage;

    Task SendToAsync<TEvent>(
        string targetActorId,
        TEvent evt,
        CancellationToken ct = default,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        throw new NotSupportedException(
            $"{GetType().Name} does not support SendToAsync.");
    }
}
