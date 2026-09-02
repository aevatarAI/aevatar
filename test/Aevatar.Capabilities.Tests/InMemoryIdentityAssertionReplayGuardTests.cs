using Aevatar.Mainnet.Host.Api.Responses;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.Capabilities.Tests;

public sealed class InMemoryIdentityAssertionReplayGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 03, 12, 00, 00, TimeSpan.Zero);

    [Fact]
    public async Task TryConsumeAsync_ShouldReturnTrue_OnFirstUse()
    {
        var time = new FakeTimeProvider(Now);
        var guard = new InMemoryIdentityAssertionReplayGuard(time);

        (await guard.TryConsumeAsync("jti-1", Now.AddMinutes(5))).Should().BeTrue();
    }

    [Fact]
    public async Task TryConsumeAsync_ShouldReturnFalse_OnDuplicateWithinLifetime()
    {
        var time = new FakeTimeProvider(Now);
        var guard = new InMemoryIdentityAssertionReplayGuard(time);
        var expiresUtc = Now.AddMinutes(5);

        (await guard.TryConsumeAsync("jti-1", expiresUtc)).Should().BeTrue();

        // Same jti presented again before it expires -> replay.
        (await guard.TryConsumeAsync("jti-1", expiresUtc)).Should().BeFalse();

        // Still a replay after some time passes but before expiry.
        time.Advance(TimeSpan.FromMinutes(1));
        (await guard.TryConsumeAsync("jti-1", expiresUtc)).Should().BeFalse();
    }

    [Fact]
    public async Task TryConsumeAsync_ShouldAllowSameJtiAgain_AfterExpiry()
    {
        var time = new FakeTimeProvider(Now);
        var guard = new InMemoryIdentityAssertionReplayGuard(time);
        var expiresUtc = Now.AddMinutes(5);

        (await guard.TryConsumeAsync("jti-1", expiresUtc)).Should().BeTrue();

        // Advance strictly past the assertion lifetime: the entry evicts and a new assertion that
        // happens to reuse the jti (a fresh, re-minted assertion) is accepted again.
        time.Advance(TimeSpan.FromMinutes(6));
        (await guard.TryConsumeAsync("jti-1", time.GetUtcNow().AddMinutes(5))).Should().BeTrue();
    }

    [Fact]
    public async Task TryConsumeAsync_ShouldTreatDistinctJtisIndependently()
    {
        var time = new FakeTimeProvider(Now);
        var guard = new InMemoryIdentityAssertionReplayGuard(time);
        var expiresUtc = Now.AddMinutes(5);

        (await guard.TryConsumeAsync("jti-1", expiresUtc)).Should().BeTrue();
        (await guard.TryConsumeAsync("jti-2", expiresUtc)).Should().BeTrue();

        // Each is single-use on its own; consuming jti-2 does not free jti-1.
        (await guard.TryConsumeAsync("jti-1", expiresUtc)).Should().BeFalse();
        (await guard.TryConsumeAsync("jti-2", expiresUtc)).Should().BeFalse();
    }

    [Fact]
    public async Task TryConsumeAsync_ShouldEvictExpiredEntries_WhenOtherJtiConsumed()
    {
        var time = new FakeTimeProvider(Now);
        var guard = new InMemoryIdentityAssertionReplayGuard(time);

        (await guard.TryConsumeAsync("jti-expiring", Now.AddMinutes(1))).Should().BeTrue();

        // jti-expiring is now past its lifetime; consuming an unrelated jti drives eviction, after
        // which re-presenting jti-expiring is allowed (it is no longer a live duplicate).
        time.Advance(TimeSpan.FromMinutes(2));
        (await guard.TryConsumeAsync("jti-other", time.GetUtcNow().AddMinutes(5))).Should().BeTrue();
        (await guard.TryConsumeAsync("jti-expiring", time.GetUtcNow().AddMinutes(5))).Should().BeTrue();
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
    public async Task TryConsumeAsync_ShouldRejectBlankJti(string jti)
    {
        var guard = new InMemoryIdentityAssertionReplayGuard(new FakeTimeProvider(Now));

        var act = async () => await guard.TryConsumeAsync(jti, Now.AddMinutes(5));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*jti*");
    }
}
