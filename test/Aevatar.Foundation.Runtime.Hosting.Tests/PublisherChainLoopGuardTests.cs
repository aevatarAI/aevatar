using Aevatar.Foundation.Runtime.Implementations.Orleans;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Propagation;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public class PublisherChainLoopGuardTests
{
    [Fact]
    public void BeforeDispatch_WhenTargetIsSelf_ShouldNotAppendPublisherMetadata()
    {
        var guard = new PublisherChainLoopGuard();
        var envelope = new EventEnvelope();

        guard.BeforeDispatch("actor-a", "actor-a", envelope);

        envelope.Metadata.ContainsKey(OrleansRuntimeConstants.PublishersMetadataKey).Should().BeFalse();
    }

    [Fact]
    public void BeforeDispatch_WhenTargetIsRemote_ShouldAppendPublisherMetadataOnce()
    {
        var guard = new PublisherChainLoopGuard();
        var envelope = new EventEnvelope();

        guard.BeforeDispatch("actor-a", "actor-b", envelope);
        guard.BeforeDispatch("actor-a", "actor-c", envelope);

        envelope.Metadata.TryGetValue(OrleansRuntimeConstants.PublishersMetadataKey, out var chain).Should().BeTrue();
        chain.Should().Be("actor-a");
    }

    [Fact]
    public void ShouldDrop_WhenChainContainsSelf_ShouldReturnTrue()
    {
        var guard = new PublisherChainLoopGuard();
        var envelope = new EventEnvelope();
        envelope.Metadata[OrleansRuntimeConstants.PublishersMetadataKey] = "upstream,actor-a";

        guard.ShouldDrop("actor-a", envelope).Should().BeTrue();
    }

    [Fact]
    public void ShouldDrop_WhenChainDoesNotContainSelf_ShouldReturnFalse()
    {
        var guard = new PublisherChainLoopGuard();
        var envelope = new EventEnvelope();
        envelope.Metadata[OrleansRuntimeConstants.PublishersMetadataKey] = "upstream,actor-b";

        guard.ShouldDrop("actor-a", envelope).Should().BeFalse();
    }

    [Fact]
    public void LoopPath_AtoBtoA_ShouldBeDroppedAtReceiverA()
    {
        var guard = new PublisherChainLoopGuard();
        var envelope = new EventEnvelope();

        guard.BeforeDispatch("actor-a", "actor-b", envelope);
        guard.ShouldDrop("actor-b", envelope).Should().BeFalse();

        guard.BeforeDispatch("actor-b", "actor-a", envelope);
        guard.ShouldDrop("actor-a", envelope).Should().BeTrue();
    }
}
