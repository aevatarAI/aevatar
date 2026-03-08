using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Callbacks;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeCallbackEnvelopeFactoryTests
{
    [Fact]
    public void CreateFiredEnvelope_ShouldPreserveOriginalEnvelopeSemantics()
    {
        var triggerEnvelope = new EventEnvelope
        {
            Id = "origin-envelope",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1)),
            Payload = Any.Pack(new StringValue { Value = "retry" }),
            PublisherId = "child-actor",
            TargetActorId = "workflow-parent",
            Direction = EventDirection.Down,
        };
        triggerEnvelope.Metadata["custom.trace_id"] = "trace-1";

        var fired = RuntimeCallbackEnvelopeFactory.CreateFiredEnvelope(
            actorId: "workflow-parent",
            callbackId: "retry-callback",
            generation: 3,
            fireIndex: 1,
            triggerEnvelope);

        fired.Id.Should().NotBe("origin-envelope");
        fired.Timestamp.Should().NotBeNull();
        fired.PublisherId.Should().Be("child-actor");
        fired.TargetActorId.Should().Be("workflow-parent");
        fired.Direction.Should().Be(EventDirection.Down);
        fired.Metadata["custom.trace_id"].Should().Be("trace-1");
        fired.Metadata[RuntimeCallbackMetadataKeys.CallbackId].Should().Be("retry-callback");
        fired.Metadata[RuntimeCallbackMetadataKeys.CallbackGeneration].Should().Be("3");
        fired.Metadata[RuntimeCallbackMetadataKeys.CallbackFireIndex].Should().Be("1");
        fired.Metadata.ContainsKey(RuntimeCallbackMetadataKeys.CallbackFiredAtUnixTimeMs).Should().BeTrue();
    }

    [Fact]
    public void CreateScheduledEnvelope_ShouldPreservePublisher_WhenConfiguredForEnvelopeRedelivery()
    {
        var triggerEnvelope = new EventEnvelope
        {
            Id = "retry-envelope",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1)),
            Payload = Any.Pack(new StringValue { Value = "retry" }),
            PublisherId = "child-actor",
            TargetActorId = "workflow-parent",
            Direction = EventDirection.Down,
        };

        var redelivered = RuntimeCallbackEnvelopeFactory.CreateScheduledEnvelope(
            actorId: "workflow-parent",
            callbackId: "retry-callback",
            generation: 4,
            fireIndex: 2,
            triggerEnvelope,
            RuntimeCallbackDeliveryMode.EnvelopeRedelivery);

        redelivered.PublisherId.Should().Be("child-actor");
        redelivered.Direction.Should().Be(EventDirection.Down);
        redelivered.TargetActorId.Should().Be("workflow-parent");
        redelivered.Id.Should().Be("retry-envelope");
        redelivered.Metadata.ContainsKey(RuntimeCallbackMetadataKeys.CallbackId).Should().BeFalse();
    }
}
