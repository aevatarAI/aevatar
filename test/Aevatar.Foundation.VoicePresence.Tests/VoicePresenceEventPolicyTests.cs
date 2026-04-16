using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Events;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class VoicePresenceEventPolicyTests
{
    [Fact]
    public void Fresh_first_event_is_admitted()
    {
        var policy = new VoicePresenceEventPolicy();
        var now = DateTimeOffset.UtcNow;

        policy.Evaluate(MakeEnvelope("Alice", now), now)
            .ShouldBe(VoicePresenceEventPolicyDecision.Admit);
    }

    [Fact]
    public void Stale_event_beyond_TTL_is_dropped()
    {
        var policy = new VoicePresenceEventPolicy { StaleAfter = TimeSpan.FromSeconds(5) };
        var now = DateTimeOffset.UtcNow;

        policy.Evaluate(MakeEnvelope("Alice", now.AddSeconds(-30)), now)
            .ShouldBe(VoicePresenceEventPolicyDecision.DropStale);
    }

    [Fact]
    public void Duplicate_within_window_is_dropped()
    {
        var policy = new VoicePresenceEventPolicy { DedupeWindow = TimeSpan.FromSeconds(2) };
        var now = DateTimeOffset.UtcNow;
        var first = MakeEnvelope("Alice", now);
        var second = MakeEnvelope("Alice", now.AddMilliseconds(500));

        policy.Evaluate(first, now).ShouldBe(VoicePresenceEventPolicyDecision.Admit);
        policy.Evaluate(second, now.AddMilliseconds(500))
            .ShouldBe(VoicePresenceEventPolicyDecision.DropDuplicate);
    }

    [Fact]
    public void Same_type_different_payload_is_admitted()
    {
        var policy = new VoicePresenceEventPolicy();
        var now = DateTimeOffset.UtcNow;

        policy.Evaluate(MakeEnvelope("Alice", now), now)
            .ShouldBe(VoicePresenceEventPolicyDecision.Admit);
        policy.Evaluate(MakeEnvelope("Bob", now), now.AddMilliseconds(10))
            .ShouldBe(VoicePresenceEventPolicyDecision.Admit);
    }

    [Fact]
    public void Duplicate_outside_window_is_admitted()
    {
        var policy = new VoicePresenceEventPolicy { DedupeWindow = TimeSpan.FromSeconds(2) };
        var now = DateTimeOffset.UtcNow;
        var first = MakeEnvelope("Alice", now);
        var later = MakeEnvelope("Alice", now.AddSeconds(5));

        policy.Evaluate(first, now).ShouldBe(VoicePresenceEventPolicyDecision.Admit);
        policy.Evaluate(later, now.AddSeconds(5)).ShouldBe(VoicePresenceEventPolicyDecision.Admit);
    }

    [Fact]
    public void Null_payload_event_should_still_be_admitted_and_deduped()
    {
        var policy = new VoicePresenceEventPolicy();
        var now = DateTimeOffset.UtcNow;
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(now),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("voice-agent", TopologyAudience.Self),
        };

        policy.Evaluate(envelope, now).ShouldBe(VoicePresenceEventPolicyDecision.Admit);
        policy.Evaluate(envelope, now.AddMilliseconds(100)).ShouldBe(VoicePresenceEventPolicyDecision.DropDuplicate);
    }

    private static EventEnvelope MakeEnvelope(string person, DateTimeOffset observedAt)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(observedAt),
            Payload = Any.Pack(new StringValue { Value = person }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("voice-agent", TopologyAudience.Self),
        };
    }
}
