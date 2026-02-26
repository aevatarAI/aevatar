using System.Reflection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class InMemoryProjectionGraphStoreTests
{
    [Fact]
    public async Task UpsertNodeAsync_ShouldNormalizeAndClone()
    {
        var store = new InMemoryProjectionGraphStore();
        var source = new ProjectionGraphNode
        {
            Scope = "  scope-1  ",
            NodeId = "  node-1  ",
            NodeType = "Actor",
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["k"] = "v",
                [ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey] = " owner-1 ",
            },
            UpdatedAt = new DateTimeOffset(2026, 2, 26, 0, 0, 0, TimeSpan.Zero),
        };

        await store.UpsertNodeAsync(source);

        source.Properties["k"] = "changed";
        source.NodeType = "Changed";
        var nodes = await store.ListNodesByOwnerAsync("scope-1", "owner-1", take: 10);

        nodes.Should().ContainSingle();
        nodes[0].Scope.Should().Be("scope-1");
        nodes[0].NodeId.Should().Be("node-1");
        nodes[0].NodeType.Should().Be("Actor");
        nodes[0].Properties["k"].Should().Be("v");
    }

    [Fact]
    public async Task UpsertNodeAsync_WhenNodeIsInvalid_ShouldThrow()
    {
        var store = new InMemoryProjectionGraphStore();

        Func<Task> nullNode = () => store.UpsertNodeAsync(null!);
        Func<Task> emptyScope = () => store.UpsertNodeAsync(new ProjectionGraphNode
        {
            Scope = " ",
            NodeId = "node-1",
            NodeType = "Actor",
            Properties = new Dictionary<string, string>(StringComparer.Ordinal),
        });
        Func<Task> emptyNodeId = () => store.UpsertNodeAsync(new ProjectionGraphNode
        {
            Scope = "scope-1",
            NodeId = " ",
            NodeType = "Actor",
            Properties = new Dictionary<string, string>(StringComparer.Ordinal),
        });

        await nullNode.Should().ThrowAsync<ArgumentNullException>();
        await emptyScope.Should().ThrowAsync<InvalidOperationException>();
        await emptyNodeId.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpsertEdgeAsync_WhenEdgeIsInvalid_ShouldThrow()
    {
        var store = new InMemoryProjectionGraphStore();
        var valid = new ProjectionGraphEdge
        {
            Scope = "scope-1",
            EdgeId = "edge-1",
            FromNodeId = "a",
            ToNodeId = "b",
            EdgeType = "LINK",
            Properties = new Dictionary<string, string>(StringComparer.Ordinal),
        };

        await store.UpsertEdgeAsync(valid);

        var missing = new ProjectionGraphEdge
        {
            Scope = "scope-1",
            EdgeId = "",
            FromNodeId = "a",
            ToNodeId = "b",
            EdgeType = "LINK",
            Properties = new Dictionary<string, string>(StringComparer.Ordinal),
        };
        Func<Task> invalid = () => store.UpsertEdgeAsync(missing);

        await invalid.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeleteAsync_ShouldIgnoreEmptyAndRemoveExisting()
    {
        var store = new InMemoryProjectionGraphStore();
        await store.UpsertNodeAsync(Node("scope-1", "node-1", ownerId: "owner-1"));
        await store.UpsertEdgeAsync(Edge("scope-1", "edge-1", "node-1", "node-2", ownerId: "owner-1"));

        await store.DeleteNodeAsync(" ", " ");
        await store.DeleteEdgeAsync("", "");

        (await store.ListNodesByOwnerAsync("scope-1", "owner-1")).Should().ContainSingle();
        (await store.ListEdgesByOwnerAsync("scope-1", "owner-1")).Should().ContainSingle();

        await store.DeleteNodeAsync("scope-1", "node-1");
        await store.DeleteEdgeAsync("scope-1", "edge-1");

        (await store.ListNodesByOwnerAsync("scope-1", "owner-1")).Should().BeEmpty();
        (await store.ListEdgesByOwnerAsync("scope-1", "owner-1")).Should().BeEmpty();
    }

    [Fact]
    public async Task ListNodesByOwnerAsync_ShouldApplyOrderingAndPagingBounds()
    {
        var store = new InMemoryProjectionGraphStore();
        await store.UpsertNodeAsync(Node("scope-1", "n-1", ownerId: "owner-1", updatedAt: new DateTimeOffset(2026, 2, 26, 0, 0, 1, TimeSpan.Zero)));
        await store.UpsertNodeAsync(Node("scope-1", "n-2", ownerId: "owner-1", updatedAt: new DateTimeOffset(2026, 2, 26, 0, 0, 3, TimeSpan.Zero)));
        await store.UpsertNodeAsync(Node("scope-1", "n-3", ownerId: "owner-1", updatedAt: new DateTimeOffset(2026, 2, 26, 0, 0, 2, TimeSpan.Zero)));
        await store.UpsertNodeAsync(Node("scope-1", "n-x", ownerId: "owner-x"));

        var page = await store.ListNodesByOwnerAsync(" scope-1 ", " owner-1 ", skip: -1, take: 2);
        var none = await store.ListNodesByOwnerAsync("scope-1", "", skip: 0, take: 10);

        page.Select(x => x.NodeId).Should().Equal("n-2", "n-3");
        none.Should().BeEmpty();
    }

    [Fact]
    public async Task ListEdgesByOwnerAsync_ShouldApplyOrderingAndPagingBounds()
    {
        var store = new InMemoryProjectionGraphStore();
        await store.UpsertEdgeAsync(Edge("scope-1", "e-1", "a", "b", ownerId: "owner-1", updatedAt: new DateTimeOffset(2026, 2, 26, 0, 0, 1, TimeSpan.Zero)));
        await store.UpsertEdgeAsync(Edge("scope-1", "e-2", "a", "c", ownerId: "owner-1", updatedAt: new DateTimeOffset(2026, 2, 26, 0, 0, 3, TimeSpan.Zero)));
        await store.UpsertEdgeAsync(Edge("scope-1", "e-3", "a", "d", ownerId: "owner-1", updatedAt: new DateTimeOffset(2026, 2, 26, 0, 0, 2, TimeSpan.Zero)));
        await store.UpsertEdgeAsync(Edge("scope-1", "e-x", "a", "x", ownerId: "owner-x"));

        var page = await store.ListEdgesByOwnerAsync("scope-1", "owner-1", skip: -2, take: 2);
        var none = await store.ListEdgesByOwnerAsync("", "owner-1", skip: 0, take: 10);

        page.Select(x => x.EdgeId).Should().Equal("e-2", "e-3");
        none.Should().BeEmpty();
    }

    [Fact]
    public async Task GetNeighborsAsync_ShouldFilterByDirectionTypeAndTake()
    {
        var store = new InMemoryProjectionGraphStore();
        await store.UpsertEdgeAsync(Edge("scope-1", "out-1", "root", "a", edgeType: "LINK", updatedAt: new DateTimeOffset(2026, 2, 26, 0, 0, 3, TimeSpan.Zero)));
        await store.UpsertEdgeAsync(Edge("scope-1", "in-1", "b", "root", edgeType: "LINK", updatedAt: new DateTimeOffset(2026, 2, 26, 0, 0, 2, TimeSpan.Zero)));
        await store.UpsertEdgeAsync(Edge("scope-1", "out-2", "root", "c", edgeType: "USES", updatedAt: new DateTimeOffset(2026, 2, 26, 0, 0, 4, TimeSpan.Zero)));

        var outbound = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = " scope-1 ",
            RootNodeId = " root ",
            Direction = ProjectionGraphDirection.Outbound,
            EdgeTypes = [" LINK ", "  ", "LINK"],
            Take = 10,
        });
        var inbound = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "root",
            Direction = ProjectionGraphDirection.Inbound,
            EdgeTypes = [],
            Take = 10,
        });
        var bothTakeOne = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "root",
            Direction = ProjectionGraphDirection.Both,
            EdgeTypes = [],
            Take = 0,
        });

        outbound.Select(x => x.EdgeId).Should().Equal("out-1");
        inbound.Select(x => x.EdgeId).Should().Equal("in-1");
        bothTakeOne.Should().ContainSingle();
        bothTakeOne[0].EdgeId.Should().Be("out-2");
    }

    [Fact]
    public async Task GetSubgraphAsync_ShouldTraverseAndCreateUnknownCounterpartNodes()
    {
        var store = new InMemoryProjectionGraphStore();
        await store.UpsertNodeAsync(Node("scope-1", "root", ownerId: "owner-1"));
        await store.UpsertNodeAsync(Node("scope-1", "known-a", ownerId: "owner-1"));
        await store.UpsertEdgeAsync(Edge("scope-1", "e-1", "root", "known-a", edgeType: "LINK"));
        await store.UpsertEdgeAsync(Edge("scope-1", "e-2", "known-a", "missing-b", edgeType: "LINK"));

        var graph = await store.GetSubgraphAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "root",
            Direction = ProjectionGraphDirection.Outbound,
            EdgeTypes = ["LINK"],
            Depth = 10,
            Take = 10,
        });

        graph.Edges.Select(x => x.EdgeId).Should().Contain(new[] { "e-1", "e-2" });
        graph.Nodes.Select(x => x.NodeId).Should().Contain(new[] { "root", "known-a", "missing-b" });
        graph.Nodes.Single(x => x.NodeId == "missing-b").NodeType.Should().Be("Unknown");
    }

    [Fact]
    public async Task Methods_ShouldHonorCancellationToken()
    {
        var store = new InMemoryProjectionGraphStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var node = Node("scope-1", "n-1", ownerId: "owner-1");
        var edge = Edge("scope-1", "e-1", "n-1", "n-2", ownerId: "owner-1");

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.UpsertNodeAsync(node, cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.UpsertEdgeAsync(edge, cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.DeleteNodeAsync("scope-1", "n-1", cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.DeleteEdgeAsync("scope-1", "e-1", cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ListNodesByOwnerAsync("scope-1", "owner-1", ct: cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ListEdgesByOwnerAsync("scope-1", "owner-1", ct: cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.GetNeighborsAsync(new ProjectionGraphQuery { Scope = "scope-1", RootNodeId = "n-1" }, cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.GetSubgraphAsync(new ProjectionGraphQuery { Scope = "scope-1", RootNodeId = "n-1" }, cts.Token));
    }

    [Fact]
    public async Task ListByOwnerAsync_ShouldIgnoreEntriesWithoutOwnerIdProperty()
    {
        var store = new InMemoryProjectionGraphStore();
        await store.UpsertNodeAsync(Node("scope-1", "owned-node", ownerId: "owner-1"));
        await store.UpsertNodeAsync(Node("scope-1", "no-owner"));
        await store.UpsertEdgeAsync(Edge("scope-1", "owned-edge", "owned-node", "x", ownerId: "owner-1"));
        await store.UpsertEdgeAsync(Edge("scope-1", "no-owner-edge", "owned-node", "y"));

        var ownedNodes = await store.ListNodesByOwnerAsync("scope-1", "owner-1");
        var ownedEdges = await store.ListEdgesByOwnerAsync("scope-1", "owner-1");

        ownedNodes.Select(x => x.NodeId).Should().Equal("owned-node");
        ownedEdges.Select(x => x.EdgeId).Should().Equal("owned-edge");
    }

    [Fact]
    public async Task GetNeighborsAndSubgraphAsync_WhenScopeOrRootMissing_ShouldReturnEmpty()
    {
        var store = new InMemoryProjectionGraphStore();
        await store.UpsertEdgeAsync(Edge("scope-1", "e-1", "a", "b", ownerId: "owner-1"));

        var neighborsMissingScope = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = " ",
            RootNodeId = "a",
        });
        var neighborsMissingRoot = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = " ",
        });
        var subgraphMissingScope = await store.GetSubgraphAsync(new ProjectionGraphQuery
        {
            Scope = "",
            RootNodeId = "a",
        });
        var subgraphMissingRoot = await store.GetSubgraphAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "",
        });

        neighborsMissingScope.Should().BeEmpty();
        neighborsMissingRoot.Should().BeEmpty();
        subgraphMissingScope.Nodes.Should().BeEmpty();
        subgraphMissingScope.Edges.Should().BeEmpty();
        subgraphMissingRoot.Nodes.Should().BeEmpty();
        subgraphMissingRoot.Edges.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSubgraphAsync_ShouldRespectTakeLimitAndInboundTraversal()
    {
        var store = new InMemoryProjectionGraphStore();
        await store.UpsertNodeAsync(Node("scope-1", "root", ownerId: "owner-1"));
        await store.UpsertNodeAsync(Node("scope-1", "left", ownerId: "owner-1"));
        await store.UpsertNodeAsync(Node("scope-1", "right", ownerId: "owner-1"));
        await store.UpsertEdgeAsync(Edge("scope-1", "e-right", "right", "root", edgeType: "LINK", updatedAt: new DateTimeOffset(2026, 2, 26, 0, 0, 3, TimeSpan.Zero)));
        await store.UpsertEdgeAsync(Edge("scope-1", "e-left", "left", "root", edgeType: "LINK", updatedAt: new DateTimeOffset(2026, 2, 26, 0, 0, 2, TimeSpan.Zero)));

        var inboundLimited = await store.GetSubgraphAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "root",
            Direction = ProjectionGraphDirection.Inbound,
            EdgeTypes = [],
            Depth = 2,
            Take = 1,
        });

        inboundLimited.Edges.Should().ContainSingle();
        inboundLimited.Edges[0].EdgeId.Should().Be("e-right");
        inboundLimited.Nodes.Select(x => x.NodeId).Should().Contain("right");
    }

    [Fact]
    public void ResolveCounterpartNodeId_ShouldHandleToNodeAndNoMatch()
    {
        var method = typeof(InMemoryProjectionGraphStore).GetMethod(
            "ResolveCounterpartNodeId",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var edge = new ProjectionGraphEdge
        {
            Scope = "scope-1",
            EdgeId = "e-1",
            FromNodeId = "from",
            ToNodeId = "to",
            EdgeType = "LINK",
            Properties = new Dictionary<string, string>(StringComparer.Ordinal),
        };

        var counterpartFromTo = (string?)method!.Invoke(null, new object?[] { edge, "to" });
        var counterpartNoMatch = (string?)method.Invoke(null, new object?[] { edge, "other" });

        counterpartFromTo.Should().Be("from");
        counterpartNoMatch.Should().Be("");
    }

    private static ProjectionGraphNode Node(
        string scope,
        string nodeId,
        string nodeType = "Actor",
        string? ownerId = null,
        DateTimeOffset? updatedAt = null)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ownerId != null)
            properties[ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey] = ownerId;

        return new ProjectionGraphNode
        {
            Scope = scope,
            NodeId = nodeId,
            NodeType = nodeType,
            Properties = properties,
            UpdatedAt = updatedAt ?? new DateTimeOffset(2026, 2, 26, 0, 0, 0, TimeSpan.Zero),
        };
    }

    private static ProjectionGraphEdge Edge(
        string scope,
        string edgeId,
        string fromNodeId,
        string toNodeId,
        string edgeType = "LINK",
        string? ownerId = null,
        DateTimeOffset? updatedAt = null)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ownerId != null)
            properties[ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey] = ownerId;

        return new ProjectionGraphEdge
        {
            Scope = scope,
            EdgeId = edgeId,
            FromNodeId = fromNodeId,
            ToNodeId = toNodeId,
            EdgeType = edgeType,
            Properties = properties,
            UpdatedAt = updatedAt ?? new DateTimeOffset(2026, 2, 26, 0, 0, 0, TimeSpan.Zero),
        };
    }
}
