using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionReadModelStoreSelectorTests
{
    [Fact]
    public void DocumentSelector_WhenSingleProviderRegistered_ShouldReturnSingleProvider()
    {
        var selector = new ProjectionDocumentStoreProviderSelector();
        var registrations = new[]
        {
            CreateDocumentRegistration("inmemory"),
        };

        var selected = selector.Select(
            registrations,
            new ProjectionDocumentSelectionOptions());

        selected.ProviderName.Should().Be("inmemory");
    }

    [Fact]
    public void DocumentSelector_WhenRequestedProviderMissing_ShouldThrow()
    {
        var selector = new ProjectionDocumentStoreProviderSelector();
        var registrations = new[]
        {
            CreateDocumentRegistration("inmemory"),
        };

        Action act = () => selector.Select(
            registrations,
            new ProjectionDocumentSelectionOptions
            {
                RequestedProviderName = "elasticsearch",
            });

        act.Should().Throw<ProjectionProviderSelectionException>()
            .Where(ex => ex.Reason.Contains("Requested document store provider is not registered", StringComparison.Ordinal));
    }

    [Fact]
    public void GraphSelector_WhenRequestedProviderMatched_ShouldReturnProvider()
    {
        var selector = new ProjectionGraphStoreProviderSelector();
        var registrations = new[]
        {
            CreateGraphRegistration("inmemory"),
            CreateGraphRegistration("neo4j"),
        };

        var selected = selector.Select(
            registrations,
            new ProjectionGraphSelectionOptions
            {
                RequestedProviderName = "Neo4J",
            });

        selected.ProviderName.Should().Be("neo4j");
    }

    [Fact]
    public void GraphSelector_WhenNoProviderRegistered_ShouldThrow()
    {
        var selector = new ProjectionGraphStoreProviderSelector();
        Action act = () => selector.Select(
            [],
            new ProjectionGraphSelectionOptions
            {
                RequestedProviderName = ProjectionProviderNames.InMemory,
            });

        act.Should().Throw<ProjectionProviderSelectionException>()
            .Where(ex => ex.Reason.Contains("No relation store provider registrations", StringComparison.Ordinal));
    }

    private static IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>> CreateDocumentRegistration(
        string providerName)
    {
        return new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
            providerName,
            _ => new NoopDocumentStore());
    }

    private static IProjectionStoreRegistration<IProjectionGraphStore> CreateGraphRegistration(string providerName)
    {
        return new DelegateProjectionStoreRegistration<IProjectionGraphStore>(
            providerName,
            _ => new NoopGraphStore());
    }

    private sealed class NoopDocumentStore : IDocumentProjectionStore<TestReadModel, string>
    {
        public Task UpsertAsync(TestReadModel readModel, CancellationToken ct = default) => Task.CompletedTask;

        public Task MutateAsync(string key, Action<TestReadModel> mutate, CancellationToken ct = default) => Task.CompletedTask;

        public Task<TestReadModel?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<TestReadModel?>(null);

        public Task<IReadOnlyList<TestReadModel>> ListAsync(int take = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TestReadModel>>([]);
    }

    private sealed class NoopGraphStore : IProjectionGraphStore
    {
        public Task UpsertNodeAsync(ProjectionGraphNode node, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertEdgeAsync(ProjectionGraphEdge edge, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ProjectionGraphEdge>> GetNeighborsAsync(ProjectionGraphQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>([]);

        public Task<ProjectionGraphSubgraph> GetSubgraphAsync(ProjectionGraphQuery query, CancellationToken ct = default) =>
            Task.FromResult(new ProjectionGraphSubgraph());
    }

    private sealed class TestReadModel;
}
