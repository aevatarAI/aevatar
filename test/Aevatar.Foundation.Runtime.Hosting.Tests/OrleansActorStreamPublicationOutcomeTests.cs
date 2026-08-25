using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansActorStreamPublicationOutcomeTests
{
    [Fact]
    public async Task ProduceAsync_WhenOrleansRejectsBeforeAdmission_ShouldExposeNotAdmittedOutcome()
    {
        var transport = Substitute.For<IAsyncStream<EventEnvelope>>();
        transport.OnNextAsync(Arg.Any<EventEnvelope>(), Arg.Any<StreamSequenceToken?>())
            .Returns(Task.FromException(CreateMessageRejectionException()));
        var provider = Substitute.For<global::Orleans.Streams.IStreamProvider>();
        provider.GetStream<EventEnvelope>(Arg.Any<StreamId>()).Returns(transport);
        var stream = new OrleansActorStream(
            "actor-1",
            "aevatar.events",
            provider,
            forwardingRegistry: null);

        Func<Task> act = () => stream.ProduceAsync(new StringValue { Value = "rejected" });

        var failure = await act.Should().ThrowAsync<EventPublicationException>();
        failure.Which.Outcome.Should().Be(EventPublicationFailureOutcome.NotAdmitted);
    }

    private static OrleansMessageRejectionException CreateMessageRejectionException() =>
        (OrleansMessageRejectionException)Activator.CreateInstance(
            typeof(OrleansMessageRejectionException),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["rejected before admission"],
            culture: null)!;
}
