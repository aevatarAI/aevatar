using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunOrchestrationComponentTests
{
    [Fact]
    public async Task WorkflowRunCommandTargetResolver_ShouldFail_WhenProjectionIsDisabled()
    {
        var actorResolver = new FakeWorkflowRunActorResolver(
            new WorkflowActorResolutionResult(new FakeActor("actor-1"), "auto", WorkflowChatRunStartError.None));
        var resolver = new WorkflowRunCommandTargetResolver(
            actorResolver,
            new FakeProjectionPort { ProjectionEnabled = false },
            new FakeWorkflowRunActorPort());

        var result = await resolver.ResolveAsync(new WorkflowChatRunRequest("hello", "auto", null));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionDisabled);
        actorResolver.ResolveCallCount.Should().Be(0);
    }

    [Fact]
    public async Task WorkflowRunCommandTargetResolver_ShouldReturnTarget_WhenActorResolutionSucceeds()
    {
        var actor = new FakeActor("actor-1");
        var resolver = new WorkflowRunCommandTargetResolver(
            new FakeWorkflowRunActorResolver(
                new WorkflowActorResolutionResult(actor, "auto", WorkflowChatRunStartError.None, ["definition-1", "actor-1"])),
            new FakeProjectionPort(),
            new FakeWorkflowRunActorPort());

        var result = await resolver.ResolveAsync(new WorkflowChatRunRequest("hello", "auto", null));

        result.Succeeded.Should().BeTrue();
        result.Target.Should().NotBeNull();
        result.Target!.ActorId.Should().Be("actor-1");
        result.Target.WorkflowName.Should().Be("auto");
        result.Target.CreatedActorIds.Should().Equal("definition-1", "actor-1");
    }

    [Fact]
    public async Task WorkflowRunCommandTargetBinder_ShouldAttachLeaseAndSink_OnSuccess()
    {
        var projectionPort = new FakeProjectionPort
        {
            EnsureLease = new FakeProjectionLease("actor-1", "cmd-1"),
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var binder = new WorkflowRunCommandTargetBinder(projectionPort);
        var target = new WorkflowRunCommandTarget(new FakeActor("actor-1"), "direct", [], projectionPort, actorPort);
        var context = new Aevatar.CQRS.Core.Abstractions.Commands.CommandContext(
            "actor-1",
            "cmd-1",
            "corr-1",
            new Dictionary<string, string>());

        var result = await binder.BindAsync(
            new WorkflowChatRunRequest("hello", "direct", null),
            target,
            context,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        target.ProjectionLease.Should().BeSameAs(projectionPort.EnsureLease);
        target.LiveSink.Should().NotBeNull();
        projectionPort.AttachCalls.Should().ContainSingle();
        actorPort.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunCommandTargetBinder_ShouldRollbackCreatedActors_WhenProjectionLeaseIsUnavailable()
    {
        var projectionPort = new FakeProjectionPort
        {
            EnsureLease = null,
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var binder = new WorkflowRunCommandTargetBinder(projectionPort);
        var target = new WorkflowRunCommandTarget(
            new FakeActor("actor-1"),
            "direct",
            ["definition-1", "actor-1", "definition-1"],
            projectionPort,
            actorPort);
        var context = new Aevatar.CQRS.Core.Abstractions.Commands.CommandContext(
            "actor-1",
            "cmd-1",
            "corr-1",
            new Dictionary<string, string>());

        var result = await binder.BindAsync(
            new WorkflowChatRunRequest("hello", "direct", null),
            target,
            context,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionDisabled);
        actorPort.DestroyCalls.Should().Equal("actor-1", "definition-1");
    }

    [Fact]
    public async Task WorkflowRunCommandTargetBinder_ShouldRollbackCreatedActors_WhenAttachFails()
    {
        var projectionPort = new FakeProjectionPort
        {
            EnsureLease = new FakeProjectionLease("actor-1", "cmd-1"),
            AttachException = new InvalidOperationException("attach failed"),
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var binder = new WorkflowRunCommandTargetBinder(projectionPort);
        var target = new WorkflowRunCommandTarget(
            new FakeActor("actor-1"),
            "direct",
            ["definition-1", "actor-1"],
            projectionPort,
            actorPort);
        var context = new Aevatar.CQRS.Core.Abstractions.Commands.CommandContext(
            "actor-1",
            "cmd-1",
            "corr-1",
            new Dictionary<string, string>());

        var act = () => binder.BindAsync(
            new WorkflowChatRunRequest("hello", "direct", null),
            target,
            context,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("attach failed");
        actorPort.DestroyCalls.Should().Equal("actor-1", "definition-1");
    }

    private sealed class FakeWorkflowRunActorResolver : IWorkflowRunActorResolver
    {
        private readonly WorkflowActorResolutionResult _result;
        public int ResolveCallCount { get; private set; }

        public FakeWorkflowRunActorResolver(WorkflowActorResolutionResult result)
        {
            _result = result;
        }

        public Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
            WorkflowChatRunRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            ResolveCallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeProjectionPort : IWorkflowExecutionProjectionLifecyclePort
    {
        public bool ProjectionEnabled { get; set; } = true;
        public FakeProjectionLease? EnsureLease { get; set; }
        public Exception? AttachException { get; set; }
        public List<(IWorkflowExecutionProjectionLease Lease, IEventSink<WorkflowRunEventEnvelope> Sink)> AttachCalls { get; } = [];

        public Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(
            string rootActorId,
            string workflowName,
            string input,
            string commandId,
            CancellationToken ct = default)
        {
            _ = rootActorId;
            _ = workflowName;
            _ = input;
            _ = commandId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IWorkflowExecutionProjectionLease?>(EnsureLease);
        }

        public Task AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (AttachException != null)
                throw AttachException;

            AttachCalls.Add((lease, sink));
            return Task.CompletedTask;
        }

        public Task DetachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReleaseActorProjectionAsync(
            IWorkflowExecutionProjectionLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeWorkflowRunActorPort : IWorkflowRunActorPort
    {
        public List<string> DestroyCalls { get; } = [];

        public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DestroyCalls.Add(actorId);
            return Task.CompletedTask;
        }

        public Task BindWorkflowDefinitionAsync(
            IActor actor,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProjectionLease : IWorkflowExecutionProjectionLease
    {
        public FakeProjectionLease(string actorId, string commandId)
        {
            ActorId = actorId;
            CommandId = commandId;
        }

        public string ActorId { get; }
        public string CommandId { get; }
    }

    private sealed class FakeActor : IActor
    {
        public FakeActor(string id)
        {
            Id = id;
            Agent = new FakeAgent(id + "-agent");
        }

        public string Id { get; }
        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent : IAgent
    {
        public FakeAgent(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("fake");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
