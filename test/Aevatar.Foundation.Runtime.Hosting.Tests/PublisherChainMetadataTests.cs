using Aevatar.Foundation.Runtime.Implementations.Orleans;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using FluentAssertions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public class PublisherChainMetadataTests
{
    [Fact]
    public void BeforeDispatch_WhenTargetIsSelf_ShouldNotAppendPublisherMetadata()
    {
        var envelope = new EventEnvelope();

        PublisherChainMetadata.AppendDispatchPublisher(envelope, "actor-a", "actor-a");

        envelope.Metadata.ContainsKey(PublisherChainMetadata.PublishersMetadataKey).Should().BeFalse();
    }

    [Fact]
    public void BeforeDispatch_WhenTargetIsRemote_ShouldAppendPublisherMetadataOnce()
    {
        var envelope = new EventEnvelope();

        PublisherChainMetadata.AppendDispatchPublisher(envelope, "actor-a", "actor-b");
        PublisherChainMetadata.AppendDispatchPublisher(envelope, "actor-a", "actor-c");

        envelope.Metadata.TryGetValue(PublisherChainMetadata.PublishersMetadataKey, out var chain).Should().BeTrue();
        chain.Should().Be("actor-a");
    }

    [Fact]
    public void ShouldDrop_WhenChainContainsSelf_ShouldReturnTrue()
    {
        var envelope = new EventEnvelope();
        envelope.Metadata[PublisherChainMetadata.PublishersMetadataKey] = "upstream,actor-a";

        PublisherChainMetadata.ShouldDropForReceiver(envelope, "actor-a").Should().BeTrue();
    }

    [Fact]
    public void ShouldDrop_WhenChainDoesNotContainSelf_ShouldReturnFalse()
    {
        var envelope = new EventEnvelope();
        envelope.Metadata[PublisherChainMetadata.PublishersMetadataKey] = "upstream,actor-b";

        PublisherChainMetadata.ShouldDropForReceiver(envelope, "actor-a").Should().BeFalse();
    }

    [Fact]
    public void LoopPath_AtoBtoA_ShouldBeDroppedAtReceiverA()
    {
        var envelope = new EventEnvelope();

        PublisherChainMetadata.AppendDispatchPublisher(envelope, "actor-a", "actor-b");
        PublisherChainMetadata.ShouldDropForReceiver(envelope, "actor-b").Should().BeFalse();

        PublisherChainMetadata.AppendDispatchPublisher(envelope, "actor-b", "actor-a");
        PublisherChainMetadata.ShouldDropForReceiver(envelope, "actor-a").Should().BeTrue();
    }
}
