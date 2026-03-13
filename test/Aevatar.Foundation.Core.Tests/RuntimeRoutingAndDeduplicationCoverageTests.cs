using FluentAssertions;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Runtime.Deduplication;

namespace Aevatar.Foundation.Core.Tests;

public sealed class RuntimeRoutingAndDeduplicationCoverageTests
{
    [Fact]
    public async Task MemoryCacheDeduplicator_ShouldRejectDuplicateEventId()
    {
        var deduplicator = new MemoryCacheDeduplicator();

        var first = await deduplicator.TryRecordAsync("evt-1");
        var second = await deduplicator.TryRecordAsync("evt-1");
        var third = await deduplicator.TryRecordAsync("evt-2");

        first.Should().BeTrue();
        second.Should().BeFalse();
        third.Should().BeTrue();
    }

    [Fact]
    public void RuntimeEnvelopeDeduplication_ShouldPreferStableOriginMetadata()
    {
        var envelope = new EventEnvelope
        {
            Id = "env-2",
            Runtime = new EnvelopeRuntime
            {
                Deduplication = new DeliveryDeduplication
                {
                    OperationId = "logical-op-1",
                },
                Retry = new EnvelopeRetryContext
                {
                    OriginEventId = "env-1",
                    Attempt = 2,
                },
            },
        };

        var built = RuntimeEnvelopeDeduplication.TryBuildDedupKey("actor-1", envelope, out var dedupKey);

        built.Should().BeTrue();
        dedupKey.Should().Be("actor-1:logical-op-1:2");
    }

}
