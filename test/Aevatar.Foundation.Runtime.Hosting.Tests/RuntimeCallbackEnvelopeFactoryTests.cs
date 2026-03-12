using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Callbacks;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeCallbackEnvelopeFactoryTests
{
    [Fact]
    public void CreateFiredEnvelope_ShouldPublishSelfContinuationWithoutOverwritingPublisher()
    {
        var triggerEnvelope = new EventEnvelope
        {
            Id = "origin-envelope",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1)),
            Payload = Any.Pack(new StringValue { Value = "retry" }),
            Route = EnvelopeRouteSemantics.CreateDirect("child-actor", "workflow-parent"),
            Propagation = new EnvelopePropagation
            {
                Baggage =
                {
                    ["custom.trace_id"] = "trace-1",
                },
            },
        };

        var fired = RuntimeCallbackEnvelopeFactory.CreateFiredEnvelope(
            actorId: "workflow-parent",
            callbackId: "retry-callback",
            generation: 3,
            fireIndex: 1,
            triggerEnvelope);

        fired.Id.Should().NotBe("origin-envelope");
        fired.Timestamp.Should().NotBeNull();
        fired.Route!.PublisherActorId.Should().Be("child-actor");
        fired.Route.GetBroadcastDirection().Should().Be(BroadcastDirection.Self);
        fired.Route.IsBroadcast().Should().BeTrue();
        fired.Route.GetTargetActorId().Should().BeEmpty();
        fired.Propagation!.Baggage["custom.trace_id"].Should().Be("trace-1");
        fired.Runtime!.Callback!.CallbackId.Should().Be("retry-callback");
        fired.Runtime.Callback.Generation.Should().Be(3);
        fired.Runtime.Callback.FireIndex.Should().Be(1);
        fired.Runtime.Callback.FiredAtUnixTimeMs.Should().BePositive();
    }

    [Fact]
    public void CreateScheduledEnvelope_ShouldPreserveDirectRoute_WhenConfiguredForEnvelopeRedelivery()
    {
        var triggerEnvelope = new EventEnvelope
        {
            Id = "retry-envelope",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1)),
            Payload = Any.Pack(new StringValue { Value = "retry" }),
            Route = EnvelopeRouteSemantics.CreateDirect("child-actor", "workflow-parent"),
        };

        var redelivered = RuntimeCallbackEnvelopeFactory.CreateScheduledEnvelope(
            actorId: "workflow-parent",
            callbackId: "retry-callback",
            generation: 4,
            fireIndex: 2,
            triggerEnvelope,
            RuntimeCallbackDeliveryMode.EnvelopeRedelivery);

        redelivered.Route!.PublisherActorId.Should().Be("child-actor");
        redelivered.Route.IsDirect().Should().BeTrue();
        redelivered.Route.GetTargetActorId().Should().Be("workflow-parent");
        redelivered.Id.Should().Be("retry-envelope");
        redelivered.Runtime?.Callback.Should().BeNull();
    }
}
