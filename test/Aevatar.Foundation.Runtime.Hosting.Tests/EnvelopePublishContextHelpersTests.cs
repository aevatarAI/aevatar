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
    public void ApplyOutboundPublishContext_ShouldApplyPropagationAndRuntimeContext()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Aevatar.Agents",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        var sourceEnvelope = new EventEnvelope
        {
            Propagation = new EnvelopePropagation
            {
                Trace = new TraceContext
                {
                    TraceId = ActivityTraceId.CreateRandom().ToString(),
                    SpanId = ActivitySpanId.CreateRandom().ToString(),
                },
                Baggage =
                {
                    ["custom.key"] = "v1",
                },
            },
        };

        using var activity = AevatarActivitySource.Source.StartActivity("apply-outbound-publish-context-test");
        activity.Should().NotBeNull();
        var outbound = new EventEnvelope();

        EnvelopePublishContextHelpers.ApplyOutboundPublishContext(
            outbound,
            sourceEnvelope,
            new PassthroughEnvelopePropagationPolicy(),
            "actor-1",
            routeTargetCount: 3);

        outbound.Propagation!.Trace!.TraceId.Should().Be(activity!.TraceId.ToString());
        outbound.Propagation.Trace.SpanId.Should().Be(activity.SpanId.ToString());
        outbound.Propagation.Trace.TraceFlags.Should().Be(((byte)activity.ActivityTraceFlags).ToString("x2"));
        outbound.Runtime!.SourceActorId.Should().Be("actor-1");
        outbound.Runtime.RouteTargetCount.Should().Be(3);
        outbound.Propagation.Baggage["custom.key"].Should().Be("v1");
    }

    private sealed class PassthroughEnvelopePropagationPolicy : IEnvelopePropagationPolicy
    {
        public void Apply(EventEnvelope outboundEnvelope, EventEnvelope? inboundEnvelope)
        {
            if (inboundEnvelope == null)
                return;

            if (inboundEnvelope.Propagation != null)
                outboundEnvelope.Propagation = inboundEnvelope.Propagation.Clone();
        }
    }
}
