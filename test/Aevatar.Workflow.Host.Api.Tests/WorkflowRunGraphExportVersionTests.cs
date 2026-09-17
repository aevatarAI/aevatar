using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowRunGraphExportVersionTests
{
    [Fact]
    public async Task ArtifactQueryPort_ShouldExportExactVersionedRouteAndSourceCoordinate()
    {
        var report = BuildReport("actor-1", "cmd-1", 12, "evt-12");
        var harness = await CreateHarnessAsync(report);

        var subgraph = await harness.Port.GetWorkflowRunGraphExportSubgraphAsync("actor-1");

        subgraph.SourceStateVersion.Should().Be(12);
        subgraph.RouteFingerprint.Should().BeEquivalentTo(new WorkflowRunGraphExportRouteFingerprint
        {
            ContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
            ContractVersion = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
            PhysicalNamespace = WorkflowExecutionGraphConstants.IncrementalPhysicalNamespace,
            RouteEpoch = 2,
        });
        subgraph.SourceCoordinate.Should().BeEquivalentTo(new WorkflowRunGraphExportSourceCoordinate
        {
            ActorId = "actor-1",
            StateVersion = 12,
            EventId = "evt-12",
        });
    }

    [Fact]
    public async Task ArtifactQueryPort_ShouldPreserveOwnerVersion_WhenTraversalOmitsRunNode()
    {
        var report = BuildReport("actor-1", "cmd-1", 12, "evt-12");
        report.Topology.Add(new WorkflowExecutionTopologyEdge("actor-1", "child-1"));
        var harness = await CreateHarnessAsync(report);

        var subgraph = await harness.Port.GetWorkflowRunGraphExportSubgraphAsync(
            "actor-1",
            depth: 1,
            take: 1,
            options: new WorkflowRunGraphExportQueryOptions
            {
                Direction = WorkflowRunGraphExportDirection.Inbound,
                EdgeTypes = [WorkflowExecutionGraphConstants.EdgeTypeChildOf],
            });

        subgraph.Nodes.Should().NotContain(node =>
            node.NodeType == WorkflowExecutionGraphConstants.RunNodeType);
        subgraph.SourceStateVersion.Should().Be(12);
        subgraph.SourceCoordinate.EventId.Should().Be("evt-12");
    }

    [Fact]
    public async Task ArtifactQueryPort_ShouldKeepRunScopedActorNode_WhenActorAppearsInAnotherOwnerGraph()
    {
        var store = new InMemoryProjectionGraphStore();
        var materializer = new WorkflowRunIncrementalGraphMaterializer(
            ProjectionGraphOwnerIdentityResolver.Instance);
        var route = IncrementalRoute();
        var first = BuildReport("actor-1", "cmd-1", 12, "evt-12", "first");
        first.Topology.Add(new WorkflowExecutionTopologyEdge("actor-1", "shared-actor"));
        var second = BuildReport("actor-2", "cmd-2", 99, "evt-99", "second");
        second.Topology.Add(new WorkflowExecutionTopologyEdge("actor-2", "shared-actor"));
        (await store.ApplyDeltaAsync(materializer.BuildFullCandidateDelta(
                first,
                WorkflowProjectionKinds.ExecutionMaterialization,
                route)))
            .Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.Applied);
        (await store.ApplyDeltaAsync(materializer.BuildFullCandidateDelta(
                second,
                WorkflowProjectionKinds.ExecutionMaterialization,
                route)))
            .Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var port = CreatePort(first, store, materializer, new StaticStatusReader(Status(route)));

        var subgraph = await port.GetWorkflowRunGraphExportSubgraphAsync("actor-1");

        subgraph.SourceStateVersion.Should().Be(12);
        subgraph.Nodes.Should().Contain(node =>
            node.NodeId == "actor:actor-1:cmd-1:shared-actor" &&
            node.Properties["actorId"] == "shared-actor" &&
            node.Properties["workflowName"] == "first");
        subgraph.Nodes.Should().NotContain(node => node.NodeId.Contains("actor-2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ArtifactQueryPort_WhenRouteChangesDuringRead_ShouldWithholdSnapshot()
    {
        var report = BuildReport("actor-1", "cmd-1", 12, "evt-12");
        var store = new InMemoryProjectionGraphStore();
        var materializer = new WorkflowRunIncrementalGraphMaterializer(
            ProjectionGraphOwnerIdentityResolver.Instance);
        var route = IncrementalRoute();
        (await store.ApplyDeltaAsync(materializer.BuildFullCandidateDelta(
                report,
                WorkflowProjectionKinds.ExecutionMaterialization,
                route)))
            .Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var changed = route.Clone();
        changed.RouteEpoch++;
        var statusReader = new SequencedStatusReader(Status(route), Status(changed));
        var port = CreatePort(report, store, materializer, statusReader);

        var subgraph = await port.GetWorkflowRunGraphExportSubgraphAsync("actor-1");

        subgraph.SourceStateVersion.Should().Be(0);
        subgraph.Nodes.Should().BeEmpty();
        subgraph.Edges.Should().BeEmpty();
        subgraph.RouteFingerprint.Should().BeNull();
    }

    [Fact]
    public async Task ArtifactQueryPort_WhenRouteIsMissingOrLegacy_WithoutLegacyStore_ShouldBeUnavailable()
    {
        var report = BuildReport("actor-1", "cmd-1", 12, "evt-12");
        var store = new InMemoryProjectionGraphStore();
        var materializer = new WorkflowRunIncrementalGraphMaterializer(
            ProjectionGraphOwnerIdentityResolver.Instance);
        var missingPort = CreatePort(report, store, materializer, new StaticStatusReader(Status(null)));
        var legacyPort = CreatePort(report, store, materializer, new StaticStatusReader(Status(LegacyRoute())));

        (await missingPort.GetWorkflowRunGraphExportSubgraphAsync("actor-1")).SourceStateVersion
            .Should().Be(0);
        (await legacyPort.GetWorkflowRunGraphExportSubgraphAsync("actor-1")).SourceStateVersion
            .Should().Be(0);
    }

    [Fact]
    public async Task ArtifactQueryPort_WhenRouteIsMissingOrLegacy_ShouldReadLegacyScopeGraph()
    {
        // Owners that never cut over keep their graph in the legacy scope graph store; the
        // committed route (or its absence) directs the read there instead of hiding history.
        var report = BuildReport("actor-1", "cmd-1", 12, "evt-12");
        report.StepIndexById["step-1"] = 0;
        report.Steps.Add(new WorkflowExecutionStepTrace { StepId = "step-1", StepType = "assign" });
        var store = new InMemoryProjectionGraphStore();
        var materializer = new WorkflowRunIncrementalGraphMaterializer(
            ProjectionGraphOwnerIdentityResolver.Instance);
        var legacyWriter = new ProjectionGraphWriter<WorkflowRunInsightReportDocument>(
            store,
            new WorkflowRunInsightReportGraphMaterializer(),
            ownerIdentityResolver: ProjectionGraphOwnerIdentityResolver.Instance);
        await legacyWriter.UpsertAsync(report, WorkflowProjectionKinds.ExecutionMaterialization);
        var missingPort = CreatePort(report, store, materializer, new StaticStatusReader(Status(null)), store);
        var legacyPort = CreatePort(report, store, materializer, new StaticStatusReader(Status(LegacyRoute())), store);

        foreach (var port in new[] { missingPort, legacyPort })
        {
            var subgraph = await port.GetWorkflowRunGraphExportSubgraphAsync("actor-1", depth: 3, take: 50);
            subgraph.SourceStateVersion.Should().Be(12);
            subgraph.Nodes.Select(static node => node.NodeId).Should().Contain("run:actor-1:cmd-1");
            subgraph.Nodes.Select(static node => node.NodeId).Should().Contain("step:actor-1:cmd-1:step-1");
            subgraph.RouteFingerprint.Should().BeNull();
            (await port.GetWorkflowRunGraphExportEdgesAsync("actor-1", take: 50)).Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task ArtifactQueryPort_WhenIncrementalRouteHasNoSnapshot_ShouldNotFallbackToLegacyGraph()
    {
        var report = BuildReport("actor-1", "cmd-1", 12, "evt-12");
        var store = new InMemoryProjectionGraphStore();
        var materializer = new WorkflowRunIncrementalGraphMaterializer(
            ProjectionGraphOwnerIdentityResolver.Instance);
        var legacyWriter = new ProjectionGraphWriter<WorkflowRunInsightReportDocument>(
            store,
            new WorkflowRunInsightReportGraphMaterializer(),
            ownerIdentityResolver: ProjectionGraphOwnerIdentityResolver.Instance);
        await legacyWriter.UpsertAsync(report, WorkflowProjectionKinds.ExecutionMaterialization);
        var port = CreatePort(report, store, materializer, new StaticStatusReader(Status(IncrementalRoute())), store);

        (await port.GetWorkflowRunGraphExportSubgraphAsync("actor-1")).SourceStateVersion.Should().Be(0);
        (await port.GetWorkflowRunGraphExportEdgesAsync("actor-1")).Should().BeEmpty();
    }

    private static async Task<QueryHarness> CreateHarnessAsync(WorkflowRunInsightReportDocument report)
    {
        var store = new InMemoryProjectionGraphStore();
        var materializer = new WorkflowRunIncrementalGraphMaterializer(
            ProjectionGraphOwnerIdentityResolver.Instance);
        var route = IncrementalRoute();
        var applied = await store.ApplyDeltaAsync(materializer.BuildFullCandidateDelta(
            report,
            WorkflowProjectionKinds.ExecutionMaterialization,
            route));
        applied.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.Applied);
        return new QueryHarness(
            CreatePort(report, store, materializer, new StaticStatusReader(Status(route))));
    }

    private static WorkflowExecutionArtifactQueryPort CreatePort(
        WorkflowRunInsightReportDocument report,
        InMemoryProjectionGraphStore store,
        WorkflowRunIncrementalGraphMaterializer materializer,
        IProjectionDocumentReader<ProjectionScopeStatusDocument, string> statusReader,
        IProjectionGraphStore? legacyGraphStore = null) =>
        new(
            new SingleDocumentReader<WorkflowRunInsightReportDocument>(report),
            new WorkflowExecutionReadModelMapper(),
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                WorkflowArtifactQueryEnabled = true,
            },
            statusReader,
            store,
            materializer,
            legacyGraphStore);

    private static ProjectionMaterializationRouteFingerprint LegacyRoute() =>
        new()
        {
            ContractId = WorkflowExecutionGraphConstants.LegacyContractId,
            ContractVersion = WorkflowExecutionGraphConstants.LegacyContractVersion,
            PhysicalNamespace = WorkflowExecutionGraphConstants.Scope,
            RouteEpoch = 1,
        };

    private static WorkflowRunInsightReportDocument BuildReport(
        string actorId,
        string commandId,
        long version,
        string eventId,
        string workflowName = "workflow") =>
        new()
        {
            Id = actorId,
            RootActorId = actorId,
            CommandId = commandId,
            WorkflowName = workflowName,
            StateVersion = version,
            LastEventId = eventId,
            UpdatedAt = DateTimeOffset.Parse("2026-08-18T08:00:00Z"),
        };

    private static ProjectionMaterializationRouteFingerprint IncrementalRoute() =>
        new()
        {
            ContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
            ContractVersion = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
            PhysicalNamespace = WorkflowExecutionGraphConstants.IncrementalPhysicalNamespace,
            RouteEpoch = 2,
        };

    private static ProjectionScopeStatusDocument Status(
        ProjectionMaterializationRouteFingerprint? route) =>
        new()
        {
            Active = true,
            Released = false,
            ActiveMaterializationRoute = route?.Clone(),
        };

    private sealed record QueryHarness(WorkflowExecutionArtifactQueryPort Port);

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
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StaticStatusReader(ProjectionScopeStatusDocument status)
        : IProjectionDocumentReader<ProjectionScopeStatusDocument, string>
    {
        public Task<ProjectionScopeStatusDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            _ = key;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ProjectionScopeStatusDocument?>(status.Clone());
        }

        public Task<ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class SequencedStatusReader(params ProjectionScopeStatusDocument[] statuses)
        : IProjectionDocumentReader<ProjectionScopeStatusDocument, string>
    {
        private int _index;

        public Task<ProjectionScopeStatusDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            _ = key;
            ct.ThrowIfCancellationRequested();
            var index = Math.Min(_index++, statuses.Length - 1);
            return Task.FromResult<ProjectionScopeStatusDocument?>(statuses[index].Clone());
        }

        public Task<ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
