using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionReadModelStoreSelectorTests
{
    [Fact]
    public void GraphStoreFactory_WhenRequestedProviderMatched_ShouldCreateRequestedProviderStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionStoreRegistration<IProjectionGraphStore>>(
            new DelegateProjectionStoreRegistration<IProjectionGraphStore>(
                "inmemory",
                _ => new NamedGraphStore("inmemory")));
        services.AddSingleton<IProjectionStoreRegistration<IProjectionGraphStore>>(
            new DelegateProjectionStoreRegistration<IProjectionGraphStore>(
                "neo4j",
                _ => new NamedGraphStore("neo4j")));

        using var serviceProvider = services.BuildServiceProvider();
        var factory = new ProjectionGraphStoreFactory();

        var selected = factory.Create(serviceProvider, "Neo4J");
        var typed = selected.Should().BeOfType<NamedGraphStore>().Subject;
        typed.ProviderName.Should().Be("neo4j");
    }

    [Fact]
    public void GraphStoreFactory_WhenRequestedProviderMissing_ShouldThrow()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionStoreRegistration<IProjectionGraphStore>>(
            new DelegateProjectionStoreRegistration<IProjectionGraphStore>(
                "inmemory",
                _ => new NamedGraphStore("inmemory")));

        using var serviceProvider = services.BuildServiceProvider();
        var factory = new ProjectionGraphStoreFactory();

        Action act = () => factory.Create(serviceProvider, "elasticsearch");

        act.Should().Throw<ProjectionProviderSelectionException>()
            .Where(ex => ex.Reason.Contains("Requested relation store provider is not registered", StringComparison.Ordinal));
    }

    [Fact]
    public void GraphStoreFactory_WhenNoProviderRegistered_ShouldThrow()
    {
        var services = new ServiceCollection();
        using var serviceProvider = services.BuildServiceProvider();
        var factory = new ProjectionGraphStoreFactory();

        Action act = () => factory.Create(serviceProvider, ProjectionProviderNames.InMemory);

        act.Should().Throw<ProjectionProviderSelectionException>()
            .Where(ex => ex.Reason.Contains("No relation store provider registrations", StringComparison.Ordinal));
    }

    private sealed class NamedGraphStore : IProjectionGraphStore
    {
        public NamedGraphStore(string providerName)
        {
            ProviderName = providerName;
        }

        public string ProviderName { get; }

        public Task UpsertNodeAsync(ProjectionGraphNode node, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertEdgeAsync(ProjectionGraphEdge edge, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ProjectionGraphEdge>> GetNeighborsAsync(ProjectionGraphQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>([]);

        public Task<ProjectionGraphSubgraph> GetSubgraphAsync(ProjectionGraphQuery query, CancellationToken ct = default) =>
            Task.FromResult(new ProjectionGraphSubgraph());
    }
}
