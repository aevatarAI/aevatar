using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.KafkaAdapter;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Orleans.Configuration;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansKafkaQueueAdapterReceiverTests
{
    [Fact]
    public async Task Initialize_WhenNamespaceMatchesConfiguredValue_ShouldEnqueueMessage()
    {
        var transport = new RecordingTransport();
        var mapper = new HashRingBasedStreamQueueMapper(
            new HashRingStreamQueueMapperOptions { TotalQueueCount = 1 },
            "test-provider");
        var queueId = mapper.GetAllQueues().Single();
        var receiver = new OrleansKafkaQueueAdapterReceiver(
            queueId,
            mapper,
            transport,
            "test-topic",
            "aevatar.custom.events");

        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await transport.PushAsync(new KafkaEnvelopeRecord
        {
            StreamNamespace = "aevatar.custom.events",
            StreamId = "actor-1",
            Payload = CreateEnvelopeBytes("payload"),
        });

        var messages = await receiver.GetQueueMessagesAsync(10);

        messages.Should().HaveCount(1);
        var events = messages[0].GetEvents<EventEnvelope>().ToList();
        events.Should().ContainSingle();
        events[0].Item1.Payload!.Unpack<StringValue>().Value.Should().Be("payload");
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
        var receiver = new OrleansKafkaQueueAdapterReceiver(
            queueId,
            mapper,
            transport,
            "test-topic",
            "aevatar.custom.events");

        await receiver.Initialize(TimeSpan.FromSeconds(5));
        await transport.PushAsync(new KafkaEnvelopeRecord
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

    private sealed class RecordingTransport : IKafkaEnvelopeTransport
    {
        private readonly Lock _handlersLock = new();
        private readonly List<Func<KafkaEnvelopeRecord, Task>> _handlers = [];

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
            Func<KafkaEnvelopeRecord, Task> handler,
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

        public async Task PushAsync(KafkaEnvelopeRecord record)
        {
            Func<KafkaEnvelopeRecord, Task>[] handlers;
            lock (_handlersLock)
            {
                handlers = _handlers.ToArray();
            }

            foreach (var handler in handlers)
            {
                await handler(record);
            }
        }

        private void Unsubscribe(Func<KafkaEnvelopeRecord, Task> handler)
        {
            lock (_handlersLock)
            {
                _handlers.Remove(handler);
            }
        }

        private sealed class Subscription : IAsyncDisposable
        {
            private readonly RecordingTransport _owner;
            private readonly Func<KafkaEnvelopeRecord, Task> _handler;
            private int _disposed;

            public Subscription(RecordingTransport owner, Func<KafkaEnvelopeRecord, Task> handler)
            {
                _owner = owner;
                _handler = handler;
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                    return ValueTask.CompletedTask;

                _owner.Unsubscribe(_handler);
                return ValueTask.CompletedTask;
            }
        }
    }
}
