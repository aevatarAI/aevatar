using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowRunGraphExportVersionTests
{
    [Fact]
    public async Task ArtifactQueryPort_ShouldUseGraphRunNodeVersion_WhenSharedGraphNodePropertiesDiffer()
    {
        var port = new WorkflowExecutionArtifactQueryPort(
            new SingleDocumentReader<WorkflowRunInsightReportDocument>(new WorkflowRunInsightReportDocument
            {
                Id = "actor-1",
                StateVersion = 12,
            }),
            new WorkflowExecutionReadModelMapper(),
            new StaticProjectionGraphStore(new ProjectionGraphSubgraph
            {
                Nodes =
                [
                    new ProjectionGraphNode
                    {
                        NodeId = "run:actor-1:cmd-1",
                        NodeType = WorkflowExecutionGraphConstants.RunNodeType,
                        Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [WorkflowExecutionGraphConstants.RootActorIdPropertyKey] = "actor-1",
                            [WorkflowExecutionGraphConstants.SourceStateVersionPropertyKey] = "12",
                        },
                    },
                    new ProjectionGraphNode
                    {
                        NodeId = "actor-1",
                        Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["sourceStateVersion"] = "99",
                        },
                    },
                    new ProjectionGraphNode
                    {
                        NodeId = "step-1",
                        Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["sourceStateVersion"] = "11",
                        },
                    },
                ],
            }),
            new WorkflowExecutionProjectionOptions { Enabled = true, WorkflowArtifactQueryEnabled = true });

        var subgraph = await port.GetWorkflowRunGraphExportSubgraphAsync("actor-1");

        subgraph.SourceStateVersion.Should().Be(12);
    }

    [Fact]
    public async Task ArtifactQueryPort_ShouldKeepOwnerGraphVersionAndRunScopedActorNode_WhenSharedActorAppearsInAnotherRun()
    {
        var graphStore = new InMemoryProjectionGraphStore();
        var graphWriter = new ProjectionGraphWriter<WorkflowRunInsightReportDocument>(
            graphStore,
            new WorkflowRunInsightReportGraphMaterializer());
        await graphWriter.UpsertAsync(new WorkflowRunInsightReportDocument
        {
            Id = "actor-1",
            RootActorId = "actor-1",
            CommandId = "cmd-1",
            WorkflowName = "first",
            StateVersion = 12,
            Topology =
            {
                new WorkflowExecutionTopologyEdge("actor-1", "shared-actor"),
            },
        });
        await graphWriter.UpsertAsync(new WorkflowRunInsightReportDocument
        {
            Id = "actor-2",
            RootActorId = "actor-2",
            CommandId = "cmd-2",
            WorkflowName = "second",
            StateVersion = 99,
            Topology =
            {
                new WorkflowExecutionTopologyEdge("actor-2", "shared-actor"),
            },
        });
        var port = new WorkflowExecutionArtifactQueryPort(
            new SingleDocumentReader<WorkflowRunInsightReportDocument>(new WorkflowRunInsightReportDocument
            {
                Id = "actor-1",
                StateVersion = 12,
            }),
            new WorkflowExecutionReadModelMapper(),
            graphStore,
            new WorkflowExecutionProjectionOptions { Enabled = true, WorkflowArtifactQueryEnabled = true });

        var subgraph = await port.GetWorkflowRunGraphExportSubgraphAsync("actor-1");

        subgraph.SourceStateVersion.Should().Be(12);
        subgraph.Nodes.Should().Contain(node =>
            node.NodeId == "actor:actor-1:cmd-1:shared-actor" &&
            node.Properties["actorId"] == "shared-actor" &&
            node.Properties["workflowName"] == "first");
    }

    private sealed class SingleDocumentReader<TReadModel>(TReadModel? document)
        : IProjectionDocumentReader<TReadModel, string>
        where TReadModel : class, IProjectionReadModel
    {
        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            _ = key;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(document);
        }

        public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            _ = query;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectionDocumentQueryResult<TReadModel>());
        }
    }

    private sealed class StaticProjectionGraphStore(ProjectionGraphSubgraph subgraph) : IProjectionGraphStore
    {
        public Task ReplaceOwnerGraphAsync(ProjectionOwnedGraph graph, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpsertNodeAsync(ProjectionGraphNode node, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpsertEdgeAsync(ProjectionGraphEdge edge, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteNodeAsync(string scope, string nodeId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectionGraphNode>> ListNodesByOwnerAsync(
            string scope,
            string ownerId,
            int skip = 0,
            int take = 5000,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectionGraphEdge>> ListEdgesByOwnerAsync(
            string scope,
            string ownerId,
            int skip = 0,
            int take = 5000,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectionGraphEdge>> GetNeighborsAsync(
            ProjectionGraphQuery query,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>([]);

        public Task<ProjectionGraphSubgraph> GetSubgraphAsync(
            ProjectionGraphQuery query,
            CancellationToken ct = default)
        {
            _ = query;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(subgraph);
        }
    }
}
