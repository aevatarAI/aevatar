using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Orchestration;
using Aevatar.Workflow.Application.Queries;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Application.Tests;

public class WorkflowChatRunApplicationServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenWorkflowMissing_ShouldReturnWorkflowNotFound()
    {
        var runtime = new FakeActorRuntime([]);
        var registry = new WorkflowDefinitionRegistry();
        var actorResolver = new WorkflowRunActorResolver(runtime, registry);
        var projectionPort = new FakeProjectionService();
        var service = new WorkflowChatRunApplicationService(
            actorResolver,
            projectionPort,
            new FakeEnvelopeFactory(),
            new FakeCommandContextPolicy(),
            new WorkflowRunRequestExecutor(NullLogger<WorkflowRunRequestExecutor>.Instance),
            new WorkflowRunOutputStreamer());

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", "missing", null),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowNotFound);
        result.Started.Should().BeNull();
        projectionPort.EnsureActorProjectionCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenStreamEndsWithRunFinished_ShouldFinalizeAsCompleted()
    {
        var actor = new FakeActor("actor-1", null, new FakeAgent("a-1", "actor-1"));
        var service = new WorkflowChatRunApplicationService(
            new StubWorkflowRunActorResolver(new WorkflowActorResolutionResult(actor, "direct", WorkflowChatRunStartError.None)),
            new FakeProjectionService(),
            new FakeEnvelopeFactory(),
            new FakeCommandContextPolicy(),
            new StubWorkflowRunRequestExecutor(),
            new StubWorkflowRunOutputStreamer(
            [
                new WorkflowOutputFrame { Type = "RUN_STARTED", ThreadId = "actor-1" },
                new WorkflowOutputFrame { Type = "RUN_FINISHED", ThreadId = "actor-1" },
            ]));

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", "direct", "actor-1"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.FinalizeResult.Should().NotBeNull();
        result.FinalizeResult!.ProjectionCompletionStatus.Should().Be(WorkflowProjectionCompletionStatus.Completed);
        result.FinalizeResult.ProjectionCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenStreamEndsWithRunError_ShouldFinalizeAsFailed()
    {
        var actor = new FakeActor("actor-1", null, new FakeAgent("a-1", "actor-1"));
        var service = new WorkflowChatRunApplicationService(
            new StubWorkflowRunActorResolver(new WorkflowActorResolutionResult(actor, "direct", WorkflowChatRunStartError.None)),
            new FakeProjectionService(),
            new FakeEnvelopeFactory(),
            new FakeCommandContextPolicy(),
            new StubWorkflowRunRequestExecutor(),
            new StubWorkflowRunOutputStreamer(
            [
                new WorkflowOutputFrame { Type = "RUN_STARTED", ThreadId = "actor-1" },
                new WorkflowOutputFrame { Type = "RUN_ERROR", Message = "boom" },
            ]));

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", "direct", "actor-1"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.FinalizeResult.Should().NotBeNull();
        result.FinalizeResult!.ProjectionCompletionStatus.Should().Be(WorkflowProjectionCompletionStatus.Failed);
        result.FinalizeResult.ProjectionCompleted.Should().BeTrue();
    }
}

public class WorkflowRunActorResolverTests
{
    [Fact]
    public async Task ResolveOrCreateAsync_WhenExistingActorWorkflowMismatches_ShouldReturnConflictError()
    {
        var workflowAgent = CreateWorkflowAgent("direct");
        var runtime = new FakeActorRuntime([new FakeActor("actor-1", null, workflowAgent)]);
        var registry = new WorkflowDefinitionRegistry();
        registry.Register("direct", WorkflowDefinitionRegistry.BuiltInDirectYaml);
        registry.Register("other", WorkflowDefinitionRegistry.BuiltInDirectYaml);
        var resolver = new WorkflowRunActorResolver(runtime, registry);

        var resolved = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", "other", "actor-1"),
            CancellationToken.None);

        resolved.Error.Should().Be(WorkflowChatRunStartError.WorkflowBindingMismatch);
        resolved.Actor.Should().BeNull();
        resolved.WorkflowNameForRun.Should().Be("direct");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_WhenExistingActorWorkflowMatches_ShouldUseBoundWorkflowName()
    {
        var workflowAgent = CreateWorkflowAgent("direct");
        var actor = new FakeActor("actor-1", null, workflowAgent);
        var runtime = new FakeActorRuntime([actor]);
        var registry = new WorkflowDefinitionRegistry();
        registry.Register("direct", WorkflowDefinitionRegistry.BuiltInDirectYaml);
        var resolver = new WorkflowRunActorResolver(runtime, registry);

        var resolved = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", "direct", "actor-1"),
            CancellationToken.None);

        resolved.Error.Should().Be(WorkflowChatRunStartError.None);
        resolved.Actor.Should().BeSameAs(actor);
        resolved.WorkflowNameForRun.Should().Be("direct");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_WhenExistingActorHasNoBoundWorkflow_ShouldReturnNotConfiguredError()
    {
        var unconfigured = CreateUnconfiguredWorkflowAgent();
        var runtime = new FakeActorRuntime([new FakeActor("actor-1", null, unconfigured)]);
        var registry = new WorkflowDefinitionRegistry();
        var resolver = new WorkflowRunActorResolver(runtime, registry);

        var resolved = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, "actor-1"),
            CancellationToken.None);

        resolved.Error.Should().Be(WorkflowChatRunStartError.AgentWorkflowNotConfigured);
        resolved.Actor.Should().BeNull();
    }

    [Fact]
    public void ConfigureWorkflow_WhenSwitchingWorkflow_ShouldThrowInvalidOperation()
    {
        var workflowAgent = CreateWorkflowAgent("direct");

        Action act = () => workflowAgent.ConfigureWorkflow(
            WorkflowDefinitionRegistry.BuiltInDirectYaml,
            "another");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already bound to workflow*");
    }

    private static WorkflowGAgent CreateWorkflowAgent(string workflowName)
    {
        var workflowAgent = CreateUnconfiguredWorkflowAgent();
        workflowAgent.ConfigureWorkflow(WorkflowDefinitionRegistry.BuiltInDirectYaml, workflowName);
        return workflowAgent;
    }

    private static WorkflowGAgent CreateUnconfiguredWorkflowAgent() =>
        new(
            new FakeActorRuntime([]),
            new StubRoleAgentTypeResolver(),
            [],
            [],
            []);
}

public class WorkflowExecutionQueryApplicationServiceTests
{
    [Fact]
    public async Task ListAgentsAsync_ShouldOnlyReturnWorkflowActors()
    {
        var projection = new FakeProjectionService
        {
            EnableActorQueryEndpointsValue = true,
            SnapshotList = [
                new WorkflowActorSnapshot
                {
                    ActorId = "wf-1",
                    WorkflowName = "direct",
                },
            ],
        };
        var registry = new WorkflowDefinitionRegistry();
        var queryService = new WorkflowExecutionQueryApplicationService(
            registry,
            projection);

        var agents = await queryService.ListAgentsAsync(CancellationToken.None);

        agents.Should().ContainSingle();
        agents[0].Id.Should().Be("wf-1");
        agents[0].Type.Should().Be(nameof(WorkflowGAgent));
    }

    [Fact]
    public async Task GetActorSnapshotAsync_ShouldReturnProjectionPortResult()
    {
        var snapshot = new WorkflowActorSnapshot
        {
            ActorId = "actor-1",
            WorkflowName = "direct",
            LastCommandId = "cmd-1",
            LastUpdatedAt = DateTimeOffset.UtcNow,
            LastSuccess = true,
            TotalSteps = 3,
        };

        var registry = new WorkflowDefinitionRegistry();
        registry.Register("direct", WorkflowDefinitionRegistry.BuiltInDirectYaml);
        var queryService = new WorkflowExecutionQueryApplicationService(
            registry,
            new FakeProjectionService
            {
                EnableActorQueryEndpointsValue = true,
                SnapshotByActorId = new Dictionary<string, WorkflowActorSnapshot>(StringComparer.Ordinal)
                {
                    ["actor-1"] = snapshot,
                },
            });

        var detail = await queryService.GetActorSnapshotAsync("actor-1", CancellationToken.None);
        detail.Should().NotBeNull();
        detail!.ActorId.Should().Be("actor-1");
        detail.LastCommandId.Should().Be("cmd-1");
        detail.TotalSteps.Should().Be(3);
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

        var resolver = new ActorRuntimeWorkflowExecutionTopologyResolver(runtime);
        var topology = await resolver.ResolveAsync("root", CancellationToken.None);

        topology.Should().HaveCount(2);
        topology.Should().Contain(new WorkflowTopologyEdge("root", "child-1"));
        topology.Should().Contain(new WorkflowTopologyEdge("child-1", "child-2"));
        topology.Should().NotContain(new WorkflowTopologyEdge("unknown-parent", "orphan"));
    }
}

internal sealed class FakeProjectionService : IWorkflowExecutionProjectionPort
{
    public bool ProjectionEnabled { get; set; } = true;
    public bool EnableActorQueryEndpointsValue { get; set; } = true;
    public bool EnsureActorProjectionCalled { get; private set; }
    public Dictionary<string, WorkflowActorSnapshot> SnapshotByActorId { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, IReadOnlyList<WorkflowActorTimelineItem>> TimelineByActorId { get; set; } = new(StringComparer.Ordinal);
    public IReadOnlyList<WorkflowActorSnapshot> SnapshotList { get; set; } = [];

    public bool EnableActorQueryEndpoints => EnableActorQueryEndpointsValue;

    public Task EnsureActorProjectionAsync(
        string rootActorId,
        string workflowName,
        string input,
        string commandId,
        CancellationToken ct = default)
    {
        EnsureActorProjectionCalled = true;
        return Task.CompletedTask;
    }

    public Task AttachLiveSinkAsync(
        string actorId,
        string commandId,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DetachLiveSinkAsync(
        string actorId,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default)
    {
        SnapshotByActorId.TryGetValue(actorId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>(SnapshotList.Take(Math.Max(1, take)).ToList());
    }

    public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!TimelineByActorId.TryGetValue(actorId, out var timeline))
            timeline = [];

        return Task.FromResult<IReadOnlyList<WorkflowActorTimelineItem>>(timeline.Take(Math.Max(1, take)).ToList());
    }
}

internal sealed class FakeEnvelopeFactory : ICommandEnvelopeFactory<WorkflowChatRunRequest>
{
    public EventEnvelope CreateEnvelope(WorkflowChatRunRequest command, CommandContext context)
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

internal sealed class FakeCommandContextPolicy : ICommandContextPolicy
{
    public CommandContext Create(
        string targetId,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? commandId = null,
        string? correlationId = null) =>
        new(
            targetId,
            commandId ?? Guid.NewGuid().ToString("N"),
            correlationId ?? Guid.NewGuid().ToString("N"),
            metadata == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(metadata, StringComparer.Ordinal));
}

internal sealed class FakeActorRuntime : IActorRuntime
{
    private readonly IReadOnlyList<IActor> _actors;

    public FakeActorRuntime(IReadOnlyList<IActor> actors)
    {
        _actors = actors;

        foreach (var child in _actors.OfType<FakeActor>())
        {
            if (string.IsNullOrWhiteSpace(child.ParentId))
                continue;

            var parent = _actors
                .OfType<FakeActor>()
                .FirstOrDefault(x => string.Equals(x.Id, child.ParentId, StringComparison.Ordinal));
            parent?.AddChild(child.Id);
        }
    }

    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
        throw new InvalidOperationException("Not expected.");

    public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
        throw new InvalidOperationException("Not expected.");

    public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IActor?> GetAsync(string id) =>
        Task.FromResult(_actors.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal)));

    public Task<bool> ExistsAsync(string id) =>
        Task.FromResult(_actors.Any(x => string.Equals(x.Id, id, StringComparison.Ordinal)));

    public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
    {
        var parent = _actors.OfType<FakeActor>()
            .FirstOrDefault(x => string.Equals(x.Id, parentId, StringComparison.Ordinal));
        var child = _actors.OfType<FakeActor>()
            .FirstOrDefault(x => string.Equals(x.Id, childId, StringComparison.Ordinal));
        if (parent != null && child != null)
        {
            parent.AddChild(childId);
            child.SetParent(parentId);
        }

        return Task.CompletedTask;
    }

    public Task UnlinkAsync(string childId, CancellationToken ct = default)
    {
        var child = _actors.OfType<FakeActor>()
            .FirstOrDefault(x => string.Equals(x.Id, childId, StringComparison.Ordinal));
        if (child == null)
            return Task.CompletedTask;

        var parentId = child.ParentId;
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            var parent = _actors.OfType<FakeActor>()
                .FirstOrDefault(x => string.Equals(x.Id, parentId, StringComparison.Ordinal));
            parent?.RemoveChild(childId);
        }

        child.SetParent(null);
        return Task.CompletedTask;
    }

    public Task RestoreAllAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class StubWorkflowRunActorResolver : IWorkflowRunActorResolver
{
    private readonly WorkflowActorResolutionResult _result;

    public StubWorkflowRunActorResolver(WorkflowActorResolutionResult result)
    {
        _result = result;
    }

    public Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct = default)
    {
        _ = request;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_result);
    }
}

internal sealed class StubWorkflowRunRequestExecutor : IWorkflowRunRequestExecutor
{
    public Task ExecuteAsync(
        IActor actor,
        string actorId,
        EventEnvelope requestEnvelope,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default)
    {
        _ = actor;
        _ = actorId;
        _ = requestEnvelope;
        _ = sink;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal sealed class StubWorkflowRunOutputStreamer : IWorkflowRunOutputStreamer
{
    private readonly IReadOnlyList<WorkflowOutputFrame> _frames;

    public StubWorkflowRunOutputStreamer(IReadOnlyList<WorkflowOutputFrame> frames)
    {
        _frames = frames;
    }

    public async Task StreamAsync(
        IWorkflowRunEventSink sink,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        _ = sink;
        foreach (var frame in _frames)
            await emitAsync(frame, ct);
    }
}

internal sealed class StubRoleAgentTypeResolver : IRoleAgentTypeResolver
{
    public Type ResolveRoleAgentType() => typeof(FakeAgent);
}

internal sealed class FakeActor : IActor
{
    private string? _parentId;
    private readonly List<string> _children = [];

    public FakeActor(string id, string? parentId, IAgent agent)
    {
        Id = id;
        _parentId = parentId;
        Agent = agent;
    }

    public string Id { get; }
    public IAgent Agent { get; }
    internal string? ParentId => _parentId;

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string?> GetParentIdAsync() => Task.FromResult(_parentId);

    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(_children.ToList());

    internal void AddChild(string childId)
    {
        if (!_children.Contains(childId, StringComparer.Ordinal))
            _children.Add(childId);
    }

    internal void RemoveChild(string childId)
    {
        _children.RemoveAll(x => string.Equals(x, childId, StringComparison.Ordinal));
    }

    internal void SetParent(string? parentId)
    {
        _parentId = parentId;
    }
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
