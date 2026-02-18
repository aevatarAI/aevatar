using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Orchestration;
using Aevatar.Workflow.Application.Queries;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Workflows;
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
}

public class WorkflowExecutionQueryApplicationServiceTests
{
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

        var runtime = new FakeActorRuntime([]);
        var registry = new WorkflowDefinitionRegistry();
        registry.Register("direct", WorkflowDefinitionRegistry.BuiltInDirectYaml);
        var queryService = new WorkflowExecutionQueryApplicationService(
            runtime,
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

        var resolver = new ActorRuntimeWorkflowExecutionTopologyResolver();
        var topology = await resolver.ResolveAsync(runtime, "root", CancellationToken.None);

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

    public bool TryResolve(EventEnvelope envelope, out CommandContext context)
    {
        context = default!;
        return false;
    }
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
