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
        binding.DirectionFilter.SetEquals([BroadcastDirection.Down, BroadcastDirection.Both]).Should().BeTrue();
    }

    [Fact]
    public void Matches_WhenDirectionFilteredOut_ShouldReturnFalse()
    {
        var envelope = new EventEnvelope
        {
            Route = EnvelopeRouteSemantics.CreateBroadcast(string.Empty, BroadcastDirection.Up),
        };
        var binding = StreamForwardingRules.CreateHierarchyBinding("source", "target");

        StreamForwardingRules.Matches(binding, envelope).Should().BeFalse();
    }

    [Fact]
    public void Matches_WhenTypeFilterConfigured_ShouldMatchByTypeUrl()
    {
        var envelope = new EventEnvelope
        {
            Payload = Any.Pack(new StringValue { Value = "x" }),
            Route = EnvelopeRouteSemantics.CreateBroadcast(string.Empty, BroadcastDirection.Down),
        };
        var binding = StreamForwardingRules.CreateHierarchyBinding("source", "target");
        binding.EventTypeFilter.Add(envelope.Payload!.TypeUrl);

        StreamForwardingRules.Matches(binding, envelope).Should().BeTrue();
    }

    [Fact]
    public void IsTargetDispatchAllowed_ShouldSkipSelfAndLoopTargets()
    {
        var envelope = new EventEnvelope();
        ForwardingVisitChain.AppendIfMissing(envelope, "a");
        ForwardingVisitChain.AppendIfMissing(envelope, "b");
        ForwardingVisitChain.AppendIfMissing(envelope, "c");

        StreamForwardingRules.IsTargetDispatchAllowed("a", "a", envelope).Should().BeFalse();
        StreamForwardingRules.IsTargetDispatchAllowed("a", "b", envelope).Should().BeFalse();
        StreamForwardingRules.IsTargetDispatchAllowed("a", "d", envelope).Should().BeTrue();
    }

    [Fact]
    public void BuildForwardedEnvelope_ShouldStampForwardingState()
    {
        var envelope = new EventEnvelope
        {
            Payload = Any.Pack(new StringValue { Value = "payload" }),
            Route = EnvelopeRouteSemantics.CreateBroadcast(string.Empty, BroadcastDirection.Down),
        };

        var forwarded = StreamForwardingRules.BuildForwardedEnvelope(
            envelope,
            "root",
            "child",
            StreamForwardingMode.TransitOnly);

        ForwardingVisitChain.Contains(forwarded, "root").Should().BeTrue();
        StreamForwardingEnvelopeState.IsForwarded(forwarded).Should().BeTrue();
        StreamForwardingEnvelopeState.GetSourceStreamId(forwarded).Should().Be("root");
        StreamForwardingEnvelopeState.GetTargetStreamId(forwarded).Should().Be("child");
        StreamForwardingEnvelopeState.GetMode(forwarded).Should().Be(StreamForwardingHandleMode.TransitOnly);
        forwarded.Payload!.Unpack<StringValue>().Value.Should().Be("payload");
    }

    [Fact]
    public void TryBuildForwardedEnvelope_WhenBindingIsValid_ShouldReturnStampedEnvelope()
    {
        var envelope = new EventEnvelope
        {
            Payload = Any.Pack(new StringValue { Value = "payload" }),
            Route = EnvelopeRouteSemantics.CreateBroadcast(string.Empty, BroadcastDirection.Down),
        };
        var binding = StreamForwardingRules.CreateHierarchyBinding("root", "child");

        var matched = StreamForwardingRules.TryBuildForwardedEnvelope(
            "root",
            binding,
            envelope,
            out var forwarded);

        matched.Should().BeTrue();
        forwarded.Should().NotBeNull();
        StreamForwardingEnvelopeState.GetTargetStreamId(forwarded!).Should().Be("child");
    }

    [Fact]
    public void ShouldSkipTransitOnlyHandling_WhenTransitEnvelopeTargetsSelf_ShouldReturnTrue()
    {
        var envelope = new EventEnvelope
        {
            Route = EnvelopeRouteSemantics.CreateBroadcast(string.Empty, BroadcastDirection.Both),
            Runtime = new EnvelopeRuntime
            {
                Forwarding = new EnvelopeForwardingContext
                {
                    TargetStreamId = "self",
                    Mode = StreamForwardingHandleMode.TransitOnly,
                },
            },
        };

        StreamForwardingRules.ShouldSkipTransitOnlyHandling("self", envelope).Should().BeTrue();
    }

    [Fact]
    public void ShouldSkipTransitOnlyHandling_WhenHandleMode_ShouldReturnFalse()
    {
        var envelope = new EventEnvelope
        {
            Route = EnvelopeRouteSemantics.CreateBroadcast(string.Empty, BroadcastDirection.Down),
            Runtime = new EnvelopeRuntime
            {
                Forwarding = new EnvelopeForwardingContext
                {
                    TargetStreamId = "self",
                    Mode = StreamForwardingHandleMode.HandleThenForward,
                },
            },
        };

        StreamForwardingRules.ShouldSkipTransitOnlyHandling("self", envelope).Should().BeFalse();
    }
}
