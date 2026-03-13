using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Reporting;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunCommandTargetAndPolicyTests
{
    [Fact]
    public void RequireLiveSink_ShouldThrow_WhenLiveObservationNotBound()
    {
        var target = CreateTarget();

        var act = () => target.RequireLiveSink();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*live sink is not bound*");
    }

    [Fact]
    public async Task ReleaseAsync_ShouldDetachReleaseDisposeAndDestroyCreatedActors()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateTarget(
            projectionPort: projectionPort,
            actorPort: actorPort,
            createdActorIds: ["definition-1", "run-1"]);
        target.BindLiveObservation(new FakeProjectionLease("run-1", "cmd-1"), new FakeEventSink());
        var detached = false;

        await target.ReleaseAsync(
            onDetachedAsync: () =>
            {
                detached = true;
                return Task.CompletedTask;
            },
            destroyCreatedActors: true,
            ct: CancellationToken.None);

        detached.Should().BeTrue();
        projectionPort.Events.Should().Equal("detach:run-1", "release:run-1");
        actorPort.DestroyCalls.Should().Equal("run-1", "definition-1");
        target.ProjectionLease.Should().BeNull();
        target.LiveSink.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseAfterInteractionAsync_WhenNonTerminal_ShouldScheduleDetachedCleanupAndTransferOwnership()
    {
        var projectionPort = new FakeProjectionPort();
        var cleanupScheduler = new FakeDetachedCleanupScheduler();
        var target = CreateTarget(
            projectionPort: projectionPort,
            cleanupScheduler: cleanupScheduler,
            createdActorIds: ["definition-1", "run-1"]);
        var lease = new FakeProjectionLease("run-1", "cmd-1");
        target.BindLiveObservation(lease, new FakeEventSink());

        await target.ReleaseAfterInteractionAsync(
            new WorkflowChatRunAcceptedReceipt("run-1", "direct", "cmd-1", "corr-1"),
            new Aevatar.CQRS.Core.Abstractions.Interactions.CommandInteractionCleanupContext<WorkflowProjectionCompletionStatus>(
                false,
                WorkflowProjectionCompletionStatus.Unknown,
                Aevatar.CQRS.Core.Abstractions.Interactions.CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete),
            CancellationToken.None);

        projectionPort.Events.Should().Equal("detach:run-1");
        cleanupScheduler.Requests.Should().ContainSingle();
        cleanupScheduler.Requests.Single().ActorId.Should().Be("run-1");
        lease.OwnershipHeartbeatStopCalls.Should().Be(1);
        target.ProjectionLease.Should().BeNull();
        target.LiveSink.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseAsync_ShouldDisposeSinkAndReleaseLease_WhenOnlyOneSideBound()
    {
        var projectionPort = new FakeProjectionPort();
        var target = CreateTarget(projectionPort);
        var lease = new FakeProjectionLease("run-1", "cmd-1");
        var sink = new FakeEventSink();
        target.BindLiveObservation(lease, sink);
        target.BindLiveObservation(lease, sink);

        await target.ReleaseAsync(destroyCreatedActors: false, ct: CancellationToken.None);

        projectionPort.Events.Should().Equal("detach:run-1", "release:run-1");
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DetachLiveObservationAsync_ShouldDetachAndDisposeSink_WithoutReleasingLease()
    {
        var projectionPort = new FakeProjectionPort();
        var target = CreateTarget(projectionPort);
        var lease = new FakeProjectionLease("run-1", "cmd-1");
        var sink = new FakeEventSink();
        target.BindLiveObservation(lease, sink);

        await target.DetachLiveObservationAsync(CancellationToken.None);

        projectionPort.Events.Should().Equal("detach:run-1");
        sink.DisposeCalls.Should().Be(1);
        target.LiveSink.Should().BeNull();
        target.ProjectionLease.Should().BeSameAs(lease);
    }

    [Fact]
    public async Task CleanupAfterDispatchFailureAsync_ShouldAggregateCleanupFailures()
    {
        var projectionPort = new FakeProjectionPort
        {
            DetachException = new InvalidOperationException("detach failed"),
        };
        var actorPort = new FakeWorkflowRunActorPort
        {
            DestroyException = new InvalidOperationException("destroy failed"),
        };
        var target = CreateTarget(
            projectionPort: projectionPort,
            actorPort: actorPort,
            createdActorIds: ["definition-1"]);
        target.BindLiveObservation(new FakeProjectionLease("run-1", "cmd-1"), new FakeEventSink());

        var act = async () => await target.CleanupAfterDispatchFailureAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("detach failed");
        actorPort.DestroyCalls.Should().Equal("definition-1");
    }

    [Fact]
    public async Task RollbackCreatedActorsAsync_ShouldSkipWhitespaceAndDuplicates()
    {
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateTarget(actorPort: actorPort, createdActorIds: [" ", "run-1", "run-1", "definition-1"]);

        await target.RollbackCreatedActorsAsync(CancellationToken.None);
        await target.RollbackCreatedActorsAsync(CancellationToken.None);

        actorPort.DestroyCalls.Should().Equal("definition-1", "run-1");
    }

    [Theory]
    [InlineData(WorkflowRunEventEnvelope.EventOneofCase.RunFinished, true, WorkflowProjectionCompletionStatus.Completed)]
    [InlineData(WorkflowRunEventEnvelope.EventOneofCase.RunError, true, WorkflowProjectionCompletionStatus.Failed)]
    [InlineData(WorkflowRunEventEnvelope.EventOneofCase.None, false, WorkflowProjectionCompletionStatus.Unknown)]
    public void WorkflowRunCompletionPolicy_ShouldResolveTerminalStatus(
        WorkflowRunEventEnvelope.EventOneofCase eventCase,
        bool expectedResolved,
        WorkflowProjectionCompletionStatus expectedStatus)
    {
        var policy = new WorkflowRunCompletionPolicy();
        var evt = eventCase switch
        {
            WorkflowRunEventEnvelope.EventOneofCase.RunFinished => new WorkflowRunEventEnvelope
            {
                RunFinished = new WorkflowRunFinishedEventPayload(),
            },
            WorkflowRunEventEnvelope.EventOneofCase.RunError => new WorkflowRunEventEnvelope
            {
                RunError = new WorkflowRunErrorEventPayload(),
            },
            _ => new WorkflowRunEventEnvelope(),
        };

        var resolved = policy.TryResolve(evt, out var status);

        resolved.Should().Be(expectedResolved);
        status.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task NoopWorkflowExecutionReportArtifactSink_ShouldHonorCancellation()
    {
        var sink = new NoopWorkflowExecutionReportArtifactSink();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sink.PersistAsync(new WorkflowRunReport(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task NoopWorkflowExecutionReportArtifactSink_ShouldCompleteWithoutSideEffects()
    {
        IWorkflowExecutionReportArtifactSink sink = new NoopWorkflowExecutionReportArtifactSink();

        await sink.PersistAsync(new WorkflowRunReport(), CancellationToken.None);
    }

    private static WorkflowRunCommandTarget CreateTarget(
        FakeProjectionPort? projectionPort = null,
        FakeWorkflowRunActorPort? actorPort = null,
        FakeDetachedCleanupScheduler? cleanupScheduler = null,
        IReadOnlyList<string>? createdActorIds = null) =>
        new(
            new FakeActor("run-1"),
            "direct",
            createdActorIds ?? [],
            projectionPort ?? new FakeProjectionPort(),
            actorPort ?? new FakeWorkflowRunActorPort(),
            cleanupScheduler ?? new FakeDetachedCleanupScheduler());

    private sealed class FakeProjectionPort : IWorkflowExecutionProjectionPort
    {
        public bool ProjectionEnabled => true;
        public Exception? DetachException { get; set; }
        public Exception? ReleaseException { get; set; }
        public List<string> Events { get; } = [];

        public Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(string rootActorId, string workflowName, string input, string commandId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task AttachLiveSinkAsync(IWorkflowExecutionProjectionLease lease, IEventSink<WorkflowRunEventEnvelope> sink, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DetachLiveSinkAsync(IWorkflowExecutionProjectionLease lease, IEventSink<WorkflowRunEventEnvelope> sink, CancellationToken ct = default)
        {
            Events.Add($"detach:{lease.ActorId}");
            if (DetachException != null)
                throw DetachException;
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(IWorkflowExecutionProjectionLease lease, CancellationToken ct = default)
        {
            Events.Add($"release:{lease.ActorId}");
            if (ReleaseException != null)
                throw ReleaseException;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProjectionLease(string actorId, string commandId) : IWorkflowExecutionProjectionOwnershipLease
    {
        public string ActorId { get; } = actorId;
        public string CommandId { get; } = commandId;
        public int OwnershipHeartbeatStopCalls { get; private set; }

        public ValueTask StopOwnershipHeartbeatAsync()
        {
            OwnershipHeartbeatStopCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDetachedCleanupScheduler : IWorkflowRunDetachedCleanupScheduler
    {
        public List<WorkflowRunDetachedCleanupRequest> Requests { get; } = [];

        public Task ScheduleAsync(WorkflowRunDetachedCleanupRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEventSink : IEventSink<WorkflowRunEventEnvelope>
    {
        public int DisposeCalls { get; private set; }

        public void Push(WorkflowRunEventEnvelope evt) => throw new NotSupportedException();

        public ValueTask PushAsync(WorkflowRunEventEnvelope evt, CancellationToken ct = default) => throw new NotSupportedException();

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEventEnvelope> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWorkflowRunActorPort : IWorkflowRunActorPort
    {
        public Exception? DestroyException { get; set; }
        public List<string> DestroyCalls { get; } = [];

        public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default)
        {
            DestroyCalls.Add(actorId);
            if (DestroyException != null)
                throw DestroyException;
            return Task.CompletedTask;
        }

        public Task BindWorkflowDefinitionAsync(IActor actor, string workflowYaml, string workflowName, IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new FakeAgent(id + "-agent");

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("fake");

        public Task<IReadOnlyList<global::System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<global::System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
