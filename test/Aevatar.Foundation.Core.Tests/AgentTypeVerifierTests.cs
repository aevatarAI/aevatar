using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests;

public class AgentTypeVerifierTests
{
    [Fact]
    public async Task IsExpectedAsync_WhenRuntimeTypeMatches_ShouldReturnTrue()
    {
        var verifier = new DefaultAgentTypeVerifier(
            new StaticActorTypeProbe(new Dictionary<string, string?>
            {
                ["actor-1"] = typeof(CollectorAgent).AssemblyQualifiedName,
            }));

        var result = await verifier.IsExpectedAsync("actor-1", typeof(CollectorAgent), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsExpectedAsync_WhenRuntimeTypeMismatches_ShouldReturnFalse()
    {
        var verifier = new DefaultAgentTypeVerifier(
            new StaticActorTypeProbe(new Dictionary<string, string?>
            {
                ["actor-1"] = typeof(EchoAgent).AssemblyQualifiedName,
            }));

        var result = await verifier.IsExpectedAsync("actor-1", typeof(CollectorAgent), CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsExpectedAsync_WhenRuntimeTypeMissing_ShouldReturnFalse()
    {
        var verifier = new DefaultAgentTypeVerifier(new StaticActorTypeProbe());
        var result = await verifier.IsExpectedAsync("actor-1", typeof(CollectorAgent), CancellationToken.None);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsExpectedAsync_WhenRuntimeTypeLooksSimilar_ShouldReturnFalse()
    {
        var verifier = new DefaultAgentTypeVerifier(
            new StaticActorTypeProbe(new Dictionary<string, string?>
            {
                ["actor-1"] = $"{typeof(CollectorAgent).FullName}Shadow",
            }));

        var result = await verifier.IsExpectedAsync("actor-1", typeof(CollectorAgent), CancellationToken.None);

        result.Should().BeFalse();
    }

    private sealed class StaticActorTypeProbe : IActorTypeProbe
    {
        private readonly IReadOnlyDictionary<string, string?> _types;

        public StaticActorTypeProbe(IReadOnlyDictionary<string, string?>? types = null)
        {
            _types = types ?? new Dictionary<string, string?>();
        }

        public Task<string?> GetRuntimeAgentTypeNameAsync(string actorId, CancellationToken ct = default)
        {
            _ = ct;
            _types.TryGetValue(actorId, out var typeName);
            return Task.FromResult(typeName);
        }
    }
}
