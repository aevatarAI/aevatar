using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionGraphStoreBindingTests
{
    [Fact]
    public async Task UpsertAsync_ShouldRemoveDisconnectedStaleEdgesForSameOwner()
    {
        var store = new RecordingGraphStore();
        var binding = new ProjectionGraphStoreBinding<TestGraphReadModel, string>(store);

        await binding.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes =
            [
                Node("root"),
                Node("left"),
                Node("orphan-a"),
                Node("orphan-b"),
            ],
            GraphEdges =
            [
                Edge("edge-root", "root", "left"),
                Edge("edge-orphan", "orphan-a", "orphan-b"),
            ],
        });

        await binding.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes =
            [
                Node("root"),
                Node("left"),
            ],
            GraphEdges =
            [
                Edge("edge-root", "root", "left"),
            ],
        });

        var rootNeighbors = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "root",
            Direction = ProjectionGraphDirection.Both,
            EdgeTypes = [],
            Take = 20,
        });
        var orphanNeighbors = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "orphan-a",
            Direction = ProjectionGraphDirection.Both,
            EdgeTypes = [],
            Take = 20,
        });

        rootNeighbors.Select(x => x.EdgeId).Should().ContainSingle("edge-root");
        orphanNeighbors.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldNotDeleteEdgesOwnedByAnotherReadModel()
    {
        var store = new RecordingGraphStore();
        var binding = new ProjectionGraphStoreBinding<TestGraphReadModel, string>(store);

        await binding.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes =
            [
                Node("a"),
                Node("b"),
            ],
            GraphEdges =
            [
                Edge("edge-owner-1", "a", "b"),
            ],
        });

        await binding.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-2",
            GraphScope = "scope-1",
            GraphNodes =
            [
                Node("c"),
                Node("d"),
            ],
            GraphEdges =
            [
                Edge("edge-owner-2", "c", "d"),
            ],
        });

        await binding.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes =
            [
                Node("a"),
                Node("b"),
            ],
            GraphEdges = [],
        });

        var owner1Edges = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "a",
            Direction = ProjectionGraphDirection.Both,
            EdgeTypes = [],
            Take = 20,
        });
        var owner2Edges = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "c",
            Direction = ProjectionGraphDirection.Both,
            EdgeTypes = [],
            Take = 20,
        });

        owner1Edges.Should().BeEmpty();
        owner2Edges.Select(x => x.EdgeId).Should().ContainSingle("edge-owner-2");
    }

    [Fact]
    public async Task UpsertAsync_WhenReadModelIdIsEmpty_ShouldThrow()
    {
        var store = new RecordingGraphStore();
        var binding = new ProjectionGraphStoreBinding<TestGraphReadModel, string>(store);

        Func<Task> act = () => binding.UpsertAsync(new TestGraphReadModel
        {
            Id = "",
            GraphScope = "scope-1",
            GraphNodes =
            [
                Node("root"),
            ],
            GraphEdges = [],
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires a non-empty Id*");
    }

    [Fact]
    public async Task UpsertAsync_WhenOwnerGraphLarge_ShouldPageThroughOwnerCleanup()
    {
        var store = new RecordingGraphStore();
        var binding = new ProjectionGraphStoreBinding<TestGraphReadModel, string>(store);

        var largeNodes = Enumerable.Range(0, 1205)
            .Select(i => Node($"n-{i}"))
            .ToList();
        var largeEdges = Enumerable.Range(0, 1205)
            .Select(i => Edge($"e-{i}", "root", $"n-{i}"))
            .ToList();
        largeNodes.Insert(0, Node("root"));

        await binding.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-large",
            GraphScope = "scope-1",
            GraphNodes = largeNodes,
            GraphEdges = largeEdges,
        });

        await binding.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-large",
            GraphScope = "scope-1",
            GraphNodes =
            [
                Node("root"),
            ],
            GraphEdges = [],
        });

        var rootNeighbors = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "root",
            Direction = ProjectionGraphDirection.Both,
            EdgeTypes = [],
            Take = 5000,
        });

        store.ListEdgesByOwnerCallCount.Should().BeGreaterThan(1);
        rootNeighbors.Should().BeEmpty();
    }

    [Fact]
    public void AvailabilityReason_WhenReadModelIsNotGraphReadModel_ShouldExplainWhySkipped()
    {
        var binding = new ProjectionGraphStoreBinding<NonGraphReadModel, string>(new RecordingGraphStore());

        binding.IsConfigured.Should().BeFalse();
        binding.AvailabilityReason.Should().Contain("does not implement");
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

    private sealed class TestGraphReadModel : IGraphReadModel
    {
        public string Id { get; init; } = "";

        public string GraphScope { get; init; } = "";

        public IReadOnlyList<ProjectionGraphNode> GraphNodes { get; init; } = [];

        public IReadOnlyList<ProjectionGraphEdge> GraphEdges { get; init; } = [];
    }

    private sealed class NonGraphReadModel : IProjectionReadModel
    {
        public string Id { get; init; } = "";
    }

    private sealed class RecordingGraphStore : IProjectionGraphStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ProjectionGraphNode> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ProjectionGraphEdge> _edges = new(StringComparer.Ordinal);

        public int ListEdgesByOwnerCallCount { get; private set; }

        public Task UpsertNodeAsync(ProjectionGraphNode node, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
                _nodes[BuildScopedKey(node.Scope, node.NodeId)] = CloneNode(node);
            return Task.CompletedTask;
        }

        public Task UpsertEdgeAsync(ProjectionGraphEdge edge, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
                _edges[BuildScopedKey(edge.Scope, edge.EdgeId)] = CloneEdge(edge);
            return Task.CompletedTask;
        }

        public Task DeleteNodeAsync(string scope, string nodeId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
                _nodes.Remove(BuildScopedKey(scope, nodeId));
            return Task.CompletedTask;
        }

        public Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
                _edges.Remove(BuildScopedKey(scope, edgeId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProjectionGraphEdge>> ListEdgesByOwnerAsync(
            string scope,
            string ownerId,
            int skip = 0,
            int take = 5000,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ListEdgesByOwnerCallCount++;
            var scopeValue = NormalizeToken(scope);
            var ownerValue = NormalizeToken(ownerId);
            if (scopeValue.Length == 0 || ownerValue.Length == 0)
                return Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>([]);

            List<ProjectionGraphEdge> edges;
            lock (_gate)
            {
                edges = _edges.Values
                    .Where(x => string.Equals(x.Scope, scopeValue, StringComparison.Ordinal))
                    .Where(x =>
                        x.Properties.TryGetValue(ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey, out var edgeOwnerId) &&
                        string.Equals(NormalizeToken(edgeOwnerId), ownerValue, StringComparison.Ordinal))
                    .OrderByDescending(x => x.UpdatedAt)
                    .Skip(Math.Max(0, skip))
                    .Take(Math.Clamp(take, 1, 50000))
                    .Select(CloneEdge)
                    .ToList();
            }

            return Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>(edges);
        }

        public Task<IReadOnlyList<ProjectionGraphNode>> ListNodesByOwnerAsync(
            string scope,
            string ownerId,
            int skip = 0,
            int take = 5000,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var scopeValue = NormalizeToken(scope);
            var ownerValue = NormalizeToken(ownerId);
            if (scopeValue.Length == 0 || ownerValue.Length == 0)
                return Task.FromResult<IReadOnlyList<ProjectionGraphNode>>([]);

            List<ProjectionGraphNode> nodes;
            lock (_gate)
            {
                nodes = _nodes.Values
                    .Where(x => string.Equals(x.Scope, scopeValue, StringComparison.Ordinal))
                    .Where(x =>
                        x.Properties.TryGetValue(ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey, out var nodeOwnerId) &&
                        string.Equals(NormalizeToken(nodeOwnerId), ownerValue, StringComparison.Ordinal))
                    .OrderByDescending(x => x.UpdatedAt)
                    .Skip(Math.Max(0, skip))
                    .Take(Math.Clamp(take, 1, 50000))
                    .Select(CloneNode)
                    .ToList();
            }

            return Task.FromResult<IReadOnlyList<ProjectionGraphNode>>(nodes);
        }

        public Task<IReadOnlyList<ProjectionGraphEdge>> GetNeighborsAsync(
            ProjectionGraphQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var scope = NormalizeToken(query.Scope);
            var rootNodeId = NormalizeToken(query.RootNodeId);
            if (scope.Length == 0 || rootNodeId.Length == 0)
                return Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>([]);

            var edgeTypes = query.EdgeTypes
                .Select(NormalizeToken)
                .Where(x => x.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            List<ProjectionGraphEdge> edges;
            lock (_gate)
            {
                edges = _edges.Values
                    .Where(x => string.Equals(x.Scope, scope, StringComparison.Ordinal))
                    .Where(x => edgeTypes.Count == 0 || edgeTypes.Contains(x.EdgeType))
                    .Where(x => MatchDirection(x, rootNodeId, query.Direction))
                    .OrderByDescending(x => x.UpdatedAt)
                    .Take(Math.Clamp(query.Take, 1, 50000))
                    .Select(CloneEdge)
                    .ToList();
            }

            return Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>(edges);
        }

        public async Task<ProjectionGraphSubgraph> GetSubgraphAsync(
            ProjectionGraphQuery query,
            CancellationToken ct = default)
        {
            var edges = await GetNeighborsAsync(query, ct);
            var nodeIds = edges
                .SelectMany(x => new[] { x.FromNodeId, x.ToNodeId })
                .Append(query.RootNodeId)
                .Where(x => NormalizeToken(x).Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            List<ProjectionGraphNode> nodes;
            lock (_gate)
            {
                nodes = nodeIds
                    .Select(nodeId =>
                    {
                        if (_nodes.TryGetValue(BuildScopedKey(query.Scope, nodeId), out var node))
                            return CloneNode(node);

                        return new ProjectionGraphNode
                        {
                            Scope = query.Scope,
                            NodeId = nodeId,
                            NodeType = "Unknown",
                            Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                            UpdatedAt = DateTimeOffset.UtcNow,
                        };
                    })
                    .ToList();
            }

            return new ProjectionGraphSubgraph
            {
                Nodes = nodes,
                Edges = edges,
            };
        }

        private static bool MatchDirection(ProjectionGraphEdge edge, string rootNodeId, ProjectionGraphDirection direction)
        {
            return direction switch
            {
                ProjectionGraphDirection.Outbound => string.Equals(edge.FromNodeId, rootNodeId, StringComparison.Ordinal),
                ProjectionGraphDirection.Inbound => string.Equals(edge.ToNodeId, rootNodeId, StringComparison.Ordinal),
                _ => string.Equals(edge.FromNodeId, rootNodeId, StringComparison.Ordinal) ||
                     string.Equals(edge.ToNodeId, rootNodeId, StringComparison.Ordinal),
            };
        }

        private static ProjectionGraphNode CloneNode(ProjectionGraphNode source)
        {
            return new ProjectionGraphNode
            {
                Scope = source.Scope,
                NodeId = source.NodeId,
                NodeType = source.NodeType,
                Properties = new Dictionary<string, string>(source.Properties, StringComparer.Ordinal),
                UpdatedAt = source.UpdatedAt,
            };
        }

        private static ProjectionGraphEdge CloneEdge(ProjectionGraphEdge source)
        {
            return new ProjectionGraphEdge
            {
                Scope = source.Scope,
                EdgeId = source.EdgeId,
                FromNodeId = source.FromNodeId,
                ToNodeId = source.ToNodeId,
                EdgeType = source.EdgeType,
                Properties = new Dictionary<string, string>(source.Properties, StringComparer.Ordinal),
                UpdatedAt = source.UpdatedAt,
            };
        }

        private static string BuildScopedKey(string scope, string id) => $"{NormalizeToken(scope)}:{NormalizeToken(id)}";

        private static string NormalizeToken(string? token) => token?.Trim() ?? "";
    }
}
