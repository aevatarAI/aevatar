using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

internal sealed class MassTransitStream : IStream
{
    private readonly string _streamId;
    private readonly string _streamNamespace;
    private readonly IMassTransitEnvelopeTransport _transport;

    public MassTransitStream(
        string streamId,
        string streamNamespace,
        IMassTransitEnvelopeTransport transport)
    {
        _streamId = streamId;
        _streamNamespace = streamNamespace;
        _transport = transport;
    }

    public string StreamId => _streamId;

    public Task ProduceAsync<T>(T message, CancellationToken ct = default)
        where T : IMessage
    {
        ArgumentNullException.ThrowIfNull(message);

        var envelope = message as EventEnvelope ?? new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(message),
            Direction = EventDirection.Down,
        };

        return _transport.PublishAsync(_streamNamespace, _streamId, envelope.ToByteArray(), ct);
    }

    public async Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
        where T : IMessage, new()
    {
        ArgumentNullException.ThrowIfNull(handler);

        var descriptor = typeof(T) == typeof(EventEnvelope) ? null : new T().Descriptor;
        return await _transport.SubscribeAsync(async record =>
        {
            if (!string.Equals(record.StreamNamespace, _streamNamespace, StringComparison.Ordinal) ||
                !string.Equals(record.StreamId, _streamId, StringComparison.Ordinal) ||
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

            if (typeof(T) == typeof(EventEnvelope))
            {
                await ((Func<EventEnvelope, Task>)(object)handler)(envelope);
                return;
            }

            if (envelope.Payload == null || descriptor == null || !envelope.Payload.Is(descriptor))
                return;

            await handler(envelope.Payload.Unpack<T>());
        }, ct);
    }
}
