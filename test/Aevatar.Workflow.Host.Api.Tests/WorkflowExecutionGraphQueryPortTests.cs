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
