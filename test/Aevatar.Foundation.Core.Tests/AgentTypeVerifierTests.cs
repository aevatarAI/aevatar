using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests;

public class AgentTypeVerifierTests
{
    [Fact]
    public async Task IsExpectedAsync_WhenRuntimeTypeMatches_ShouldReturnTrue()
    {
        var manifestStore = new InMemoryManifestStore();
        var verifier = new DefaultAgentTypeVerifier(
            new StaticActorTypeProbe(new Dictionary<string, string?>
            {
                ["actor-1"] = typeof(CollectorAgent).AssemblyQualifiedName,
            }),
            manifestStore);

        var result = await verifier.IsExpectedAsync("actor-1", typeof(CollectorAgent), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsExpectedAsync_WhenRuntimeTypeMismatches_ShouldNotFallbackToManifest()
    {
        var manifestStore = new InMemoryManifestStore();
        await manifestStore.SaveAsync("actor-1", new AgentManifest
        {
            AgentId = "actor-1",
            AgentTypeName = typeof(CollectorAgent).AssemblyQualifiedName!,
        });

        var verifier = new DefaultAgentTypeVerifier(
            new StaticActorTypeProbe(new Dictionary<string, string?>
            {
                ["actor-1"] = typeof(EchoAgent).AssemblyQualifiedName,
            }),
            manifestStore);

        var result = await verifier.IsExpectedAsync("actor-1", typeof(CollectorAgent), CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsExpectedAsync_WhenRuntimeTypeMissing_ShouldFallbackToManifestAssemblyQualifiedName()
    {
        var manifestStore = new InMemoryManifestStore();
        await manifestStore.SaveAsync("actor-1", new AgentManifest
        {
            AgentId = "actor-1",
            AgentTypeName = typeof(CollectorAgent).AssemblyQualifiedName!,
        });

        var verifier = new DefaultAgentTypeVerifier(new StaticActorTypeProbe(), manifestStore);

        var result = await verifier.IsExpectedAsync("actor-1", typeof(CollectorAgent), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsExpectedAsync_WhenRuntimeTypeMissing_ShouldFallbackToManifestFullName()
    {
        var manifestStore = new InMemoryManifestStore();
        await manifestStore.SaveAsync("actor-1", new AgentManifest
        {
            AgentId = "actor-1",
            AgentTypeName = typeof(CollectorAgent).FullName!,
        });

        var verifier = new DefaultAgentTypeVerifier(new StaticActorTypeProbe(), manifestStore);

        var result = await verifier.IsExpectedAsync("actor-1", typeof(CollectorAgent), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsExpectedAsync_WhenManifestTypeLooksSimilar_ShouldReturnFalse()
    {
        var manifestStore = new InMemoryManifestStore();
        await manifestStore.SaveAsync("actor-1", new AgentManifest
        {
            AgentId = "actor-1",
            AgentTypeName = $"{typeof(CollectorAgent).FullName}Shadow",
        });

        var verifier = new DefaultAgentTypeVerifier(new StaticActorTypeProbe(), manifestStore);

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
