using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;
using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansMassTransitQueueAdapterCoverageTests
{
    [Fact]
    public async Task OrleansMassTransitQueueAdapter_ShouldPublishSupportedMessagesOnly()
    {
        var transport = new RecordingEnvelopeTransport();
        var mapper = new HashRingBasedStreamQueueMapper(
            new HashRingStreamQueueMapperOptions { TotalQueueCount = 1 },
            "provider-a");
        var adapter = new OrleansMassTransitQueueAdapter(
            "provider-a",
            transport,
            mapper,
            "aevatar.actor.events");
        var streamId = StreamId.Create("aevatar.actor.events", "actor-1");

        var envelope = new EventEnvelope
        {
            Id = "evt-a",
            Payload = Any.Pack(new StringValue { Value = "envelope" }),
            Direction = EventDirection.Down,
        };
        await adapter.QueueMessageBatchAsync(
            streamId,
            new object[]
            {
                envelope,
                new StringValue { Value = "protobuf" },
                new object(),
            },
            new EventSequenceTokenV2(1),
            []);

        transport.Published.Should().HaveCount(2);
        transport.Published[0].streamNamespace.Should().Be("aevatar.actor.events");
        transport.Published[0].streamId.Should().Be("actor-1");

        var first = EventEnvelope.Parser.ParseFrom(transport.Published[0].payload);
        first.Id.Should().Be("evt-a");
        first.Payload!.Unpack<StringValue>().Value.Should().Be("envelope");

        var second = EventEnvelope.Parser.ParseFrom(transport.Published[1].payload);
        second.Payload!.Unpack<StringValue>().Value.Should().Be("protobuf");
    }

    [Fact]
    public void OrleansMassTransitQueueAdapter_CreateReceiver_ShouldReturnReceiver()
    {
        var mapper = new HashRingBasedStreamQueueMapper(
            new HashRingStreamQueueMapperOptions { TotalQueueCount = 1 },
            "provider-a");
        var queueId = mapper.GetAllQueues().Single();
        var adapter = new OrleansMassTransitQueueAdapter(
            "provider-a",
            new RecordingEnvelopeTransport(),
            mapper,
            "aevatar.actor.events");

        var receiver = adapter.CreateReceiver(queueId);

        receiver.Should().BeOfType<OrleansMassTransitQueueAdapterReceiver>();
        adapter.Name.Should().Be("provider-a");
        adapter.IsRewindable.Should().BeFalse();
        adapter.Direction.Should().Be(StreamProviderDirection.ReadWrite);
    }

    [Fact]
    public async Task OrleansMassTransitQueueAdapterFactory_ShouldBuildAdapterAndInfrastructure()
    {
        var transport = new RecordingEnvelopeTransport();
        var factory = new OrleansMassTransitQueueAdapterFactory(
            new AevatarOrleansRuntimeOptions
            {
                StreamProviderName = "provider-b",
                QueueCount = 0,
                QueueCacheSize = 1,
                ActorEventNamespace = " ",
            },
            transport,
            NullLoggerFactory.Instance);

        var adapter = await factory.CreateAdapter();
        adapter.Name.Should().Be("provider-b");

        var mapper = factory.GetStreamQueueMapper();
        mapper.GetAllQueues().Should().ContainSingle();

        var cache = factory.GetQueueAdapterCache();
        cache.Should().NotBeNull();

        var queueId = mapper.GetAllQueues().Single();
        var handler1 = await factory.GetDeliveryFailureHandler(queueId);
        var handler2 = await factory.GetDeliveryFailureHandler(queueId);
        handler1.Should().NotBeNull();
        handler2.Should().BeSameAs(handler1);
    }

    [Fact]
    public async Task OrleansMassTransitQueueAdapterFactory_ShouldHonorCustomActorEventNamespace()
    {
        var transport = new RecordingEnvelopeTransport();
        var options = new AevatarOrleansRuntimeOptions
        {
            StreamProviderName = "provider-c",
            QueueCount = 2,
            QueueCacheSize = 256,
            ActorEventNamespace = "custom.actor.events",
        };
        var factory = new OrleansMassTransitQueueAdapterFactory(options, transport, NullLoggerFactory.Instance);
        var adapter = await factory.CreateAdapter();
        var queueId = factory.GetStreamQueueMapper().GetAllQueues().First();
        var receiver = (OrleansMassTransitQueueAdapterReceiver)adapter.CreateReceiver(queueId);

        var field = typeof(OrleansMassTransitQueueAdapterReceiver)
            .GetField("_actorEventNamespace", BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull();
        field!.GetValue(receiver).Should().Be("custom.actor.events");
    }

    private sealed class RecordingEnvelopeTransport : IMassTransitEnvelopeTransport
    {
        public List<(string streamNamespace, string streamId, byte[] payload)> Published { get; } = [];

        public Task PublishAsync(string streamNamespace, string streamId, byte[] payload, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((streamNamespace, streamId, payload));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            Func<MassTransitEnvelopeRecord, Task> handler,
            CancellationToken ct = default)
        {
            _ = handler;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(NoOpAsyncDisposable.Instance);
        }
    }

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public static NoOpAsyncDisposable Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
