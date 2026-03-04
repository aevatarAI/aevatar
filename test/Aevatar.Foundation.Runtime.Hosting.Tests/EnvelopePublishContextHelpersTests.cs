using System.Diagnostics;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Runtime.Propagation;
using FluentAssertions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class EnvelopePublishContextHelpersTests
{
    [Fact]
    public void ApplyOutboundPublishContext_ShouldApplyPropagationAndPublishMetadata()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Aevatar.Agents",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        var sourceEnvelope = new EventEnvelope();
        sourceEnvelope.Metadata[EnvelopeMetadataKeys.TraceId] = ActivityTraceId.CreateRandom().ToString();
        sourceEnvelope.Metadata[EnvelopeMetadataKeys.TraceSpanId] = ActivitySpanId.CreateRandom().ToString();
        sourceEnvelope.Metadata["custom.key"] = "v1";

        using var activity = AevatarActivitySource.Source.StartActivity("apply-outbound-publish-context-test");
        activity.Should().NotBeNull();
        var outbound = new EventEnvelope();

        EnvelopePublishContextHelpers.ApplyOutboundPublishContext(
            outbound,
            sourceEnvelope,
            new PassthroughEnvelopePropagationPolicy(),
            "actor-1",
            routeTargetCount: 3);

        outbound.Metadata[EnvelopeMetadataKeys.TraceId].Should().Be(activity!.TraceId.ToString());
        outbound.Metadata[EnvelopeMetadataKeys.TraceSpanId].Should().Be(activity.SpanId.ToString());
        outbound.Metadata[EnvelopeMetadataKeys.TraceFlags].Should().Be(((byte)activity.ActivityTraceFlags).ToString("x2"));
        outbound.Metadata[EnvelopeMetadataKeys.SourceActorId].Should().Be("actor-1");
        outbound.Metadata[EnvelopeMetadataKeys.RouteTargetCount].Should().Be("3");
        outbound.Metadata["custom.key"].Should().Be("v1");
    }

    private sealed class PassthroughEnvelopePropagationPolicy : IEnvelopePropagationPolicy
    {
        public void Apply(EventEnvelope outboundEnvelope, EventEnvelope? inboundEnvelope)
        {
            if (inboundEnvelope == null)
                return;

            foreach (var (key, value) in inboundEnvelope.Metadata)
                outboundEnvelope.Metadata[key] = value;
        }
    }
}
