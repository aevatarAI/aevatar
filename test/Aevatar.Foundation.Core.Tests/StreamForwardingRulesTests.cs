using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Tests;

public class StreamForwardingRulesTests
{
    [Fact]
    public void CreateHierarchyBinding_ShouldUseDownAndBothDirections()
    {
        var binding = StreamForwardingRules.CreateHierarchyBinding("parent", "child");

        binding.SourceStreamId.Should().Be("parent");
        binding.TargetStreamId.Should().Be("child");
        binding.ForwardingMode.Should().Be(StreamForwardingMode.HandleThenForward);
        binding.DirectionFilter.SetEquals([EventDirection.Down, EventDirection.Both]).Should().BeTrue();
    }

    [Fact]
    public void Matches_WhenDirectionFilteredOut_ShouldReturnFalse()
    {
        var envelope = new EventEnvelope { Direction = EventDirection.Up };
        var binding = StreamForwardingRules.CreateHierarchyBinding("source", "target");

        StreamForwardingRules.Matches(binding, envelope).Should().BeFalse();
    }

    [Fact]
    public void Matches_WhenTypeFilterConfigured_ShouldMatchByTypeUrl()
    {
        var envelope = new EventEnvelope
        {
            Direction = EventDirection.Down,
            Payload = Any.Pack(new StringValue { Value = "x" }),
        };
        var binding = StreamForwardingRules.CreateHierarchyBinding("source", "target");
        binding.EventTypeFilter.Add(envelope.Payload!.TypeUrl);

        StreamForwardingRules.Matches(binding, envelope).Should().BeTrue();
    }

    [Fact]
    public void IsTargetDispatchAllowed_ShouldSkipSelfAndLoopTargets()
    {
        var envelope = new EventEnvelope();
        envelope.Metadata[PublisherChainMetadata.PublishersMetadataKey] = "a,b,c";

        StreamForwardingRules.IsTargetDispatchAllowed("a", "a", envelope).Should().BeFalse();
        StreamForwardingRules.IsTargetDispatchAllowed("a", "b", envelope).Should().BeFalse();
        StreamForwardingRules.IsTargetDispatchAllowed("a", "d", envelope).Should().BeTrue();
    }

    [Fact]
    public void BuildForwardedEnvelope_ShouldStampForwardingMetadata()
    {
        var envelope = new EventEnvelope
        {
            Direction = EventDirection.Down,
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        };

        var forwarded = StreamForwardingRules.BuildForwardedEnvelope(
            envelope,
            "root",
            "child",
            StreamForwardingMode.TransitOnly);

        forwarded.Metadata[PublisherChainMetadata.PublishersMetadataKey].Should().Be("root");
        forwarded.Metadata[StreamForwardingEnvelopeMetadata.ForwardedKey]
            .Should().Be(StreamForwardingEnvelopeMetadata.ForwardedValue);
        forwarded.Metadata[StreamForwardingEnvelopeMetadata.ForwardSourceKey].Should().Be("root");
        forwarded.Metadata[StreamForwardingEnvelopeMetadata.ForwardTargetKey].Should().Be("child");
        forwarded.Metadata[StreamForwardingEnvelopeMetadata.ForwardModeKey]
            .Should().Be(StreamForwardingEnvelopeMetadata.ForwardModeTransit);
        forwarded.Payload!.Unpack<StringValue>().Value.Should().Be("payload");
    }

    [Fact]
    public void TryBuildForwardedEnvelope_WhenBindingIsValid_ShouldReturnStampedEnvelope()
    {
        var envelope = new EventEnvelope
        {
            Direction = EventDirection.Down,
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        };
        var binding = StreamForwardingRules.CreateHierarchyBinding("root", "child");

        var matched = StreamForwardingRules.TryBuildForwardedEnvelope(
            "root",
            binding,
            envelope,
            out var forwarded);

        matched.Should().BeTrue();
        forwarded.Should().NotBeNull();
        forwarded!.Metadata[StreamForwardingEnvelopeMetadata.ForwardTargetKey].Should().Be("child");
    }

    [Fact]
    public void ShouldSkipTransitOnlyHandling_WhenTransitEnvelopeTargetsSelf_ShouldReturnTrue()
    {
        var envelope = new EventEnvelope { Direction = EventDirection.Both };
        envelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardedKey] =
            StreamForwardingEnvelopeMetadata.ForwardedValue;
        envelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardTargetKey] = "self";
        envelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardModeKey] =
            StreamForwardingEnvelopeMetadata.ForwardModeTransit;

        StreamForwardingRules.ShouldSkipTransitOnlyHandling("self", envelope).Should().BeTrue();
    }

    [Fact]
    public void ShouldSkipTransitOnlyHandling_WhenHandleMode_ShouldReturnFalse()
    {
        var envelope = new EventEnvelope { Direction = EventDirection.Down };
        envelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardedKey] =
            StreamForwardingEnvelopeMetadata.ForwardedValue;
        envelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardTargetKey] = "self";
        envelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardModeKey] =
            StreamForwardingEnvelopeMetadata.ForwardModeHandle;

        StreamForwardingRules.ShouldSkipTransitOnlyHandling("self", envelope).Should().BeFalse();
    }
}
