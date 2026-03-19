using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;
using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Orleans.Configuration;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansMassTransitQueueAdapterReceiverTests
{
    [Fact]
    public async Task Initialize_WhenNamespaceAndQueueMatch_ShouldEnqueueMessage()
    {
        var transport = new RecordingTransport();
        var mapper = new HashRingBasedStreamQueueMapper(
            new HashRingStreamQueueMapperOptions { TotalQueueCount = 1 },
            "test-provider");
        var queueId = mapper.GetAllQueues().Single();
        var receiver = new OrleansMassTransitQueueAdapterReceiver(
            queueId,
            transport,
            mapper,
            "aevatar.custom.events");

        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await transport.PushAsync(new MassTransitEnvelopeRecord
        {
            StreamNamespace = "aevatar.custom.events",
            StreamId = "actor-1",
            Payload = CreateEnvelopeBytes("payload"),
        });

        var messages = await receiver.GetQueueMessagesAsync(10);

        messages.Should().ContainSingle();
        messages[0].GetEvents<EventEnvelope>().Single().Item1.Payload!.Unpack<StringValue>().Value.Should().Be("payload");
        await receiver.Shutdown(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Initialize_WhenMappedQueueDoesNotMatchReceiver_ShouldDropMessage()
    {
        var transport = new RecordingTransport();
        var mapper = new HashRingBasedStreamQueueMapper(
            new HashRingStreamQueueMapperOptions { TotalQueueCount = 2 },
            "test-provider");
        var queues = mapper.GetAllQueues().ToArray();
        var targetStreamId = FindStreamIdForQueue(mapper, "aevatar.custom.events", queues[1]);
        var receiver = new OrleansMassTransitQueueAdapterReceiver(
            queues[0],
            transport,
            mapper,
            "aevatar.custom.events");

        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await transport.PushAsync(new MassTransitEnvelopeRecord
        {
            StreamNamespace = "aevatar.custom.events",
            StreamId = targetStreamId,
            Payload = CreateEnvelopeBytes("payload"),
        });

        var messages = await receiver.GetQueueMessagesAsync(10);

        messages.Should().BeEmpty();
        await receiver.Shutdown(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Initialize_WhenNamespaceDoesNotMatchConfiguredValue_ShouldDropMessage()
    {
        var transport = new RecordingTransport();
        var mapper = new HashRingBasedStreamQueueMapper(
            new HashRingStreamQueueMapperOptions { TotalQueueCount = 1 },
            "test-provider");
        var queueId = mapper.GetAllQueues().Single();
        var receiver = new OrleansMassTransitQueueAdapterReceiver(
            queueId,
            transport,
            mapper,
            "aevatar.custom.events");

        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await transport.PushAsync(new MassTransitEnvelopeRecord
        {
            StreamNamespace = "aevatar.another.events",
            StreamId = "actor-1",
            Payload = CreateEnvelopeBytes("payload"),
        });

        var messages = await receiver.GetQueueMessagesAsync(10);

        messages.Should().BeEmpty();
        await receiver.Shutdown(TimeSpan.FromSeconds(1));
    }

    private static byte[] CreateEnvelopeBytes(string value)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(new StringValue { Value = value }),
        };
        return envelope.ToByteArray();
    }

    private static string FindStreamIdForQueue(IStreamQueueMapper mapper, string streamNamespace, QueueId queueId)
    {
        for (var i = 0; i < 1024; i++)
        {
            var candidate = $"actor-{i}";
            var streamId = StreamId.Create(streamNamespace, candidate);
            if (mapper.GetQueueForStream(streamId) == queueId)
                return candidate;
        }

        throw new InvalidOperationException($"Unable to find a stream id for queue '{queueId}'.");
    }

    private sealed class RecordingTransport : IMassTransitEnvelopeTransport
    {
        private readonly Lock _handlersLock = new();
        private readonly List<Func<MassTransitEnvelopeRecord, Task>> _handlers = [];

        public Task PublishAsync(
            string streamNamespace,
            string streamId,
            byte[] payload,
            CancellationToken ct = default)
        {
            _ = streamNamespace;
            _ = streamId;
            _ = payload;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            Func<MassTransitEnvelopeRecord, Task> handler,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ct.ThrowIfCancellationRequested();

            lock (_handlersLock)
            {
                _handlers.Add(handler);
            }

            return Task.FromResult<IAsyncDisposable>(new Subscription(this, handler));
        }

        public async Task PushAsync(MassTransitEnvelopeRecord record)
        {
            List<Func<MassTransitEnvelopeRecord, Task>> handlers;
            lock (_handlersLock)
            {
                handlers = _handlers.ToList();
            }

            foreach (var handler in handlers)
                await handler(record);
        }

        private void Remove(Func<MassTransitEnvelopeRecord, Task> handler)
        {
            lock (_handlersLock)
            {
                _handlers.Remove(handler);
            }
        }

        private sealed class Subscription : IAsyncDisposable
        {
            private readonly RecordingTransport _owner;
            private readonly Func<MassTransitEnvelopeRecord, Task> _handler;
            private int _disposed;

            public Subscription(RecordingTransport owner, Func<MassTransitEnvelopeRecord, Task> handler)
            {
                _owner = owner;
                _handler = handler;
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                    return ValueTask.CompletedTask;

                _owner.Remove(_handler);
                return ValueTask.CompletedTask;
            }
        }
    }
}
