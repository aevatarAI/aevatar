using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests;

public class AgentKindVerifierTests
{
    [Fact]
    public async Task IsExpectedKindAsync_WhenRuntimeKindMatches_ShouldReturnTrue()
    {
        var verifier = new DefaultAgentKindVerifier(
            new StaticActorKindProbe(new Dictionary<string, string?>
            {
                ["actor-1"] = "test.collector",
            }));

        var result = await verifier.IsExpectedKindAsync("actor-1", "test.collector", CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsExpectedKindAsync_WhenRuntimeKindMismatches_ShouldReturnFalse()
    {
        var verifier = new DefaultAgentKindVerifier(
            new StaticActorKindProbe(new Dictionary<string, string?>
            {
                ["actor-1"] = "test.echo",
            }));

        var result = await verifier.IsExpectedKindAsync("actor-1", "test.collector", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsExpectedKindAsync_WhenRuntimeKindMissing_ShouldReturnFalse()
    {
        var verifier = new DefaultAgentKindVerifier(new StaticActorKindProbe());

        var result = await verifier.IsExpectedKindAsync("actor-1", "test.collector", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsExpectedKindAsync_TrimsExpectedKindOnly()
    {
        var verifier = new DefaultAgentKindVerifier(
            new StaticActorKindProbe(new Dictionary<string, string?>
            {
                ["actor-1"] = "test.collector",
            }));

        var result = await verifier.IsExpectedKindAsync("actor-1", " test.collector ", CancellationToken.None);

        result.Should().BeTrue();
    }

    private sealed class StaticActorKindProbe : IActorKindProbe
    {
        private readonly IReadOnlyDictionary<string, string?> _kinds;

        public StaticActorKindProbe(IReadOnlyDictionary<string, string?>? kinds = null)
        {
            _kinds = kinds ?? new Dictionary<string, string?>();
        }

        public Task<string?> GetRuntimeAgentKindAsync(string actorId, CancellationToken ct = default)
        {
            _ = ct;
            _kinds.TryGetValue(actorId, out var kind);
            return Task.FromResult(kind);
        }
    }
}
