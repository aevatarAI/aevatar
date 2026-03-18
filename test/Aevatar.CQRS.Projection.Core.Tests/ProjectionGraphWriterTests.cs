using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionGraphWriterTests
{
    [Fact]
    public async Task UpsertAsync_ShouldReplaceOwnerGraphAndRemoveDisconnectedStaleEdges()
    {
        var store = new InMemoryProjectionGraphStore();
        var writer = new ProjectionGraphWriter<TestGraphReadModel>(store, new TestGraphMaterializer());

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("root"), Node("left"), Node("orphan-a"), Node("orphan-b")],
            GraphEdges = [Edge("edge-root", "root", "left"), Edge("edge-orphan", "orphan-a", "orphan-b")],
        });

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("root"), Node("left")],
            GraphEdges = [Edge("edge-root", "root", "left")],
        });

        var rootNeighbors = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "root",
            Direction = ProjectionGraphDirection.Both,
            Take = 20,
        });
        var orphanNeighbors = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "orphan-a",
            Direction = ProjectionGraphDirection.Both,
            Take = 20,
        });

        rootNeighbors.Select(x => x.EdgeId).Should().ContainSingle("edge-root");
        orphanNeighbors.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldPreserveEdgesOwnedByAnotherReadModel()
    {
        var store = new InMemoryProjectionGraphStore();
        var writer = new ProjectionGraphWriter<TestGraphReadModel>(store, new TestGraphMaterializer());

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("a"), Node("b")],
            GraphEdges = [Edge("edge-owner-1", "a", "b")],
        });
        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-2",
            GraphScope = "scope-1",
            GraphNodes = [Node("c"), Node("d")],
            GraphEdges = [Edge("edge-owner-2", "c", "d")],
        });
        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("a"), Node("b")],
            GraphEdges = [],
        });

        var owner1Edges = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "a",
            Direction = ProjectionGraphDirection.Both,
            Take = 20,
        });
        var owner2Edges = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "c",
            Direction = ProjectionGraphDirection.Both,
            Take = 20,
        });

        owner1Edges.Should().BeEmpty();
        owner2Edges.Select(x => x.EdgeId).Should().ContainSingle("edge-owner-2");
    }

    [Fact]
    public async Task UpsertAsync_WithEmptyGraphCollections_ShouldCleanupManagedResources()
    {
        var store = new InMemoryProjectionGraphStore();
        var writer = new ProjectionGraphWriter<TestGraphReadModel>(store, new TestGraphMaterializer());

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("root"), Node("leaf")],
            GraphEdges = [Edge("edge-root-leaf", "root", "leaf")],
        });
        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [],
            GraphEdges = [],
        });

        var ownerId = BuildOwnerId("owner-1");
        (await store.ListNodesByOwnerAsync("scope-1", ownerId, take: 100)).Should().BeEmpty();
        (await store.ListEdgesByOwnerAsync("scope-1", ownerId, take: 100)).Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldKeepManagedNodeWhenForeignEdgeStillReferencesIt()
    {
        var store = new InMemoryProjectionGraphStore();
        var writer = new ProjectionGraphWriter<TestGraphReadModel>(store, new TestGraphMaterializer());

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("root"), Node("stale")],
            GraphEdges = [],
        });

        await store.UpsertEdgeAsync(new ProjectionGraphEdge
        {
            Scope = "scope-1",
            EdgeId = "external-edge",
            EdgeType = "LINK",
            FromNodeId = "stale",
            ToNodeId = "external-node",
            Properties = new Dictionary<string, string>(StringComparer.Ordinal),
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("root")],
            GraphEdges = [],
        });

        var ownerNodes = await store.ListNodesByOwnerAsync("scope-1", BuildOwnerId("owner-1"), take: 100);
        ownerNodes.Select(x => x.NodeId).Should().Contain("stale");
    }

    [Fact]
    public async Task UpsertAsync_ShouldNormalizeInvalidNodesAndEdges()
    {
        var store = new InMemoryProjectionGraphStore();
        var writer = new ProjectionGraphWriter<TestGraphReadModel>(store, new TestGraphMaterializer());

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-2",
            GraphScope = "scope-1",
            GraphNodes =
            [
                new ProjectionGraphNode
                {
                    Scope = "scope-1",
                    NodeId = "valid-node",
                    NodeType = " ",
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                    UpdatedAt = default,
                },
                new ProjectionGraphNode
                {
                    Scope = "scope-1",
                    NodeId = " ",
                    NodeType = "Actor",
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
            GraphEdges =
            [
                new ProjectionGraphEdge
                {
                    Scope = "scope-1",
                    EdgeId = "valid-edge",
                    EdgeType = "LINK",
                    FromNodeId = "valid-node",
                    ToNodeId = "target-node",
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                    UpdatedAt = default,
                },
                new ProjectionGraphEdge
                {
                    Scope = "scope-1",
                    EdgeId = "",
                    EdgeType = "LINK",
                    FromNodeId = "valid-node",
                    ToNodeId = "target-node",
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
        });

        var ownerId = BuildOwnerId("owner-2");
        var nodes = await store.ListNodesByOwnerAsync("scope-1", ownerId, take: 100);
        var edges = await store.ListEdgesByOwnerAsync("scope-1", ownerId, take: 100);

        nodes.Select(x => x.NodeId).Should().Equal("valid-node");
        nodes[0].NodeType.Should().Be("Unknown");
        nodes[0].UpdatedAt.Should().NotBe(default);
        edges.Select(x => x.EdgeId).Should().Equal("valid-edge");
        edges[0].UpdatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task UpsertAsync_WhenReadModelIdOrGraphScopeIsMissing_ShouldThrow()
    {
        var store = new InMemoryProjectionGraphStore();
        var writer = new ProjectionGraphWriter<TestGraphReadModel>(store, new TestGraphMaterializer());

        Func<Task> emptyId = () => writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "",
            GraphScope = "scope-1",
            GraphNodes = [Node("root")],
        });
        Func<Task> emptyScope = () => writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = " ",
            GraphNodes = [Node("root")],
        });

        await emptyId.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires a non-empty Id*");
        await emptyScope.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Graph scope is required*");
    }

    private static string BuildOwnerId(string id) => $"{typeof(TestGraphReadModel).FullName}:{id}";

    private static ProjectionGraphNode Node(string nodeId)
    {
        return new ProjectionGraphNode
        {
            Scope = "scope-1",
            NodeId = nodeId,
            NodeType = "Actor",
            Properties = new Dictionary<string, string>(StringComparer.Ordinal),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static ProjectionGraphEdge Edge(string edgeId, string fromNodeId, string toNodeId)
    {
        return new ProjectionGraphEdge
        {
            Scope = "scope-1",
            EdgeId = edgeId,
            EdgeType = "LINK",
            FromNodeId = fromNodeId,
            ToNodeId = toNodeId,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private sealed class TestGraphReadModel : IProjectionReadModel
    {
        public string Id { get; init; } = "";

        public string ActorId => Id;

        public long StateVersion { get; init; }

        public string LastEventId { get; init; } = "";

        public DateTimeOffset UpdatedAt { get; init; }

        public string GraphScope { get; init; } = "";

        public IReadOnlyList<ProjectionGraphNode> GraphNodes { get; init; } = [];

        public IReadOnlyList<ProjectionGraphEdge> GraphEdges { get; init; } = [];
    }

    private sealed class TestGraphMaterializer : IProjectionGraphMaterializer<TestGraphReadModel>
    {
        public ProjectionGraphMaterialization Materialize(TestGraphReadModel readModel)
        {
            return new ProjectionGraphMaterialization
            {
                Scope = readModel.GraphScope,
                Nodes = readModel.GraphNodes,
                Edges = readModel.GraphEdges,
            };
        }
    }
}
