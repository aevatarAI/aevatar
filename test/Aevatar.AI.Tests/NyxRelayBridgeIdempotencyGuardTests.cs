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

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public MutableTimeProvider(DateTimeOffset initial) => _now = initial;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
