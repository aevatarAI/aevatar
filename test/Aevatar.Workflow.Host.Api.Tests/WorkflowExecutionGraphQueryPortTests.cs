using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionGraphQueryPortTests
{
    [Fact]
    public async Task VersionedSnapshot_ShouldFilterDirectionAndEdgeTypes()
    {
        var now = DateTimeOffset.UtcNow;
        var harness = CreateHarness(new ProjectionGraphOwnerSnapshot
        {
            Nodes =
            {
                NodeMutation("actor-1", "Actor", now),
                NodeMutation("actor-2", "Actor", now),
            },
            Edges =
            {
                EdgeMutation("edge-1", "actor-2", "actor-1", "CHILD_OF", now),
                EdgeMutation("edge-2", "actor-1", "actor-2", "OWNS", now),
            },
        });
        var options = new WorkflowRunGraphExportQueryOptions
        {
            Direction = WorkflowRunGraphExportDirection.Inbound,
            EdgeTypes = ["CHILD_OF"],
        };

        var edges = await harness.Port.GetWorkflowRunGraphExportEdgesAsync(
            "actor-1",
            take: 7,
            options: options);
        var subgraph = await harness.Port.GetWorkflowRunGraphExportSubgraphAsync(
            "actor-1",
            depth: 4,
            take: 11,
            options: options);

        edges.Should().ContainSingle(x => x.EdgeId == "edge-1");
        subgraph.RootNodeId.Should().Be("actor-1");
        subgraph.SourceStateVersion.Should().Be(12);
        subgraph.Edges.Should().ContainSingle(x => x.EdgeId == "edge-1");
        harness.VersionedStore.ReadCount.Should().Be(2);
    }

    [Fact]
    public async Task InvalidDirectionAndBlankEdgeTypes_ShouldNormalizeBeforeFiltering()
    {
        var now = DateTimeOffset.UtcNow;
        var harness = CreateHarness(new ProjectionGraphOwnerSnapshot
        {
            Nodes =
            {
                NodeMutation("actor-1", "Actor", now),
                NodeMutation("actor-2", "Actor", now),
            },
            Edges =
            {
                EdgeMutation("edge-1", "actor-1", "actor-2", "CHILD_OF", now),
                EdgeMutation("edge-2", "actor-2", "actor-1", "IGNORED", now),
            },
        });
        var options = new WorkflowRunGraphExportQueryOptions
        {
            Direction = (WorkflowRunGraphExportDirection)99,
            EdgeTypes = [" CHILD_OF ", "", "CHILD_OF", "  ", "OWNS"],
        };

        var edges = await harness.Port.GetWorkflowRunGraphExportEdgesAsync(
            "actor-1",
            take: 0,
            options: options);
        var subgraph = await harness.Port.GetWorkflowRunGraphExportSubgraphAsync(
            "actor-1",
            depth: 99,
            take: 5001,
            options: options);

        edges.Should().ContainSingle(x => x.EdgeId == "edge-1");
        subgraph.Edges.Should().ContainSingle(x => x.EdgeId == "edge-1");
    }

    // ── take budget: every returned edge's endpoints are returned nodes ────────────────────

    [Fact]
    public async Task VersionedSnapshot_TakeOne_ShouldReturnEdgeWithBothEndpoints()
    {
        var harness = CreateHarness(ChainSnapshot());

        var subgraph = await harness.Port.GetWorkflowRunGraphExportSubgraphAsync("actor-1", depth: 4, take: 1);

        subgraph.Edges.Should().ContainSingle().Which.EdgeId.Should().Be("edge-1");
        subgraph.Nodes.Select(static node => node.NodeId).Should().Equal("actor-1", "child-1");
        AssertEndpointClosure(subgraph);
    }

    [Theory]
    [InlineData(1, new[] { "edge-1" })]
    [InlineData(2, new[] { "edge-1", "edge-2" })]
    [InlineData(3, new[] { "edge-1", "edge-2", "edge-3" })]
    [InlineData(50, new[] { "edge-1", "edge-2", "edge-3" })]
    public async Task VersionedSnapshot_Chain_ShouldHonourEdgeBudgetDeterministically(int take, string[] expectedEdges)
    {
        var harness = CreateHarness(ChainSnapshot());

        var subgraph = await harness.Port.GetWorkflowRunGraphExportSubgraphAsync("actor-1", depth: 8, take: take);

        subgraph.Edges.Select(static edge => edge.EdgeId).Should().Equal(expectedEdges);
        subgraph.Nodes.Should().HaveCount(expectedEdges.Length + 1, "root plus one endpoint per chain edge");
        AssertEndpointClosure(subgraph);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task VersionedSnapshot_Branch_ShouldNeverReturnDanglingEdgesAtTakeBoundary(int take)
    {
        var now = DateTimeOffset.UtcNow;
        // root -> a, root -> b, a -> c: at take=1/2 the budget ends mid-level; at take=3 the
        // second level is reached. Whatever the cut, endpoints must be present.
        var harness = CreateHarness(new ProjectionGraphOwnerSnapshot
        {
            Nodes =
            {
                NodeMutation("actor-1", "Actor", now),
                NodeMutation("branch-a", "Step", now),
                NodeMutation("branch-b", "Step", now),
                NodeMutation("branch-c", "Step", now),
            },
            Edges =
            {
                EdgeMutation("edge-a-c", "branch-a", "branch-c", "NEXT", now),
                EdgeMutation("edge-root-a", "actor-1", "branch-a", "NEXT", now),
                EdgeMutation("edge-root-b", "actor-1", "branch-b", "NEXT", now),
            },
        });

        var subgraph = await harness.Port.GetWorkflowRunGraphExportSubgraphAsync("actor-1", depth: 4, take: take);
        var again = await harness.Port.GetWorkflowRunGraphExportSubgraphAsync("actor-1", depth: 4, take: take);

        subgraph.Edges.Should().HaveCount(take);
        // Level 1 edges (in stable edge-id order) come before level 2 edges.
        subgraph.Edges.Select(static edge => edge.EdgeId).Should().Equal(
            new[] { "edge-root-a", "edge-root-b", "edge-a-c" }.Take(take));
        AssertEndpointClosure(subgraph);
        again.Edges.Select(static edge => edge.EdgeId).Should().Equal(subgraph.Edges.Select(static edge => edge.EdgeId));
        again.Nodes.Select(static node => node.NodeId).Should().Equal(subgraph.Nodes.Select(static node => node.NodeId));
    }

    [Theory]
    [InlineData(WorkflowRunGraphExportDirection.Outbound, "edge-out")]
    [InlineData(WorkflowRunGraphExportDirection.Inbound, "edge-in")]
    public async Task VersionedSnapshot_DirectionalTakeOne_ShouldReturnTheReachedEndpoint(
        WorkflowRunGraphExportDirection direction,
        string expectedEdgeId)
    {
        var now = DateTimeOffset.UtcNow;
        var harness = CreateHarness(new ProjectionGraphOwnerSnapshot
        {
            Nodes =
            {
                NodeMutation("actor-1", "Actor", now),
                NodeMutation("downstream", "Step", now),
                NodeMutation("upstream", "Step", now),
            },
            Edges =
            {
                EdgeMutation("edge-in", "upstream", "actor-1", "NEXT", now),
                EdgeMutation("edge-out", "actor-1", "downstream", "NEXT", now),
            },
        });

        var subgraph = await harness.Port.GetWorkflowRunGraphExportSubgraphAsync(
            "actor-1",
            depth: 2,
            take: 1,
            options: new WorkflowRunGraphExportQueryOptions { Direction = direction });
        var both = await harness.Port.GetWorkflowRunGraphExportSubgraphAsync(
            "actor-1",
            depth: 2,
            take: 1,
            options: new WorkflowRunGraphExportQueryOptions { Direction = WorkflowRunGraphExportDirection.Both });

        subgraph.Edges.Should().ContainSingle().Which.EdgeId.Should().Be(expectedEdgeId);
        subgraph.Nodes.Should().HaveCount(2);
        AssertEndpointClosure(subgraph);
        both.Edges.Should().ContainSingle().Which.EdgeId.Should().Be("edge-in", "stable edge-id order");
        AssertEndpointClosure(both);
    }

    [Fact]
    public async Task VersionedSnapshot_EdgeWithoutSnapshotEndpoint_ShouldNotBeReturned()
    {
        var now = DateTimeOffset.UtcNow;
        var harness = CreateHarness(new ProjectionGraphOwnerSnapshot
        {
            Nodes = { NodeMutation("actor-1", "Actor", now) },
            Edges = { EdgeMutation("edge-ghost", "actor-1", "missing", "NEXT", now) },
        });

        var subgraph = await harness.Port.GetWorkflowRunGraphExportSubgraphAsync("actor-1", depth: 2, take: 5);

        subgraph.Edges.Should().BeEmpty();
        subgraph.Nodes.Select(static node => node.NodeId).Should().Equal("actor-1");
    }

    private static ProjectionGraphOwnerSnapshot ChainSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return new ProjectionGraphOwnerSnapshot
        {
            Nodes =
            {
                NodeMutation("actor-1", "Actor", now),
                NodeMutation("child-1", "Step", now),
                NodeMutation("child-2", "Step", now),
                NodeMutation("child-3", "Step", now),
            },
            Edges =
            {
                EdgeMutation("edge-1", "actor-1", "child-1", "NEXT", now),
                EdgeMutation("edge-2", "child-1", "child-2", "NEXT", now),
                EdgeMutation("edge-3", "child-2", "child-3", "NEXT", now),
            },
        };
    }

    private static void AssertEndpointClosure(WorkflowRunGraphExportSubgraph subgraph)
    {
        var nodeIds = subgraph.Nodes.Select(static node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        foreach (var edge in subgraph.Edges)
        {
            nodeIds.Should().Contain(edge.FromNodeId, $"edge {edge.EdgeId} must not dangle at its source");
            nodeIds.Should().Contain(edge.ToNodeId, $"edge {edge.EdgeId} must not dangle at its target");
        }
    }

    private static VersionedGraphHarness CreateHarness(ProjectionGraphOwnerSnapshot snapshot)
    {
        var route = new ProjectionMaterializationRouteFingerprint
        {
            ContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
            ContractVersion = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
            PhysicalNamespace = WorkflowExecutionGraphConstants.IncrementalPhysicalNamespace,
            RouteEpoch = 2,
        };
        var materializer = new WorkflowRunIncrementalGraphMaterializer(
            ProjectionGraphOwnerIdentityResolver.Instance);
        snapshot.Route = materializer.ResolveStoreRoute(
            WorkflowProjectionKinds.ExecutionMaterialization,
            "actor-1",
            route);
        snapshot.Source = new ProjectionGraphSourceCoordinate
        {
            ActorId = "actor-1",
            StateVersion = 12,
            EventId = "evt-12",
        };
        var versionedStore = new RecordingVersionedProjectionGraphStore(snapshot);
        var port = new WorkflowExecutionArtifactQueryPort(
            new StaticDocumentReader<WorkflowRunInsightReportDocument>(),
            new WorkflowExecutionReadModelMapper(),
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                WorkflowArtifactQueryEnabled = true,
            },
            new StaticDocumentReader<ProjectionScopeStatusDocument>(new ProjectionScopeStatusDocument
            {
                Active = true,
                ActiveMaterializationRoute = route,
            }),
            versionedStore,
            materializer);
        return new VersionedGraphHarness(port, versionedStore);
    }

    private static ProjectionGraphNodeMutation NodeMutation(
        string nodeId,
        string nodeType,
        DateTimeOffset updatedAt) =>
        new()
        {
            NodeId = nodeId,
            NodeType = nodeType,
            UpdatedAtEpochMs = updatedAt.ToUnixTimeMilliseconds(),
        };

    private static ProjectionGraphEdgeMutation EdgeMutation(
        string edgeId,
        string fromNodeId,
        string toNodeId,
        string edgeType,
        DateTimeOffset updatedAt) =>
        new()
        {
            EdgeId = edgeId,
            FromNodeId = fromNodeId,
            ToNodeId = toNodeId,
            EdgeType = edgeType,
            UpdatedAtEpochMs = updatedAt.ToUnixTimeMilliseconds(),
        };

    private sealed record VersionedGraphHarness(
        WorkflowExecutionArtifactQueryPort Port,
        RecordingVersionedProjectionGraphStore VersionedStore);

    private sealed class StaticDocumentReader<TDocument>(TDocument? document = null)
        : IProjectionDocumentReader<TDocument, string>
        where TDocument : class, IProjectionReadModel
    {
        public Task<TDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(document);
        }

        public Task<ProjectionDocumentQueryResult<TDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingVersionedProjectionGraphStore(ProjectionGraphOwnerSnapshot snapshot)
        : IVersionedProjectionGraphStore
    {
        public int ReadCount { get; private set; }

        public Task<ProjectionGraphDeltaApplyResult> ApplyDeltaAsync(
            ProjectionGraphDelta delta,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ProjectionGraphOwnerSnapshotReadResult> ReadOwnerSnapshotAsync(
            ProjectionGraphRouteFingerprint route,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(new ProjectionGraphOwnerSnapshotReadResult
            {
                Disposition = ProjectionGraphOwnerSnapshotReadDisposition.Found,
                Snapshot = snapshot.Clone(),
            });
        }
    }
}
