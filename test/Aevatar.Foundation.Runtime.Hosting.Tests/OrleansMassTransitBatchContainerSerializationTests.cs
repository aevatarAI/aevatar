using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansMassTransitBatchContainerSerializationTests
{
    [Fact]
    public void OrleansMassTransitBatchContainer_ShouldRoundtripThroughOrleansSerializer()
    {
        using var serviceProvider = new ServiceCollection()
            .AddSerializer(serializerBuilder => serializerBuilder.AddProtobufSerializer())
            .BuildServiceProvider();

        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var batch = new OrleansMassTransitBatchContainer(
            StreamId.Create("aevatar.actor.events", "actor-1"),
            new EventEnvelope
            {
                Id = "evt-1",
                Payload = Any.Pack(new StringValue { Value = "payload-1" }),
            },
            new EventSequenceTokenV2(42));

        var copy = serializer.Deserialize<OrleansMassTransitBatchContainer>(serializer.SerializeToArray(batch));

        copy.StreamId.GetNamespace().Should().Be("aevatar.actor.events");
        copy.StreamId.GetKeyAsString().Should().Be("actor-1");
        copy.SequenceToken.Should().BeOfType<EventSequenceTokenV2>();
        copy.GetEvents<EventEnvelope>().Should().ContainSingle();
        copy.GetEvents<EventEnvelope>().Single().Item1.Id.Should().Be("evt-1");
        copy.GetEvents<EventEnvelope>().Single().Item1.Payload!.Unpack<StringValue>().Value.Should().Be("payload-1");
    }
}
