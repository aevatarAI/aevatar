using Aevatar.GAgents.NyxidChat.Relay;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.Tests;

public sealed class NyxRelayBridgeIdempotencyGuardTests
{
    [Fact]
    public void TryClaim_ShouldReturnTrueOnFirstCallAndFalseOnSecond()
    {
        var guard = new NyxRelayBridgeIdempotencyGuard();

        guard.TryClaim("key-1").Should().BeTrue();
        guard.TryClaim("key-1").Should().BeFalse();
    }

    [Fact]
    public void TryClaim_ShouldAdmitDistinctKeys()
    {
        var guard = new NyxRelayBridgeIdempotencyGuard();

        guard.TryClaim("key-a").Should().BeTrue();
        guard.TryClaim("key-b").Should().BeTrue();
    }

    [Fact]
    public async Task TryClaim_UnderConcurrentCallers_SucceedsExactlyOnce()
    {
        var guard = new NyxRelayBridgeIdempotencyGuard();
        const int callers = 32;
        using var gate = new ManualResetEventSlim(false);

        async Task<bool> ClaimOnceAsync()
        {
            await Task.Yield();
            gate.Wait();
            return guard.TryClaim("shared-key");
        }

        var tasks = Enumerable.Range(0, callers).Select(_ => Task.Run(ClaimOnceAsync)).ToArray();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        results.Count(won => won).Should().Be(1,
            because: "exactly one concurrent caller must win the atomic first-writer claim");
    }

    [Fact]
    public void TryClaim_ShouldReclaimAfterTtlExpiry()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 4, 23, 0, 0, 0, TimeSpan.Zero));
        var guard = new NyxRelayBridgeIdempotencyGuard(TimeSpan.FromMinutes(5), timeProvider);

        guard.TryClaim("ephemeral").Should().BeTrue();
        guard.TryClaim("ephemeral").Should().BeFalse();

        timeProvider.Advance(TimeSpan.FromMinutes(6));

        guard.TryClaim("ephemeral").Should()
            .BeTrue(because: "TTL has elapsed, a later retry is a fresh callback, not a duplicate");
    }

    [Fact]
    public void TryClaim_ShouldEvictExpiredEntries_ToBoundMemory()
    {
        // Without active eviction, every processed message_id stays in the guard forever.
        // Verify that crossing the sweep interval purges expired keys from the underlying
        // map, so memory is bounded by recent-traffic * TTL rather than total-traffic.
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 4, 23, 0, 0, 0, TimeSpan.Zero));
        var guard = new NyxRelayBridgeIdempotencyGuard(TimeSpan.FromMinutes(10), timeProvider);

        for (var i = 0; i < 500; i++)
            guard.TryClaim($"msg-{i}").Should().BeTrue();

        guard.ClaimCount.Should().Be(500);

        // Past TTL + sweep interval; the next claim must trigger the opportunistic sweep.
        timeProvider.Advance(TimeSpan.FromMinutes(15));
        guard.TryClaim("msg-after").Should().BeTrue();

        guard.ClaimCount.Should().Be(1,
            because: "all 500 expired entries must have been swept, leaving only the fresh claim");
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public MutableTimeProvider(DateTimeOffset initial) => _now = initial;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
