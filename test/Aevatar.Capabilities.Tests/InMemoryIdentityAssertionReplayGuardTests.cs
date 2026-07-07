using Aevatar.Mainnet.Host.Api.Responses;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.Capabilities.Tests;

public sealed class InMemoryIdentityAssertionReplayGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 03, 12, 00, 00, TimeSpan.Zero);

    [Fact]
    public void TryConsume_ShouldReturnTrue_OnFirstUse()
    {
        var time = new FakeTimeProvider(Now);
        var guard = new InMemoryIdentityAssertionReplayGuard(time);

        guard.TryConsume("jti-1", Now.AddMinutes(5)).Should().BeTrue();
    }

    [Fact]
    public void TryConsume_ShouldReturnFalse_OnDuplicateWithinLifetime()
    {
        var time = new FakeTimeProvider(Now);
        var guard = new InMemoryIdentityAssertionReplayGuard(time);
        var expiresUtc = Now.AddMinutes(5);

        guard.TryConsume("jti-1", expiresUtc).Should().BeTrue();

        // Same jti presented again before it expires -> replay.
        guard.TryConsume("jti-1", expiresUtc).Should().BeFalse();

        // Still a replay after some time passes but before expiry.
        time.Advance(TimeSpan.FromMinutes(1));
        guard.TryConsume("jti-1", expiresUtc).Should().BeFalse();
    }

    [Fact]
    public void TryConsume_ShouldAllowSameJtiAgain_AfterExpiry()
    {
        var time = new FakeTimeProvider(Now);
        var guard = new InMemoryIdentityAssertionReplayGuard(time);
        var expiresUtc = Now.AddMinutes(5);

        guard.TryConsume("jti-1", expiresUtc).Should().BeTrue();

        // Advance strictly past the assertion lifetime: the entry evicts and a new assertion that
        // happens to reuse the jti (a fresh, re-minted assertion) is accepted again.
        time.Advance(TimeSpan.FromMinutes(6));
        guard.TryConsume("jti-1", time.GetUtcNow().AddMinutes(5)).Should().BeTrue();
    }

    [Fact]
    public void TryConsume_ShouldTreatDistinctJtisIndependently()
    {
        var time = new FakeTimeProvider(Now);
        var guard = new InMemoryIdentityAssertionReplayGuard(time);
        var expiresUtc = Now.AddMinutes(5);

        guard.TryConsume("jti-1", expiresUtc).Should().BeTrue();
        guard.TryConsume("jti-2", expiresUtc).Should().BeTrue();

        // Each is single-use on its own; consuming jti-2 does not free jti-1.
        guard.TryConsume("jti-1", expiresUtc).Should().BeFalse();
        guard.TryConsume("jti-2", expiresUtc).Should().BeFalse();
    }

    [Fact]
    public void TryConsume_ShouldEvictExpiredEntries_WhenOtherJtiConsumed()
    {
        var time = new FakeTimeProvider(Now);
        var guard = new InMemoryIdentityAssertionReplayGuard(time);

        guard.TryConsume("jti-expiring", Now.AddMinutes(1)).Should().BeTrue();

        // jti-expiring is now past its lifetime; consuming an unrelated jti drives eviction, after
        // which re-presenting jti-expiring is allowed (it is no longer a live duplicate).
        time.Advance(TimeSpan.FromMinutes(2));
        guard.TryConsume("jti-other", time.GetUtcNow().AddMinutes(5)).Should().BeTrue();
        guard.TryConsume("jti-expiring", time.GetUtcNow().AddMinutes(5)).Should().BeTrue();
    }

    [Fact]
    public void Constructor_ShouldRejectNullTimeProvider()
    {
        ((Action)(() => new InMemoryIdentityAssertionReplayGuard(null!)))
            .Should().Throw<ArgumentNullException>()
            .WithMessage("*timeProvider*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryConsume_ShouldRejectBlankJti(string jti)
    {
        var guard = new InMemoryIdentityAssertionReplayGuard(new FakeTimeProvider(Now));

        ((Action)(() => guard.TryConsume(jti, Now.AddMinutes(5))))
            .Should().Throw<ArgumentException>()
            .WithMessage("*jti*");
    }
}
