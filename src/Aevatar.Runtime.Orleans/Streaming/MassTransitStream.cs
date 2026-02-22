// ─────────────────────────────────────────────────────────────
// MassTransitStream - IStream implementation backed by MassTransit.
// ProduceAsync sends AgentEventMessage via ISendEndpointProvider.
// SubscribeAsync creates a dynamic MassTransit receive endpoint.
// ─────────────────────────────────────────────────────────────

using Aevatar.Orleans.Consumers;
using Google.Protobuf;
using MassTransit;

namespace Aevatar.Orleans.Streaming;

/// <summary>
/// MassTransit-backed event stream for a single actor.
/// Uses <see cref="IAgentEventSender"/> for transport-agnostic message sending
/// and <see cref="IBus"/> for dynamic subscription endpoints.
/// </summary>
public sealed class MassTransitStream : IStream
{
    private readonly IAgentEventSender _sender;
    private readonly IBus _bus;

    /// <summary>Creates a MassTransit stream for the specified actor.</summary>
    public MassTransitStream(string streamId, IAgentEventSender sender, IBus bus)
    {
        StreamId = streamId;
        _sender = sender;
        _bus = bus;
    }

    /// <inheritdoc />
    public string StreamId { get; }

    /// <inheritdoc />
    public async Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
    {
        byte[] bytes;
        if (message is EventEnvelope envelope)
        {
            bytes = envelope.ToByteArray();
        }
        else
        {
            // Wrap non-envelope messages in an EventEnvelope
            var wrapped = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Google.Protobuf.WellKnownTypes.Any.Pack(message),
                Direction = EventDirection.Down,
            };
            bytes = wrapped.ToByteArray();
        }

        var msg = new AgentEventMessage
        {
            TargetActorId = StreamId,
            EnvelopeBytes = bytes,
        };

        await _sender.SendAsync(msg, ct);
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> SubscribeAsync<T>(
        Func<T, Task> handler, CancellationToken ct = default) where T : IMessage
    {
        // Create a dynamic receive endpoint for this subscription.
        // Each subscription gets a unique temporary queue so the subscriber
        // receives only messages targeted at this StreamId.
        var endpointName = $"aevatar-sub-{StreamId}-{Guid.NewGuid():N}";

        var handle = _bus.ConnectReceiveEndpoint(endpointName, cfg =>
        {
            cfg.Handler<AgentEventMessage>(async context =>
            {
                var msg = context.Message;
                if (msg.TargetActorId != StreamId) return;

                var envelope = EventEnvelope.Parser.ParseFrom(msg.EnvelopeBytes);

                if (typeof(T) == typeof(EventEnvelope))
                {
                    await ((Func<EventEnvelope, Task>)(object)handler)(envelope);
                }
            });
        });

        // Must await Ready before the endpoint can receive messages
        await handle.Ready;

        return new MassTransitSubscription(handle);
    }

    /// <summary>Disposable subscription handle wrapping a MassTransit endpoint.</summary>
    private sealed class MassTransitSubscription(HostReceiveEndpointHandle handle) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await handle.StopAsync();
        }
    }
}
