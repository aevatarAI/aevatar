using Aevatar.Foundation.Runtime.Implementations.Orleans;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using FluentAssertions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public class VisitedActorChainTests
{
    [Fact]
    public void AppendDispatchPublisher_WhenTargetIsSelf_ShouldNotAppendVisitedActor()
    {
        var envelope = new EventEnvelope();

        VisitedActorChain.AppendDispatchPublisher(envelope, "actor-a", "actor-a");

        envelope.Runtime?.VisitedActorIds.Should().BeEmpty();
    }

    [Fact]
    public void AppendDispatchPublisher_WhenTargetIsRemote_ShouldAppendVisitedActorOnce()
    {
        var envelope = new EventEnvelope();

        VisitedActorChain.AppendDispatchPublisher(envelope, "actor-a", "actor-b");
        VisitedActorChain.AppendDispatchPublisher(envelope, "actor-a", "actor-c");

        envelope.Runtime!.VisitedActorIds.Should().ContainSingle().Which.Should().Be("actor-a");
    }

    [Fact]
    public void ShouldDrop_WhenChainContainsSelf_ShouldReturnTrue()
    {
        var envelope = new EventEnvelope();
        envelope.Runtime = new EnvelopeRuntime();
        envelope.Runtime.VisitedActorIds.Add("upstream");
        envelope.Runtime.VisitedActorIds.Add("actor-a");

        VisitedActorChain.ShouldDropForReceiver(envelope, "actor-a").Should().BeTrue();
    }

    [Fact]
    public void ShouldDrop_WhenChainDoesNotContainSelf_ShouldReturnFalse()
    {
        var envelope = new EventEnvelope();
        envelope.Runtime = new EnvelopeRuntime();
        envelope.Runtime.VisitedActorIds.Add("upstream");
        envelope.Runtime.VisitedActorIds.Add("actor-b");

        VisitedActorChain.ShouldDropForReceiver(envelope, "actor-a").Should().BeFalse();
    }

    [Fact]
    public void LoopPath_AtoBtoA_ShouldBeDroppedAtReceiverA()
    {
        var envelope = new EventEnvelope();

        VisitedActorChain.AppendDispatchPublisher(envelope, "actor-a", "actor-b");
        VisitedActorChain.ShouldDropForReceiver(envelope, "actor-b").Should().BeFalse();

        VisitedActorChain.AppendDispatchPublisher(envelope, "actor-b", "actor-a");
        VisitedActorChain.ShouldDropForReceiver(envelope, "actor-a").Should().BeTrue();
    }
}
