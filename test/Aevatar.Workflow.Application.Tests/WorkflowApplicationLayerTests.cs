using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Orchestration;
using Aevatar.Workflow.Application.Queries;
using Aevatar.Workflow.Application.Reporting;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Application.Tests;

public class WorkflowChatRunApplicationServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenWorkflowMissing_ShouldReturnWorkflowNotFound()
    {
        var runtime = new FakeActorRuntime([]);
        var registry = new WorkflowDefinitionRegistry();
        var orchestrator = new SpyRunOrchestrator();
        var service = new WorkflowChatRunApplicationService(
            runtime,
            registry,
            orchestrator,
            new FakeEnvelopeFactory(),
            new NoopReportSink(),
            LoggerFactory.Create(_ => { }).CreateLogger<WorkflowChatRunApplicationService>());

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", "missing", null),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowNotFound);
        result.Started.Should().BeNull();
        orchestrator.StartCalled.Should().BeFalse();
    }
}

public class WorkflowExecutionQueryApplicationServiceTests
{
    [Fact]
    public async Task ListRunsAsync_ShouldMapProjectionReportToTypedSummary()
    {
        var report = new WorkflowExecutionReport
        {
            RunId = "run-1",
            WorkflowName = "direct",
            RootActorId = "actor-1",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-2),
            EndedAt = DateTimeOffset.UtcNow,
            DurationMs = 200,
            Success = true,
            ProjectionScope = "actor_shared",
            CompletionStatus = "completed",
            Summary = new WorkflowExecutionSummary { TotalSteps = 3 },
        };

        var runtime = new FakeActorRuntime([]);
        var registry = new WorkflowDefinitionRegistry();
        registry.Register("direct", WorkflowDefinitionRegistry.BuiltInDirectYaml);
        var queryService = new WorkflowExecutionQueryApplicationService(
            runtime,
            registry,
            new FakeProjectionService
            {
                EnableRunQueryEndpointsValue = true,
                Runs = [report],
                ReportByRunId = new Dictionary<string, WorkflowExecutionReport>(StringComparer.Ordinal)
                {
                    ["run-1"] = report,
                },
            });

        var runs = await queryService.ListRunsAsync(50, CancellationToken.None);
        var run = runs.Should().ContainSingle().Subject;

        run.RunId.Should().Be("run-1");
        run.ProjectionScope.Should().Be(WorkflowRunProjectionScope.ActorShared);
        run.CompletionStatus.Should().Be(WorkflowRunCompletionStatus.Completed);

        var detail = await queryService.GetRunAsync("run-1", CancellationToken.None);
        detail.Should().NotBeNull();
        detail!.RunId.Should().Be("run-1");
        detail.ProjectionScope.Should().Be(WorkflowRunProjectionScope.ActorShared);
        detail.CompletionStatus.Should().Be(WorkflowRunCompletionStatus.Completed);
    }
}

public class ActorRuntimeWorkflowExecutionTopologyResolverTests
{
    [Fact]
    public async Task ResolveAsync_ShouldOnlyReturnReachableEdgesFromRoot()
    {
        var runtime = new FakeActorRuntime(
        [
            new FakeActor("root", null, new FakeAgent("a-root", "root")),
            new FakeActor("child-1", "root", new FakeAgent("a-1", "child-1")),
            new FakeActor("child-2", "child-1", new FakeAgent("a-2", "child-2")),
            new FakeActor("orphan", "unknown-parent", new FakeAgent("a-3", "orphan")),
        ]);

        var resolver = new ActorRuntimeWorkflowExecutionTopologyResolver();
        var topology = await resolver.ResolveAsync(runtime, "root", CancellationToken.None);

        topology.Should().HaveCount(2);
        topology.Should().Contain(new WorkflowExecutionTopologyEdge("root", "child-1"));
        topology.Should().Contain(new WorkflowExecutionTopologyEdge("child-1", "child-2"));
        topology.Should().NotContain(new WorkflowExecutionTopologyEdge("unknown-parent", "orphan"));
    }
}

internal sealed class FakeProjectionService : IWorkflowExecutionProjectionService
{
    public bool ProjectionEnabled { get; set; } = true;
    public bool EnableRunQueryEndpointsValue { get; set; } = true;
    public bool EnableRunReportArtifacts { get; set; }
    public IReadOnlyList<WorkflowExecutionReport> Runs { get; set; } = [];
    public Dictionary<string, WorkflowExecutionReport> ReportByRunId { get; set; } = new(StringComparer.Ordinal);

    public bool EnableRunQueryEndpoints => EnableRunQueryEndpointsValue;

    public Task<WorkflowExecutionProjectionSession> StartAsync(string rootActorId, string workflowName, string input, CancellationToken ct = default) =>
        Task.FromResult(new WorkflowExecutionProjectionSession
        {
            RunId = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.UtcNow,
            Context = null,
        });

    public Task<ProjectionRunCompletionStatus> WaitForRunProjectionCompletionStatusAsync(string runId, TimeSpan? timeoutOverride = null, CancellationToken ct = default) =>
        Task.FromResult(ProjectionRunCompletionStatus.Completed);

    public Task<WorkflowExecutionReport?> CompleteAsync(WorkflowExecutionProjectionSession session, IReadOnlyList<WorkflowExecutionTopologyEdge> topology, CancellationToken ct = default) =>
        Task.FromResult<WorkflowExecutionReport?>(null);

    public Task<IReadOnlyList<WorkflowExecutionReport>> ListRunsAsync(int take = 50, CancellationToken ct = default) =>
        Task.FromResult(Runs);

    public Task<WorkflowExecutionReport?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        ReportByRunId.TryGetValue(runId, out var report);
        return Task.FromResult(report);
    }
}

internal sealed class SpyRunOrchestrator : IWorkflowExecutionRunOrchestrator
{
    public bool StartCalled { get; private set; }

    public Task<WorkflowProjectionRun> StartAsync(string actorId, string workflowName, string prompt, Aevatar.Presentation.AGUI.IAGUIEventSink sink, CancellationToken ct = default)
    {
        StartCalled = true;
        throw new InvalidOperationException("StartAsync should not be called in this test.");
    }

    public Task<WorkflowProjectionFinalizeResult> FinalizeAsync(WorkflowProjectionRun projectionRun, IActorRuntime runtime, string actorId, CancellationToken ct = default) =>
        throw new InvalidOperationException("Not expected.");

    public Task RollbackAsync(WorkflowProjectionRun projectionRun, CancellationToken ct = default) =>
        Task.CompletedTask;
}

internal sealed class FakeEnvelopeFactory : IWorkflowChatRequestEnvelopeFactory
{
    public EventEnvelope Create(string prompt, string runId)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new Google.Protobuf.WellKnownTypes.Empty()),
            PublisherId = "test",
            Direction = EventDirection.Self,
        };
    }
}

internal sealed class NoopReportSink : IWorkflowExecutionReportArtifactSink
{
    public Task PersistAsync(WorkflowExecutionReport report, CancellationToken ct = default) =>
        Task.CompletedTask;
}

internal sealed class FakeActorRuntime : IActorRuntime
{
    private readonly IReadOnlyList<IActor> _actors;

    public FakeActorRuntime(IReadOnlyList<IActor> actors) => _actors = actors;

    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
        throw new InvalidOperationException("Not expected.");

    public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
        throw new InvalidOperationException("Not expected.");

    public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IActor?> GetAsync(string id) =>
        Task.FromResult(_actors.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal)));

    public Task<IReadOnlyList<IActor>> GetAllAsync() => Task.FromResult(_actors);

    public Task<bool> ExistsAsync(string id) =>
        Task.FromResult(_actors.Any(x => string.Equals(x.Id, id, StringComparison.Ordinal)));

    public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

    public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RestoreAllAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeActor : IActor
{
    private readonly string? _parentId;

    public FakeActor(string id, string? parentId, IAgent agent)
    {
        Id = id;
        _parentId = parentId;
        Agent = agent;
    }

    public string Id { get; }
    public IAgent Agent { get; }

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string?> GetParentIdAsync() => Task.FromResult(_parentId);

    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
}

internal sealed class FakeAgent : IAgent
{
    private readonly string _description;

    public FakeAgent(string id, string description)
    {
        Id = id;
        _description = description;
    }

    public string Id { get; }

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult(_description);

    public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<System.Type>>([]);

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}
