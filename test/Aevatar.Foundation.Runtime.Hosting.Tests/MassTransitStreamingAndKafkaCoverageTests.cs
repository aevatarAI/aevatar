using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class MassTransitStreamingAndKafkaCoverageTests
{
    [Fact]
    public async Task MassTransitStream_ProduceAsync_ShouldValidateAndPublishEnvelopes()
    {
        var transport = new RecordingEnvelopeTransport();
        var stream = new MassTransitStream("actor-1", "aevatar.events", transport);

        var nullAct = async () => await stream.ProduceAsync<StringValue>(null!);
        await nullAct.Should().ThrowAsync<ArgumentNullException>();

        var envelope = new EventEnvelope
        {
            Id = "evt-1",
            Payload = Any.Pack(new StringValue { Value = "direct" }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
        };
        await stream.ProduceAsync(envelope);
        await stream.ProduceAsync(new StringValue { Value = "wrapped" });

        transport.Published.Should().HaveCount(2);
        transport.Published[0].streamNamespace.Should().Be("aevatar.events");
        transport.Published[0].streamId.Should().Be("actor-1");
        var firstEnvelope = EventEnvelope.Parser.ParseFrom(transport.Published[0].payload);
        firstEnvelope.Id.Should().Be("evt-1");
        firstEnvelope.Payload!.Unpack<StringValue>().Value.Should().Be("direct");

        var secondEnvelope = EventEnvelope.Parser.ParseFrom(transport.Published[1].payload);
        secondEnvelope.Payload!.Unpack<StringValue>().Value.Should().Be("wrapped");
    }

    [Fact]
    public async Task MassTransitStream_SubscribeAsync_ShouldFilterAndDispatchByType()
    {
        var transport = new RecordingEnvelopeTransport();
        var stream = new MassTransitStream("actor-2", "aevatar.events", transport);
        var receivedEnvelopes = new List<EventEnvelope>();
        var receivedStrings = new List<string>();

        await using var envelopeSubscription = await stream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            receivedEnvelopes.Add(envelope.Clone());
            return Task.CompletedTask;
        });

        await using var stringSubscription = await stream.SubscribeAsync<StringValue>(value =>
        {
            receivedStrings.Add(value.Value);
            return Task.CompletedTask;
        });

        await transport.PushAsync(new MassTransitEnvelopeRecord
        {
            StreamNamespace = "another.events",
            StreamId = "actor-2",
            Payload = new byte[] { 1, 2, 3 },
        });
        await transport.PushAsync(new MassTransitEnvelopeRecord
        {
            StreamNamespace = "aevatar.events",
            StreamId = "another-actor",
            Payload = new byte[] { 1, 2, 3 },
        });
        await transport.PushAsync(new MassTransitEnvelopeRecord
        {
            StreamNamespace = "aevatar.events",
            StreamId = "actor-2",
            Payload = new byte[] { 1, 2, 3 },
        });

        var textEnvelope = new EventEnvelope
        {
            Id = "evt-2",
            Payload = Any.Pack(new StringValue { Value = "ok" }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
        };
        await transport.PushAsync(new MassTransitEnvelopeRecord
        {
            StreamNamespace = "aevatar.events",
            StreamId = "actor-2",
            Payload = textEnvelope.ToByteArray(),
        });

        var intEnvelope = new EventEnvelope
        {
            Id = "evt-3",
            Payload = Any.Pack(new Int32Value { Value = 7 }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
        };
        await transport.PushAsync(new MassTransitEnvelopeRecord
        {
            StreamNamespace = "aevatar.events",
            StreamId = "actor-2",
            Payload = intEnvelope.ToByteArray(),
        });

        receivedEnvelopes.Should().HaveCount(2);
        receivedEnvelopes.Select(x => x.Id).Should().BeEquivalentTo("evt-2", "evt-3");
        receivedStrings.Should().ContainSingle().Which.Should().Be("ok");
    }

    [Fact]
    public async Task MassTransitActorEventSubscriptionProvider_ShouldReuseSingleTransportSubscription()
    {
        var transport = new RecordingEnvelopeTransport();
        var provider = new MassTransitActorEventSubscriptionProvider(
            transport,
            new MassTransitStreamOptions
            {
                StreamNamespace = "aevatar.events",
            });
        var actor1Messages = new List<string>();
        var actor2Messages = new List<string>();

        await using var actor1Subscription = await provider.SubscribeAsync<StringValue>("actor-1", value =>
        {
            actor1Messages.Add(value.Value);
            return Task.CompletedTask;
        });
        await using var actor2Subscription = await provider.SubscribeAsync<StringValue>("actor-2", value =>
        {
            actor2Messages.Add(value.Value);
            return Task.CompletedTask;
        });

        transport.SubscribeCallCount.Should().Be(1);
        transport.ActiveSubscriptionCount.Should().Be(1);

        await transport.PushAsync(CreateRecord("actor-1", "a-1"));
        await transport.PushAsync(CreateRecord("actor-2", "a-2"));
        await transport.PushAsync(CreateRecord("actor-3", "a-3"));

        actor1Messages.Should().ContainSingle().Which.Should().Be("a-1");
        actor2Messages.Should().ContainSingle().Which.Should().Be("a-2");
    }

    [Fact]
    public async Task MassTransitActorEventSubscriptionProvider_ShouldFanOutLocallyAndReleaseTransportWhenIdle()
    {
        var transport = new RecordingEnvelopeTransport();
        var provider = new MassTransitActorEventSubscriptionProvider(
            transport,
            new MassTransitStreamOptions
            {
                StreamNamespace = "aevatar.events",
            });
        var actorMessages = new List<string>();

        var subscription1 = await provider.SubscribeAsync<StringValue>("actor-1", value =>
        {
            actorMessages.Add("first:" + value.Value);
            return Task.CompletedTask;
        });
        var subscription2 = await provider.SubscribeAsync<StringValue>("actor-1", value =>
        {
            actorMessages.Add("second:" + value.Value);
            return Task.CompletedTask;
        });

        transport.SubscribeCallCount.Should().Be(1);
        transport.ActiveSubscriptionCount.Should().Be(1);

        await transport.PushAsync(CreateRecord("actor-1", "shared"));

        actorMessages.Should().BeEquivalentTo("first:shared", "second:shared");

        await subscription1.DisposeAsync();
        transport.ActiveSubscriptionCount.Should().Be(1);

        await subscription2.DisposeAsync();
        transport.ActiveSubscriptionCount.Should().Be(0);
    }

    [Fact]
    public async Task MassTransitKafkaEnvelopeTransport_ShouldValidateArgumentsAndWireDispatcher()
    {
        var producer = DispatchProxy.Create<ITopicProducer<KafkaStreamEnvelopeMessage>, TopicProducerProxy>();
        var producerProxy = (TopicProducerProxy)(object)producer;
        var dispatcher = new MassTransitKafkaEnvelopeDispatcher();
        var transport = new MassTransitKafkaEnvelopeTransport(producer, dispatcher);

        await Assert.ThrowsAsync<ArgumentException>(() => transport.PublishAsync("", "actor", new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.PublishAsync("events", "", new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => transport.PublishAsync("events", "actor", null!));

        await transport.PublishAsync("aevatar.events", "actor-3", new byte[] { 3, 2, 1 });
        producerProxy.ProduceCallCount.Should().Be(1);
        producerProxy.ProducedMessage.Should().NotBeNull();
        producerProxy.ProducedMessage!.StreamNamespace.Should().Be("aevatar.events");
        producerProxy.ProducedMessage.StreamId.Should().Be("actor-3");
        producerProxy.ProducedMessage.Payload.Should().Equal(3, 2, 1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            transport.SubscribeAsync(_ => Task.CompletedTask, cts.Token));

        var delivered = new List<MassTransitEnvelopeRecord>();
        await using (await transport.SubscribeAsync(record =>
                     {
                         delivered.Add(record);
                         return Task.CompletedTask;
                     }))
        {
            await dispatcher.DispatchAsync(new MassTransitEnvelopeRecord
            {
                StreamNamespace = "aevatar.events",
                StreamId = "actor-3",
                Payload = new byte[] { 9 },
            });
        }

        delivered.Should().ContainSingle();
        delivered[0].StreamNamespace.Should().Be("aevatar.events");
        delivered[0].StreamId.Should().Be("actor-3");
    }

    [Fact]
    public async Task MassTransitKafkaEnvelopeConsumer_ShouldIgnoreInvalidAndDispatchValidMessage()
    {
        var dispatcher = new MassTransitKafkaEnvelopeDispatcher();
        var consumer = new MassTransitKafkaEnvelopeConsumer(
            dispatcher,
            NullLogger<MassTransitKafkaEnvelopeConsumer>.Instance);
        var delivered = new List<MassTransitEnvelopeRecord>();
        await dispatcher.SubscribeAsync(record =>
        {
            delivered.Add(record);
            return Task.CompletedTask;
        });

        var invalidContext = CreateConsumeContext(new KafkaStreamEnvelopeMessage
        {
            StreamNamespace = "",
            StreamId = "actor-4",
            Payload = new byte[] { 1 },
        });
        await consumer.Consume(invalidContext);
        delivered.Should().BeEmpty();

        var validContext = CreateConsumeContext(new KafkaStreamEnvelopeMessage
        {
            StreamNamespace = "aevatar.events",
            StreamId = "actor-4",
            Payload = new byte[] { 4, 5, 6 },
        });
        await consumer.Consume(validContext);

        delivered.Should().ContainSingle();
        delivered[0].StreamNamespace.Should().Be("aevatar.events");
        delivered[0].StreamId.Should().Be("actor-4");
        delivered[0].Payload.Should().Equal(4, 5, 6);
    }

    private static ConsumeContext<KafkaStreamEnvelopeMessage> CreateConsumeContext(KafkaStreamEnvelopeMessage message)
    {
        var context = DispatchProxy.Create<ConsumeContext<KafkaStreamEnvelopeMessage>, ConsumeContextProxy>();
        var proxy = (ConsumeContextProxy)(object)context;
        proxy.Message = message;
        return context;
    }

    private static MassTransitEnvelopeRecord CreateRecord(string actorId, string value)
    {
        var envelope = new EventEnvelope
        {
            Id = "evt-" + actorId + "-" + value,
            Payload = Any.Pack(new StringValue { Value = value }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
        };

        return new MassTransitEnvelopeRecord
        {
            StreamNamespace = "aevatar.events",
            StreamId = actorId,
            Payload = envelope.ToByteArray(),
        };
    }

    private sealed class RecordingEnvelopeTransport : IMassTransitEnvelopeTransport
    {
        private readonly Lock _lock = new();
        private readonly List<Func<MassTransitEnvelopeRecord, Task>> _handlers = [];

        public List<(string streamNamespace, string streamId, byte[] payload)> Published { get; } = [];

        public int SubscribeCallCount { get; private set; }

        public int ActiveSubscriptionCount
        {
            get
            {
                lock (_lock)
                {
                    return _handlers.Count;
                }
            }
        }

        public Task PublishAsync(
            string streamNamespace,
            string streamId,
            byte[] payload,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((streamNamespace, streamId, payload));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            Func<MassTransitEnvelopeRecord, Task> handler,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ct.ThrowIfCancellationRequested();

            lock (_lock)
            {
                SubscribeCallCount++;
                _handlers.Add(handler);
            }

            return Task.FromResult<IAsyncDisposable>(new Subscription(this, handler));
        }

        public async Task PushAsync(MassTransitEnvelopeRecord record)
        {
            Func<MassTransitEnvelopeRecord, Task>[] handlers;
            lock (_lock)
            {
                handlers = _handlers.ToArray();
            }

            foreach (var handler in handlers)
            {
                await handler(record);
            }
        }

        private void Unsubscribe(Func<MassTransitEnvelopeRecord, Task> handler)
        {
            lock (_lock)
            {
                _handlers.Remove(handler);
            }
        }

        private sealed class Subscription : IAsyncDisposable
        {
            private readonly RecordingEnvelopeTransport _owner;
            private readonly Func<MassTransitEnvelopeRecord, Task> _handler;
            private int _disposed;

            public Subscription(RecordingEnvelopeTransport owner, Func<MassTransitEnvelopeRecord, Task> handler)
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

    private class TopicProducerProxy : DispatchProxy
    {
        public int ProduceCallCount { get; private set; }

        public KafkaStreamEnvelopeMessage? ProducedMessage { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "Produce" &&
                args is { Length: > 0 } &&
                args[0] is KafkaStreamEnvelopeMessage message)
            {
                ProduceCallCount++;
                ProducedMessage = message;
                return Task.CompletedTask;
            }

            throw new NotSupportedException($"Unexpected topic producer call: {targetMethod?.Name}");
        }
    }

    private class ConsumeContextProxy : DispatchProxy
    {
        public KafkaStreamEnvelopeMessage Message { get; set; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = args;
            if (targetMethod?.Name == "get_Message")
                return Message;

            return targetMethod?.ReturnType == typeof(Task)
                ? Task.CompletedTask
                : targetMethod?.ReturnType.IsValueType == true
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
        }
    }
}
