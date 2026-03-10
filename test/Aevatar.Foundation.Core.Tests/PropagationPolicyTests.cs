using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Core.Propagation;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.Foundation.Core.Tests;

public sealed class PropagationPolicyTests
{
    private static readonly IEnvelopePropagationPolicy Policy =
        new DefaultEnvelopePropagationPolicy(new DefaultCorrelationLinkPolicy());

    [Fact]
    public void Apply_ShouldPropagateCorrelationAndSetOneHopCausation()
    {
        var inbound = new EventEnvelope
        {
            Id = "evt-in-1",
            Payload = Any.Pack(new PingEvent { Message = "in" }),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "corr-1",
            },
        };
        inbound.Propagation.Baggage["tenant"] = "acme";
        inbound.Propagation.CausationEventId = "old";
        inbound.Propagation.Baggage["command.id"] = "cmd-1";

        var outbound = new EventEnvelope
        {
            Id = "evt-out-1",
            Payload = Any.Pack(new PongEvent { Reply = "out" }),
        };

        Policy.Apply(outbound, inbound);

        outbound.Propagation.CorrelationId.ShouldBe("corr-1");
        outbound.Propagation.Baggage["tenant"].ShouldBe("acme");
        outbound.Propagation.CausationEventId.ShouldBe("evt-in-1");
        outbound.Propagation.Baggage["command.id"].ShouldBe("cmd-1");
    }

    [Fact]
    public void Apply_ShouldNotOverrideExplicitOutboundCorrelationId()
    {
        var inbound = new EventEnvelope
        {
            Id = "evt-in-2",
            Payload = Any.Pack(new PingEvent { Message = "in" }),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "corr-inbound",
            },
        };

        var outbound = new EventEnvelope
        {
            Id = "evt-out-2",
            Payload = Any.Pack(new PongEvent { Reply = "out" }),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "corr-explicit",
            },
        };

        Policy.Apply(outbound, inbound);

        outbound.Propagation.CorrelationId.ShouldBe("corr-explicit");
        outbound.Propagation.CausationEventId.ShouldBe("evt-in-2");
    }

    [Fact]
    public void Apply_ShouldNotPropagateRuntimeDedupOriginMetadata()
    {
        var inbound = new EventEnvelope
        {
            Id = "evt-in-dedup",
            Payload = Any.Pack(new PingEvent { Message = "in" }),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "corr-dedup",
            },
            Runtime = new EnvelopeRuntime
            {
                Deduplication = new DeliveryDeduplication
                {
                    OperationId = "dispatch-op-1",
                },
            },
        };
        inbound.Propagation.Baggage["tenant"] = "acme";

        var outbound = new EventEnvelope
        {
            Id = "evt-out-dedup",
            Payload = Any.Pack(new PongEvent { Reply = "out" }),
        };

        Policy.Apply(outbound, inbound);

        outbound.Runtime.ShouldBeNull();
        outbound.Propagation.Baggage["tenant"].ShouldBe("acme");
        outbound.Propagation.CausationEventId.ShouldBe("evt-in-dedup");
        outbound.Propagation.CorrelationId.ShouldBe("corr-dedup");
    }

    [Fact]
    public void Apply_TwoHop_ShouldLinkToDirectUpstreamOnly()
    {
        var firstInbound = new EventEnvelope
        {
            Id = "evt-1",
            Payload = Any.Pack(new PingEvent { Message = "first" }),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "corr-chain",
            },
        };

        var secondEnvelope = new EventEnvelope
        {
            Id = "evt-2",
            Payload = Any.Pack(new PongEvent { Reply = "second" }),
        };
        Policy.Apply(secondEnvelope, firstInbound);

        var thirdEnvelope = new EventEnvelope
        {
            Id = "evt-3",
            Payload = Any.Pack(new PongEvent { Reply = "third" }),
        };
        Policy.Apply(thirdEnvelope, secondEnvelope);

        thirdEnvelope.Propagation.CorrelationId.ShouldBe("corr-chain");
        thirdEnvelope.Propagation.CausationEventId.ShouldBe("evt-2");
    }
}
