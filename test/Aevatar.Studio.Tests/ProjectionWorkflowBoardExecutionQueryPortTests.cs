using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.WorkflowBoards;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Hosting.WorkflowBoards;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class ProjectionWorkflowBoardExecutionQueryPortTests
{
    [Fact]
    public void AddWorkflowBoardExecutionProjectionAdapter_ShouldRegisterExecutionPortOnlyWhenBoardReaderExists()
    {
        var withoutReader = new ServiceCollection();
        withoutReader.AddWorkflowBoardExecutionProjectionAdapter();

        withoutReader.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IWorkflowBoardExecutionQueryPort));

        var withReader = new ServiceCollection();
        withReader.AddSingleton<IProjectionDocumentReader<WorkflowExecutionBoardDocument, string>>(
            new StubDocumentReader([]));
        withReader.AddWorkflowBoardExecutionProjectionAdapter();

        withReader.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IWorkflowBoardExecutionQueryPort) &&
            descriptor.ImplementationType == typeof(WorkflowProjectionBoardExecutionQueryPort));
    }

    [Fact]
    public async Task GetCurrentExecutionAsync_ShouldMapBoardDocumentByActorId()
    {
        var reader = new StubDocumentReader([
            new WorkflowExecutionBoardDocument
            {
                Id = "actor-alpha",
                RootActorId = "actor-alpha",
                CommandId = "cmd-alpha",
                DefinitionActorId = "definition-alpha",
                RunId = "run-alpha",
                WorkflowName = "workflow-alpha",
                ScopeId = "scope-alpha",
                CompletionStatus = WorkflowExecutionBoardCompletionStatus.Running,
                CurrentNodeId = "node-current",
                StateVersion = 9,
                LastEventId = "evt-9",
                LastNodeUpdatedAt = DateTimeOffset.Parse("2026-06-24T13:20:00Z"),
                Summary = new WorkflowExecutionBoardSummaryReadModel
                {
                    CompletedSteps = 1,
                    RunningNodes = 1,
                    WaitingOrPendingNodes = 1,
                    FailedNodes = 1,
                    DefinitionStepCount = 15,
                },
                NodeEntries =
                {
                    new WorkflowExecutionBoardNodeReadModel
                    {
                        NodeId = "node-done",
                        Name = "Done",
                        Status = WorkflowExecutionBoardNodeStatus.Completed,
                        CompletedAt = DateTimeOffset.Parse("2026-06-24T13:10:00Z"),
                        DurationMs = 1000,
                    },
                    new WorkflowExecutionBoardNodeReadModel
                    {
                        NodeId = "node-current",
                        Name = "Current",
                        Status = WorkflowExecutionBoardNodeStatus.Running,
                        RequestedAt = DateTimeOffset.Parse("2026-06-24T13:11:00Z"),
                        UpdatedAt = DateTimeOffset.Parse("2026-06-24T13:20:00Z"),
                        DurationMs = 540000,
                    },
                    new WorkflowExecutionBoardNodeReadModel
                    {
                        NodeId = "node-waiting",
                        Name = "Waiting",
                        Status = WorkflowExecutionBoardNodeStatus.Waiting,
                    },
                    new WorkflowExecutionBoardNodeReadModel
                    {
                        NodeId = "node-failed",
                        Name = "Failed",
                        Status = WorkflowExecutionBoardNodeStatus.Failed,
                        CompletedAt = DateTimeOffset.Parse("2026-06-24T13:18:00Z"),
                    },
                },
            },
        ]);
        var port = new WorkflowProjectionBoardExecutionQueryPort(reader);

        var snapshot = await port.GetCurrentExecutionAsync(new WorkflowBoardExecutionLookup(
            "scope-alpha",
            "team-alpha",
            "m-alpha",
            WorkflowId: "wf-alpha",
            PublishedServiceId: "svc-alpha",
            ActorId: "actor-alpha"));

        snapshot.Should().NotBeNull();
        snapshot!.Availability.Should().Be(WorkflowBoardExecutionAvailability.Available);
        snapshot.CurrentExecutionId.Should().Be("run-alpha");
        snapshot.CurrentNode.Should().NotBeNull();
        snapshot.CurrentNode!.NodeId.Should().Be("node-current");
        snapshot.CurrentNode.Status.Should().Be(WorkflowBoardCurrentNodeStatus.Running);
        snapshot.CompletedNodes.Should().ContainSingle()
            .Which.NodeId.Should().Be("node-done");
        snapshot.PendingNodes.Should().ContainSingle()
            .Which.Status.Should().Be(WorkflowBoardPendingNodeStatus.Waiting);
        snapshot.FailedNodes.Should().ContainSingle()
            .Which.NodeId.Should().Be("node-failed");
        snapshot.LastNodeUpdatedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:20:00Z"));
        snapshot.ExecutionStatus.Should().Be(WorkflowBoardMemberExecutionStatus.Running);
        snapshot.Summary.Should().Be(new WorkflowBoardExecutionSummary(1, 1, 1, 1, 15));
        snapshot.Revision.Should().Be("state-version-9:event-evt-9");
    }

    [Fact]
    public async Task GetCurrentExecutionAsync_ShouldMapLatestServiceRunBoardDocument()
    {
        var reader = new StubDocumentReader([
            new WorkflowExecutionBoardDocument
            {
                Id = "run-actor-alpha",
                RootActorId = "run-actor-alpha",
                CommandId = "cmd-alpha",
                DefinitionActorId = "definition-alpha",
                RunId = "run-actor-alpha",
                WorkflowName = "workflow-alpha",
                ScopeId = "scope-alpha",
                CompletionStatus = WorkflowExecutionBoardCompletionStatus.WaitingForSignal,
                CurrentNodeId = "wait_for_board_signal",
                StateVersion = 8,
                LastEventId = "evt-8",
                Summary = new WorkflowExecutionBoardSummaryReadModel
                {
                    CompletedSteps = 0,
                    RunningNodes = 0,
                    WaitingOrPendingNodes = 1,
                    FailedNodes = 0,
                    DefinitionStepCount = 15,
                },
                NodeEntries =
                {
                    new WorkflowExecutionBoardNodeReadModel
                    {
                        NodeId = "wait_for_board_signal",
                        Name = "wait_for_board_signal",
                        Status = WorkflowExecutionBoardNodeStatus.Waiting,
                    },
                },
            },
        ]);
        var serviceRuns = new StubServiceRunQueryPort([
            BuildServiceRun(
                scopeId: "scope-alpha",
                serviceId: "svc-alpha",
                runId: "run-actor-alpha",
                targetActorId: "run-actor-alpha",
                updatedAt: DateTimeOffset.Parse("2026-06-24T13:20:00Z")),
        ]);
        var port = new WorkflowProjectionBoardExecutionQueryPort(reader, serviceRuns);

        var snapshot = await port.GetCurrentExecutionAsync(new WorkflowBoardExecutionLookup(
            "scope-alpha",
            "team-alpha",
            "m-alpha",
            WorkflowId: "wf-alpha",
            PublishedServiceId: "svc-alpha",
            ActorId: "definition-alpha"));

        snapshot.Should().NotBeNull();
        snapshot!.Availability.Should().Be(WorkflowBoardExecutionAvailability.Available);
        snapshot.CurrentExecutionId.Should().Be("run-actor-alpha");
        snapshot.PendingNodes.Should().ContainSingle()
            .Which.NodeId.Should().Be("wait_for_board_signal");
        snapshot.ExecutionStatus.Should().Be(WorkflowBoardMemberExecutionStatus.Waiting);
        snapshot.Summary.Should().Be(new WorkflowBoardExecutionSummary(0, 0, 1, 0, 15));
        serviceRuns.Queries.Should().ContainSingle()
            .Which.Should().Be(new ServiceRunQuery("scope-alpha", "svc-alpha", 1));
    }

    [Fact]
    public async Task GetCurrentExecutionAsync_ShouldReturnUnavailableForMissingActorOrScopeMismatch()
    {
        var reader = new StubDocumentReader([
            new WorkflowExecutionBoardDocument
            {
                Id = "actor-alpha",
                RootActorId = "actor-alpha",
                ScopeId = "other-scope",
            },
        ]);
        var port = new WorkflowProjectionBoardExecutionQueryPort(reader);

        var blankActor = await port.GetCurrentExecutionAsync(new WorkflowBoardExecutionLookup(
            "scope-alpha",
            "team-alpha",
            "m-alpha",
            WorkflowId: "wf-alpha",
            PublishedServiceId: "svc-alpha",
            ActorId: " "));
        var missing = await port.GetCurrentExecutionAsync(new WorkflowBoardExecutionLookup(
            "scope-alpha",
            "team-alpha",
            "m-alpha",
            WorkflowId: "wf-alpha",
            PublishedServiceId: "svc-alpha",
            ActorId: "missing-actor"));
        var scopeMismatch = await port.GetCurrentExecutionAsync(new WorkflowBoardExecutionLookup(
            "scope-alpha",
            "team-alpha",
            "m-alpha",
            WorkflowId: "wf-alpha",
            PublishedServiceId: "svc-alpha",
            ActorId: "actor-alpha"));

        blankActor.Should().BeEquivalentTo(Unavailable());
        missing.Should().BeEquivalentTo(Unavailable());
        scopeMismatch.Should().BeEquivalentTo(Unavailable());
    }

    private static WorkflowBoardExecutionSnapshot Unavailable() =>
        new(
            WorkflowBoardExecutionAvailability.Unavailable,
            [],
            [],
            []);

    private static ServiceRunSnapshot BuildServiceRun(
        string scopeId,
        string serviceId,
        string runId,
        string targetActorId,
        DateTimeOffset updatedAt) =>
        new(
            scopeId,
            serviceId,
            $"{scopeId}:default:default:{serviceId}",
            runId,
            "cmd-alpha",
            "corr-alpha",
            "chat",
            string.Empty,
            ServiceImplementationKind.Workflow,
            targetActorId,
            "rev-alpha",
            "deployment-alpha",
            ServiceRunStatus.Accepted,
            runId,
            string.Empty,
            string.Empty,
            "default",
            1,
            "event-alpha",
            updatedAt,
            updatedAt,
            string.Empty,
            string.Empty);

    private sealed class StubDocumentReader
        : IProjectionDocumentReader<WorkflowExecutionBoardDocument, string>
    {
        private readonly Dictionary<string, WorkflowExecutionBoardDocument> _documents;

        public StubDocumentReader(IEnumerable<WorkflowExecutionBoardDocument> documents)
        {
            _documents = documents.ToDictionary(x => x.Id, StringComparer.Ordinal);
        }

        public Task<WorkflowExecutionBoardDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_documents.TryGetValue(key, out var document) ? document : null);
        }

        public Task<ProjectionDocumentQueryResult<WorkflowExecutionBoardDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectionDocumentQueryResult<WorkflowExecutionBoardDocument>
            {
                Items = _documents.Values.ToList(),
            });
        }
    }

    private sealed class StubServiceRunQueryPort(
        IEnumerable<ServiceRunSnapshot> runs)
        : IServiceRunQueryPort
    {
        private readonly IReadOnlyList<ServiceRunSnapshot> _runs = runs.ToArray();

        public List<ServiceRunQuery> Queries { get; } = [];

        public Task<IReadOnlyList<ServiceRunSnapshot>> ListAsync(
            ServiceRunQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Queries.Add(query);
            var result = _runs
                .Where(run =>
                    string.Equals(run.ScopeId, query.ScopeId, StringComparison.Ordinal) &&
                    string.Equals(run.ServiceId, query.ServiceId, StringComparison.Ordinal))
                .OrderByDescending(static run => run.UpdatedAt)
                .Take(query.Take)
                .ToArray();
            return Task.FromResult<IReadOnlyList<ServiceRunSnapshot>>(result);
        }

        public Task<ServiceRunSnapshot?> GetByRunIdAsync(
            string scopeId,
            string serviceId,
            string runId,
            CancellationToken ct = default) =>
            Task.FromResult(_runs.FirstOrDefault(run =>
                string.Equals(run.ScopeId, scopeId, StringComparison.Ordinal) &&
                string.Equals(run.ServiceId, serviceId, StringComparison.Ordinal) &&
                string.Equals(run.RunId, runId, StringComparison.Ordinal)));

        public Task<ServiceRunSnapshot?> GetByCommandIdAsync(
            string scopeId,
            string serviceId,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult(_runs.FirstOrDefault(run =>
                string.Equals(run.ScopeId, scopeId, StringComparison.Ordinal) &&
                string.Equals(run.ServiceId, serviceId, StringComparison.Ordinal) &&
                string.Equals(run.CommandId, commandId, StringComparison.Ordinal)));
    }
}
