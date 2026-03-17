using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

public sealed class MassTransitActorEventSubscriptionProvider : IActorEventSubscriptionProvider
{
    private readonly IMassTransitEnvelopeTransport _transport;
    private readonly MassTransitStreamOptions _options;

    public MassTransitActorEventSubscriptionProvider(
        IMassTransitEnvelopeTransport transport,
        MassTransitStreamOptions options)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
        string actorId,
        Func<TMessage, Task> handler,
        CancellationToken ct = default)
        where TMessage : class, IMessage, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(handler);

        var descriptor = typeof(TMessage) == typeof(EventEnvelope) ? null : new TMessage().Descriptor;
        return _transport.SubscribeAsync(async record =>
        {
            if (!string.Equals(record.StreamNamespace, _options.StreamNamespace, StringComparison.Ordinal) ||
                !string.Equals(record.StreamId, actorId, StringComparison.Ordinal) ||
                record.Payload is not { Length: > 0 })
            {
                return;
            }

            EventEnvelope envelope;
            try
            {
                envelope = EventEnvelope.Parser.ParseFrom(record.Payload);
            }
            catch
            {
                return;
            }

            if (typeof(TMessage) == typeof(EventEnvelope))
            {
                await ((Func<EventEnvelope, Task>)(object)handler)(envelope);
                return;
            }

            if (envelope.Payload == null || descriptor == null || !envelope.Payload.Is(descriptor))
                return;

            await handler(envelope.Payload.Unpack<TMessage>());
        }, ct);
    }
}
