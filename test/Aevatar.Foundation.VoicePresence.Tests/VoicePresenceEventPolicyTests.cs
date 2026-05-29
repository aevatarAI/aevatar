using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
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

        policy.Evaluate(MakeEnvelope("Alice", now), now, [])
            .Decision
            .ShouldBe(VoicePresenceEventPolicyDecision.Admit);
    }

    [Fact]
    public void Stale_event_beyond_TTL_is_dropped()
    {
        var policy = new VoicePresenceEventPolicy { StaleAfter = TimeSpan.FromSeconds(5) };
        var now = DateTimeOffset.UtcNow;

        policy.Evaluate(MakeEnvelope("Alice", now.AddSeconds(-30)), now, [])
            .Decision
            .ShouldBe(VoicePresenceEventPolicyDecision.DropStale);
    }

    [Fact]
    public void Duplicate_within_window_is_dropped()
    {
        var policy = new VoicePresenceEventPolicy { DedupeWindow = TimeSpan.FromSeconds(2) };
        var now = DateTimeOffset.UtcNow;
        var first = MakeEnvelope("Alice", now);
        var second = MakeEnvelope("Alice", now.AddMilliseconds(500));
        var fence = new List<VoicePresenceEventDedupeFenceEntry>();

        var verdict = policy.Evaluate(first, now, fence);
        verdict.Decision.ShouldBe(VoicePresenceEventPolicyDecision.Admit);
        fence = policy.BuildFence(fence, verdict, now).ToList();
        policy.Evaluate(second, now.AddMilliseconds(500), fence)
            .Decision
            .ShouldBe(VoicePresenceEventPolicyDecision.DropDuplicate);
    }

    [Fact]
    public void Evaluate_is_pure_and_only_drops_duplicates_from_explicit_fence()
    {
        var policy = new VoicePresenceEventPolicy { DedupeWindow = TimeSpan.FromSeconds(2) };
        var now = DateTimeOffset.UtcNow;
        var envelope = MakeEnvelope("Alice", now);

        var first = policy.Evaluate(envelope, now, []);
        first.Decision.ShouldBe(VoicePresenceEventPolicyDecision.Admit);

        var second = policy.Evaluate(envelope, now.AddMilliseconds(100), []);
        second.Decision.ShouldBe(VoicePresenceEventPolicyDecision.Admit);

        var fence = policy.BuildFence([], first, now);
        policy.Evaluate(envelope, now.AddMilliseconds(100), fence)
            .Decision
            .ShouldBe(VoicePresenceEventPolicyDecision.DropDuplicate);
    }

    [Fact]
    public void Source_should_not_restore_internal_recent_event_cache_pattern()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src",
            "Aevatar.Foundation.VoicePresence",
            "Events",
            "VoicePresenceEventPolicy.cs"));

        source.ShouldNotContain("_recentKeys");
        source.ShouldNotContain("RecentEventEntry");
        source.ShouldNotContain("LinkedList<RecentEventEntry>");
    }

    [Fact]
    public void Same_type_different_payload_is_admitted()
    {
        var policy = new VoicePresenceEventPolicy();
        var now = DateTimeOffset.UtcNow;
        var fence = new List<VoicePresenceEventDedupeFenceEntry>();

        var verdict = policy.Evaluate(MakeEnvelope("Alice", now), now, fence);
        verdict.Decision.ShouldBe(VoicePresenceEventPolicyDecision.Admit);
        fence = policy.BuildFence(fence, verdict, now).ToList();
        policy.Evaluate(MakeEnvelope("Bob", now), now.AddMilliseconds(10), fence)
            .Decision
            .ShouldBe(VoicePresenceEventPolicyDecision.Admit);
    }

    [Fact]
    public void Duplicate_outside_window_is_admitted()
    {
        var policy = new VoicePresenceEventPolicy { DedupeWindow = TimeSpan.FromSeconds(2) };
        var now = DateTimeOffset.UtcNow;
        var first = MakeEnvelope("Alice", now);
        var later = MakeEnvelope("Alice", now.AddSeconds(5));
        var fence = new List<VoicePresenceEventDedupeFenceEntry>();

        var verdict = policy.Evaluate(first, now, fence);
        verdict.Decision.ShouldBe(VoicePresenceEventPolicyDecision.Admit);
        fence = policy.BuildFence(fence, verdict, now).ToList();
        policy.Evaluate(later, now.AddSeconds(5), fence).Decision.ShouldBe(VoicePresenceEventPolicyDecision.Admit);
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
        var fence = new List<VoicePresenceEventDedupeFenceEntry>();

        var verdict = policy.Evaluate(envelope, now, fence);
        verdict.Decision.ShouldBe(VoicePresenceEventPolicyDecision.Admit);
        fence = policy.BuildFence(fence, verdict, now).ToList();
        policy.Evaluate(envelope, now.AddMilliseconds(100), fence)
            .Decision
            .ShouldBe(VoicePresenceEventPolicyDecision.DropDuplicate);
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

    private static string FindRepositoryFile(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find repository file.", Path.Combine(relativePath));
    }
}
