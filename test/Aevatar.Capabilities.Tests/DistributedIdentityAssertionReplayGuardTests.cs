using Aevatar.Mainnet.Host.Api.Responses;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.Capabilities.Tests;

public sealed class DistributedIdentityAssertionReplayGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 14, 12, 00, 00, TimeSpan.Zero);

    [Fact]
    public async Task TryConsumeAsync_AcrossTwoHostGuards_ShouldAtomicallyAdmitOnlyOne()
    {
        var store = new CoordinatedReplayStore(expectedCalls: 2);
        var time = new FakeTimeProvider(Now);
        var firstHost = new DistributedIdentityAssertionReplayGuard(store, time);
        var secondHost = new DistributedIdentityAssertionReplayGuard(store, time);
        var expiresUtc = Now.AddMinutes(5);

        var attempts = new[]
        {
            firstHost.TryConsumeAsync("assertion-jti", expiresUtc).AsTask(),
            secondHost.TryConsumeAsync("assertion-jti", expiresUtc).AsTask(),
        };
        var results = await Task.WhenAll(attempts);

        results.Should().ContainSingle(static admitted => admitted);
        results.Should().ContainSingle(static admitted => !admitted);
        store.Keys.Should().ContainSingle();
    }

    [Fact]
    public async Task TryConsumeAsync_ShouldUseHashedSharedKeyAndAssertionLifetime()
    {
        var store = new CoordinatedReplayStore(expectedCalls: 1);
        var guard = new DistributedIdentityAssertionReplayGuard(store, new FakeTimeProvider(Now));
        var acceptedUntilUtc = Now.AddMinutes(3);

        var admitted = await guard.TryConsumeAsync("sensitive-jti", acceptedUntilUtc);

        admitted.Should().BeTrue();
        store.Keys.Should().ContainSingle()
            .Which.Should().StartWith("aevatar:mainnet:nyxid-identity-assertion:jti:")
            .And.NotContain("sensitive-jti");
        store.Retentions.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMinutes(3));
    }

    private sealed class CoordinatedReplayStore : IIdentityAssertionSingleUseStore
    {
        private readonly int _expectedCalls;
        private readonly TaskCompletionSource _allCallersReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
        private int _callCount;

        public CoordinatedReplayStore(int expectedCalls)
        {
            _expectedCalls = expectedCalls;
        }

        public IReadOnlyCollection<string> Keys
        {
            get
            {
                lock (_gate)
                    return _keys.ToArray();
            }
        }

        public List<TimeSpan> Retentions { get; } = [];

        public async ValueTask<bool> TryAddAsync(
            string key,
            TimeSpan retention,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
                Retentions.Add(retention);

            if (Interlocked.Increment(ref _callCount) == _expectedCalls)
                _allCallersReady.TrySetResult();

            await _allCallersReady.Task.WaitAsync(cancellationToken);
            lock (_gate)
                return _keys.Add(key);
        }
    }
}
