using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionReadModelStoreSelectorTests
{
    [Fact]
    public async Task ProjectionGraphStoreFanout_ShouldFanoutWritesAndUseFirstRegisteredQueryStore()
    {
        var firstStore = new NamedGraphStore("first");
        var secondStore = new NamedGraphStore("second");
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionStoreRegistration<IProjectionGraphStore>>(
            new DelegateProjectionStoreRegistration<IProjectionGraphStore>(
                "first",
                _ => firstStore));
        services.AddSingleton<IProjectionStoreRegistration<IProjectionGraphStore>>(
            new DelegateProjectionStoreRegistration<IProjectionGraphStore>(
                "second",
                _ => secondStore));

        using var serviceProvider = services.BuildServiceProvider();
        var fanout = new ProjectionGraphStoreFanout(
            serviceProvider.GetServices<IProjectionStoreRegistration<IProjectionGraphStore>>(),
            serviceProvider);

        await fanout.UpsertNodeAsync(new ProjectionGraphNode
        {
            Scope = "projection-scope",
            NodeId = "node-1",
            NodeType = "Actor",
            Properties = new Dictionary<string, string>(),
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var edges = await fanout.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "projection-scope",
            RootNodeId = "node-1",
            Direction = ProjectionGraphDirection.Both,
            EdgeTypes = [],
            Depth = 1,
            Take = 10,
        });

        firstStore.UpsertNodeCount.Should().Be(1);
        secondStore.UpsertNodeCount.Should().Be(1);
        edges.Should().HaveCount(1);
        edges[0].EdgeType.Should().Be("first");
    }

    [Fact]
    public async Task ProjectionGraphStoreFanout_ShouldReadFromFirstRegistration_WhenOrderDiffers()
    {
        var firstStore = new NamedGraphStore("from-first");
        var secondStore = new NamedGraphStore("from-second");
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionStoreRegistration<IProjectionGraphStore>>(
            new DelegateProjectionStoreRegistration<IProjectionGraphStore>(
                "first",
                _ => firstStore));
        services.AddSingleton<IProjectionStoreRegistration<IProjectionGraphStore>>(
            new DelegateProjectionStoreRegistration<IProjectionGraphStore>(
                "second",
                _ => secondStore));

        using var serviceProvider = services.BuildServiceProvider();
        var fanout = new ProjectionGraphStoreFanout(
            serviceProvider.GetServices<IProjectionStoreRegistration<IProjectionGraphStore>>(),
            serviceProvider);

        var edges = await fanout.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "projection-scope",
            RootNodeId = "node-1",
            Direction = ProjectionGraphDirection.Both,
            EdgeTypes = [],
            Depth = 1,
            Take = 10,
        });

        edges.Should().ContainSingle();
        edges[0].EdgeType.Should().Be("from-first");
    }

    [Fact]
    public void ProjectionGraphStoreFanout_WhenNoRegistrations_ShouldThrow()
    {
        var services = new ServiceCollection();
        using var serviceProvider = services.BuildServiceProvider();

        Action act = () => new ProjectionGraphStoreFanout([], serviceProvider);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No graph projection store providers are registered*");
    }

    private sealed class NamedGraphStore : IProjectionGraphStore
    {
        public NamedGraphStore(string providerName)
        {
            ProviderName = providerName;
        }

        public string ProviderName { get; }

        public int UpsertNodeCount { get; private set; }

        public Task UpsertNodeAsync(ProjectionGraphNode node, CancellationToken ct = default)
        {
            _ = node;
            UpsertNodeCount++;
            return Task.CompletedTask;
        }

        public Task UpsertEdgeAsync(ProjectionGraphEdge edge, CancellationToken ct = default)
        {
            _ = edge;
            return Task.CompletedTask;
        }

        public Task DeleteNodeAsync(string scope, string nodeId, CancellationToken ct = default)
        {
            _ = scope;
            _ = nodeId;
            return Task.CompletedTask;
        }

        public Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default)
        {
            _ = scope;
            _ = edgeId;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProjectionGraphNode>> ListNodesByOwnerAsync(
            string scope,
            string ownerId,
            int take = 5000,
            CancellationToken ct = default)
        {
            _ = scope;
            _ = ownerId;
            _ = take;
            return Task.FromResult<IReadOnlyList<ProjectionGraphNode>>([]);
        }

        public Task<IReadOnlyList<ProjectionGraphEdge>> ListEdgesByOwnerAsync(
            string scope,
            string ownerId,
            int take = 5000,
            CancellationToken ct = default)
        {
            _ = scope;
            _ = ownerId;
            _ = take;
            return Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>([]);
        }

        public Task<IReadOnlyList<ProjectionGraphEdge>> GetNeighborsAsync(ProjectionGraphQuery query, CancellationToken ct = default)
        {
            _ = query;
            IReadOnlyList<ProjectionGraphEdge> result =
            [
                new ProjectionGraphEdge
                {
                    Scope = "projection-scope",
                    EdgeId = "edge-1",
                    EdgeType = ProviderName,
                    FromNodeId = "node-1",
                    ToNodeId = "node-2",
                    Properties = new Dictionary<string, string>(),
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            ];
            return Task.FromResult(result);
        }

        public Task<ProjectionGraphSubgraph> GetSubgraphAsync(ProjectionGraphQuery query, CancellationToken ct = default)
        {
            _ = query;
            return Task.FromResult(new ProjectionGraphSubgraph());
        }
    }
}
