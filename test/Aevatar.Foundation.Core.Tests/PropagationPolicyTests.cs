using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Abstractions.Streaming;
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
            CorrelationId = "corr-1",
            Payload = Any.Pack(new PingEvent { Message = "in" }),
        };
        inbound.Metadata["tenant"] = "acme";
        inbound.Metadata["trace.causation_id"] = "old";
        inbound.Metadata["command.id"] = "cmd-1";

        var outbound = new EventEnvelope
        {
            Id = "evt-out-1",
            Payload = Any.Pack(new PongEvent { Reply = "out" }),
        };

        Policy.Apply(outbound, inbound);

        outbound.CorrelationId.ShouldBe("corr-1");
        outbound.Metadata["tenant"].ShouldBe("acme");
        outbound.Metadata[EnvelopeMetadataKeys.TraceCausationId].ShouldBe("evt-in-1");
        outbound.Metadata.ContainsKey("command.id").ShouldBeFalse();
    }

    [Fact]
    public void Apply_ShouldNotOverrideExplicitOutboundCorrelationId()
    {
        var inbound = new EventEnvelope
        {
            Id = "evt-in-2",
            CorrelationId = "corr-inbound",
            Payload = Any.Pack(new PingEvent { Message = "in" }),
        };

        var outbound = new EventEnvelope
        {
            Id = "evt-out-2",
            CorrelationId = "corr-explicit",
            Payload = Any.Pack(new PongEvent { Reply = "out" }),
        };

        Policy.Apply(outbound, inbound);

        outbound.CorrelationId.ShouldBe("corr-explicit");
        outbound.Metadata[EnvelopeMetadataKeys.TraceCausationId].ShouldBe("evt-in-2");
    }

    [Fact]
    public void Apply_TwoHop_ShouldLinkToDirectUpstreamOnly()
    {
        var firstInbound = new EventEnvelope
        {
            Id = "evt-1",
            CorrelationId = "corr-chain",
            Payload = Any.Pack(new PingEvent { Message = "first" }),
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

        thirdEnvelope.CorrelationId.ShouldBe("corr-chain");
        thirdEnvelope.Metadata[EnvelopeMetadataKeys.TraceCausationId].ShouldBe("evt-2");
    }

    [Fact]
    public void Apply_ShouldNotPropagatePublisherChain()
    {
        var inbound = new EventEnvelope
        {
            Id = "evt-in-pub",
            CorrelationId = "corr-pub",
            Payload = Any.Pack(new PingEvent { Message = "in" }),
        };
        inbound.Metadata[PublisherChainMetadata.PublishersMetadataKey] = "parent-actor";

        var outbound = new EventEnvelope
        {
            Id = "evt-out-pub",
            Payload = Any.Pack(new PongEvent { Reply = "out" }),
        };

        Policy.Apply(outbound, inbound);

        outbound.CorrelationId.ShouldBe("corr-pub");
        outbound.Metadata.ContainsKey(PublisherChainMetadata.PublishersMetadataKey).ShouldBeFalse();
    }
}
