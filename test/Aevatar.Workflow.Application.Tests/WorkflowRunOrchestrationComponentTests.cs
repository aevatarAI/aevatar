using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using System.Runtime.CompilerServices;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunOrchestrationComponentTests
{
    [Fact]
    public async Task ContextFactory_ShouldCreateContextAndInjectSessionMetadata()
    {
        var actor = new ComponentActor("actor-1");
        var actorPort = new RecordingActorPort();
        var projectionPort = new CapturingProjectionPort();
        var factory = new WorkflowRunContextFactory(
            new ComponentRunActorResolver(actor, "direct"),
            actorPort,
            projectionPort,
            new DeterministicCommandContextPolicy(
                commandId: "cmd-1",
                correlationId: "corr-1",
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source"] = "tests",
                }));

        var result = await factory.CreateAsync(
            new WorkflowChatRunRequest("hello", "direct", "actor-1"),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.Context.Should().NotBeNull();
        result.Context!.ActorId.Should().Be("actor-1");
        result.Context.CommandId.Should().Be("cmd-1");
        result.Context.CommandContext.CorrelationId.Should().Be("corr-1");
        result.Context.CommandContext.Metadata[WorkflowRunCommandMetadataKeys.SessionId].Should().Be("corr-1");
        result.Context.CommandContext.Metadata["source"].Should().Be("tests");
        projectionPort.EnsureCalls.Should().ContainSingle()
            .Which.Should().Be(("actor-1", "direct", "hello", "cmd-1"));
        actorPort.DestroyedActorIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ContextFactory_WhenProjectionDisabled_ShouldReturnProjectionDisabled()
    {
        var actor = new ComponentActor("actor-2");
        var actorPort = new RecordingActorPort();
        var projectionPort = new CapturingProjectionPort { ProjectionEnabled = false };
        var factory = new WorkflowRunContextFactory(
            new ComponentRunActorResolver(actor, "direct", ["definition-2", "actor-2"]),
            actorPort,
            projectionPort,
            new DeterministicCommandContextPolicy("cmd-2", "corr-2"));

        var result = await factory.CreateAsync(
            new WorkflowChatRunRequest("hello", "direct", "actor-2"),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionDisabled);
        result.Context.Should().BeNull();
        actorPort.DestroyedActorIds.Should().Equal("actor-2", "definition-2");
    }

    [Fact]
    public async Task ContextFactory_WhenAttachThrows_ShouldDisposeCreatedSink()
    {
        var actor = new ComponentActor("actor-3");
        var actorPort = new RecordingActorPort();
        var projectionPort = new CapturingProjectionPort { ThrowOnAttach = true };
        var factory = new WorkflowRunContextFactory(
            new ComponentRunActorResolver(actor, "direct", ["definition-3", "actor-3"]),
            actorPort,
            projectionPort,
            new DeterministicCommandContextPolicy("cmd-3", "corr-3"));

        var act = async () => await factory.CreateAsync(
            new WorkflowChatRunRequest("hello", "direct", "actor-3"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("attach failed");

        projectionPort.LastAttachedSink.Should().NotBeNull();
        var push = () => projectionPort.LastAttachedSink!.Push(
            new WorkflowRunStartedEvent { ThreadId = "actor-3" });
        push.Should().Throw<EventSinkCompletedException>();
        actorPort.DestroyedActorIds.Should().Equal("actor-3", "definition-3");
    }

    [Fact]
    public async Task ContextFactory_WhenProjectionDisabled_ShouldRollbackDistinctActorsInReverseOrder()
    {
        var actor = new ComponentActor("actor-rollback");
        var actorPort = new RecordingActorPort();
        var projectionPort = new CapturingProjectionPort { ProjectionEnabled = false };
        var factory = new WorkflowRunContextFactory(
            new ComponentRunActorResolver(actor, "direct", [" ", "definition-rollback", "actor-rollback", "definition-rollback", "actor-rollback"]),
            actorPort,
            projectionPort,
            new DeterministicCommandContextPolicy("cmd-r1", "corr-r1"));

        var result = await factory.CreateAsync(
            new WorkflowChatRunRequest("hello", "direct", "actor-rollback"),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.ProjectionDisabled);
        result.Context.Should().BeNull();
        actorPort.DestroyedActorIds.Should().Equal("actor-rollback", "definition-rollback");
    }

    [Fact]
    public async Task ContextFactory_WhenProjectionDisabledRollbackFails_ShouldThrowAggregateRollbackError()
    {
        var actor = new ComponentActor("actor-rf");
        var actorPort = new RecordingActorPort();
        actorPort.FailDestroyIds.UnionWith(["actor-rf", "definition-rf"]);
        var projectionPort = new CapturingProjectionPort { ProjectionEnabled = false };
        var factory = new WorkflowRunContextFactory(
            new ComponentRunActorResolver(actor, "direct", ["definition-rf", "actor-rf"]),
            actorPort,
            projectionPort,
            new DeterministicCommandContextPolicy("cmd-rf", "corr-rf"));

        var act = async () => await factory.CreateAsync(
            new WorkflowChatRunRequest("hello", "direct", "actor-rf"),
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<AggregateException>();
        error.Which.Message.Should().Contain("Workflow actor rollback failed");
        error.Which.InnerExceptions.Should().HaveCount(2);
        actorPort.DestroyedActorIds.Should().Equal("actor-rf", "definition-rf");
    }

    [Fact]
    public async Task ContextFactory_WhenAttachThrowsAndRollbackFails_ShouldThrowCompositeAggregate()
    {
        var actor = new ComponentActor("actor-attach");
        var actorPort = new RecordingActorPort();
        actorPort.FailDestroyIds.Add("actor-attach");
        var projectionPort = new CapturingProjectionPort { ThrowOnAttach = true };
        var factory = new WorkflowRunContextFactory(
            new ComponentRunActorResolver(actor, "direct", ["definition-attach", "actor-attach"]),
            actorPort,
            projectionPort,
            new DeterministicCommandContextPolicy("cmd-attach", "corr-attach"));

        var act = async () => await factory.CreateAsync(
            new WorkflowChatRunRequest("hello", "direct", "actor-attach"),
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<AggregateException>();
        error.Which.Message.Should().Contain("rollback also failed");
        error.Which.InnerExceptions.Should().HaveCount(2);
        error.Which.InnerExceptions[0].Message.Should().Contain("attach failed");
        error.Which.InnerExceptions[1].Message.Should().Contain("Failed to rollback workflow actor 'actor-attach'");
    }

    [Fact]
    public async Task ExecutionEngine_WhenNoTerminalFrame_ShouldFinalizeAsFailed()
    {
        var requestExecutor = new CapturingRequestExecutor();
        var outputStreamer = new SequenceOutputStreamer(
        [
            new WorkflowOutputFrame { Type = WorkflowRunEventTypes.RunStarted, ThreadId = "actor-1" },
            new WorkflowOutputFrame { Type = WorkflowRunEventTypes.StepStarted, StepName = "s1" },
        ]);
        var finalizer = new SpyResourceFinalizer();
        var snapshotEmitter = new WorkflowRunStateSnapshotEmitter(
            new NoopProjectionQueryPort(),
            outputStreamer);
        var engine = new WorkflowRunExecutionEngine(
            new ComponentEnvelopeFactory(),
            requestExecutor,
            outputStreamer,
            new WorkflowRunCompletionPolicy(),
            finalizer,
            snapshotEmitter);
        var runContext = BuildRunContext("actor-1", "cmd-11");
        var emitted = new List<WorkflowOutputFrame>();

        var result = await engine.ExecuteAsync(
            runContext,
            new WorkflowChatRunRequest("hello", "direct", "actor-1"),
            (frame, _) =>
            {
                emitted.Add(frame);
                return ValueTask.CompletedTask;
            },
            ct: CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.FinalizeResult.Should().NotBeNull();
        result.FinalizeResult!.ProjectionCompleted.Should().BeFalse();
        result.FinalizeResult.ProjectionCompletionStatus.Should().Be(WorkflowProjectionCompletionStatus.Failed);
        emitted.Should().HaveCount(3);
        emitted[^1].Type.Should().Be(WorkflowRunEventTypes.StateSnapshot);
        emitted[^1].Snapshot.Should().NotBeNull();
        requestExecutor.Calls.Should().ContainSingle();
        finalizer.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecutionEngine_WhenStreamerThrows_ShouldStillFinalizeResources()
    {
        var finalizer = new SpyResourceFinalizer();
        var outputStreamer = new ThrowingOutputStreamer(new InvalidOperationException("stream failed"));
        var snapshotEmitter = new WorkflowRunStateSnapshotEmitter(
            new NoopProjectionQueryPort(),
            outputStreamer);
        var engine = new WorkflowRunExecutionEngine(
            new ComponentEnvelopeFactory(),
            new CapturingRequestExecutor(),
            outputStreamer,
            new WorkflowRunCompletionPolicy(),
            finalizer,
            snapshotEmitter);
        var runContext = BuildRunContext("actor-4", "cmd-4");

        var act = async () => await engine.ExecuteAsync(
            runContext,
            new WorkflowChatRunRequest("hello", "direct", "actor-4"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("stream failed");
        finalizer.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecutionEngine_WhenSnapshotAvailable_ShouldEmitStateSnapshotFrame()
    {
        var queryPort = new NoopProjectionQueryPort
        {
            SnapshotByActorId = new Dictionary<string, WorkflowActorSnapshot>(StringComparer.Ordinal)
            {
                ["actor-7"] = new WorkflowActorSnapshot
                {
                    ActorId = "actor-7",
                    WorkflowName = "direct",
                    LastCommandId = "cmd-7",
                },
            },
        };
        var outputStreamer = new SequenceOutputStreamer(
        [
            new WorkflowOutputFrame { Type = WorkflowRunEventTypes.RunStarted, ThreadId = "actor-7" },
            new WorkflowOutputFrame { Type = WorkflowRunEventTypes.RunFinished, ThreadId = "actor-7" },
        ]);
        var snapshotEmitter = new WorkflowRunStateSnapshotEmitter(queryPort, outputStreamer);
        var engine = new WorkflowRunExecutionEngine(
            new ComponentEnvelopeFactory(),
            new CapturingRequestExecutor(),
            outputStreamer,
            new WorkflowRunCompletionPolicy(),
            new SpyResourceFinalizer(),
            snapshotEmitter);
        var emitted = new List<WorkflowOutputFrame>();

        _ = await engine.ExecuteAsync(
            BuildRunContext("actor-7", "cmd-7"),
            new WorkflowChatRunRequest("hello", "direct", "actor-7"),
            (frame, _) =>
            {
                emitted.Add(frame);
                return ValueTask.CompletedTask;
            },
            ct: CancellationToken.None);

        emitted.Should().Contain(f => f.Type == WorkflowRunEventTypes.StateSnapshot);
        var snapshotFrame = emitted.First(f => f.Type == WorkflowRunEventTypes.StateSnapshot);
        var payload = snapshotFrame.Snapshot.Should().BeOfType<WorkflowStateSnapshotPayload>().Subject;
        payload.ActorId.Should().Be("actor-7");
        payload.CommandId.Should().Be("cmd-7");
        payload.ProjectionCompletionStatus.Should().Be(WorkflowProjectionCompletionStatus.Completed.ToString());
        payload.ProjectionCompleted.Should().BeTrue();
        payload.SnapshotAvailable.Should().BeTrue();
        payload.Snapshot.Should().BeOfType<WorkflowActorSnapshot>();
    }

    [Fact]
    public async Task ResourceFinalizer_ShouldDetachThenReleaseAndAlwaysDisposeSink()
    {
        var projectionPort = new CapturingProjectionPort();
        var finalizer = new WorkflowRunResourceFinalizer(projectionPort);
        var sink = new TrackingSink();
        var runContext = BuildRunContext("actor-5", "cmd-5", sink);

        await finalizer.FinalizeAsync(
            runContext,
            Task.CompletedTask,
            CancellationToken.None);

        projectionPort.DetachCalls.Should().ContainSingle();
        projectionPort.ReleaseCalls.Should().ContainSingle();
        sink.Completed.Should().BeTrue();
        sink.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task ResourceFinalizer_WhenDetachThrows_ShouldStillCompleteAndDisposeSink()
    {
        var projectionPort = new CapturingProjectionPort { ThrowOnDetach = true };
        var finalizer = new WorkflowRunResourceFinalizer(projectionPort);
        var sink = new TrackingSink();
        var runContext = BuildRunContext("actor-6", "cmd-6", sink);

        var act = async () => await finalizer.FinalizeAsync(
            runContext,
            Task.CompletedTask,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("detach failed");
        sink.Completed.Should().BeTrue();
        sink.Disposed.Should().BeTrue();
    }

    [Theory]
    [InlineData(WorkflowRunEventTypes.RunFinished, true, WorkflowProjectionCompletionStatus.Completed)]
    [InlineData(WorkflowRunEventTypes.RunError, true, WorkflowProjectionCompletionStatus.Failed)]
    [InlineData(WorkflowRunEventTypes.StepStarted, false, WorkflowProjectionCompletionStatus.Unknown)]
    public void CompletionPolicy_ShouldResolveExpectedTerminalTypes(
        string frameType,
        bool expectedResolved,
        WorkflowProjectionCompletionStatus expectedStatus)
    {
        var policy = new WorkflowRunCompletionPolicy();

        var resolved = policy.TryResolve(
            new WorkflowOutputFrame { Type = frameType },
            out var status);

        resolved.Should().Be(expectedResolved);
        status.Should().Be(expectedStatus);
    }

    private static WorkflowRunContext BuildRunContext(
        string actorId,
        string commandId,
        IEventSink<WorkflowRunEvent>? sink = null)
    {
        var resolvedSink = sink ?? new EventChannel<WorkflowRunEvent>();
        return new WorkflowRunContext
        {
            Actor = new ComponentActor(actorId),
            WorkflowName = "direct",
            Sink = resolvedSink,
            CommandId = commandId,
            CommandContext = new CommandContext(
                actorId,
                commandId,
                CorrelationId: $"corr-{commandId}",
                new Dictionary<string, string>(StringComparer.Ordinal)),
            ProjectionLease = new ProjectionLease(actorId, commandId),
        };
    }

    private sealed class ComponentRunActorResolver : IWorkflowRunActorResolver
    {
        private readonly IActor _actor;
        private readonly string _workflowName;
        private readonly IReadOnlyList<string> _createdActorIds;

        public ComponentRunActorResolver(
            IActor actor,
            string workflowName,
            IReadOnlyList<string>? createdActorIds = null)
        {
            _actor = actor;
            _workflowName = workflowName;
            _createdActorIds = createdActorIds ?? Array.Empty<string>();
        }

        public Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
            WorkflowChatRunRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new WorkflowActorResolutionResult(
                _actor,
                _workflowName,
                WorkflowChatRunStartError.None,
                _createdActorIds));
        }
    }

    private sealed class RecordingActorPort : IWorkflowRunActorPort
    {
        public List<string> DestroyedActorIds { get; } = [];
        public HashSet<string> FailDestroyIds { get; } = new(StringComparer.Ordinal);

        public Task<IActor?> GetAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult<IActor?>(null);

        public Task<WorkflowActorBinding> DescribeAsync(IActor actor, CancellationToken ct = default) =>
            Task.FromResult(WorkflowActorBinding.Unsupported(actor.Id));

        public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default)
        {
            DestroyedActorIds.Add(actorId);
            if (FailDestroyIds.Contains(actorId))
                throw new InvalidOperationException($"destroy failed: {actorId}");
            return Task.CompletedTask;
        }

        public Task<bool> IsWorkflowDefinitionActorAsync(IActor actor, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> IsWorkflowRunActorAsync(IActor actor, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<string?> GetBoundWorkflowNameAsync(IActor actor, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task BindWorkflowDefinitionAsync(
            IActor actor,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default) =>
            Task.FromResult(WorkflowYamlParseResult.Success("direct"));
    }

    private sealed class DeterministicCommandContextPolicy : ICommandContextPolicy
    {
        private readonly string _commandId;
        private readonly string _correlationId;
        private readonly IReadOnlyDictionary<string, string> _metadata;

        public DeterministicCommandContextPolicy(
            string commandId,
            string correlationId,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            _commandId = commandId;
            _correlationId = correlationId;
            _metadata = metadata ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public CommandContext Create(
            string targetId,
            IReadOnlyDictionary<string, string>? metadata = null,
            string? commandId = null,
            string? correlationId = null)
        {
            _ = metadata;
            _ = commandId;
            _ = correlationId;
            return new CommandContext(
                targetId,
                _commandId,
                _correlationId,
                new Dictionary<string, string>(_metadata, StringComparer.Ordinal));
        }
    }

    private sealed class CapturingProjectionPort : IWorkflowExecutionProjectionLifecyclePort
    {
        public bool ProjectionEnabled { get; set; } = true;
        public bool EnableActorQueryEndpoints => false;
        public bool ThrowOnAttach { get; set; }
        public bool ThrowOnDetach { get; set; }
        public IEventSink<WorkflowRunEvent>? LastAttachedSink { get; private set; }
        public List<(string ActorId, string WorkflowName, string Input, string CommandId)> EnsureCalls { get; } = [];
        public List<(string ActorId, string CommandId)> DetachCalls { get; } = [];
        public List<(string ActorId, string CommandId)> ReleaseCalls { get; } = [];

        public Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(
            string rootActorId,
            string workflowName,
            string input,
            string commandId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EnsureCalls.Add((rootActorId, workflowName, input, commandId));
            return Task.FromResult<IWorkflowExecutionProjectionLease?>(new ProjectionLease(rootActorId, commandId));
        }

        public Task AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEvent> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            LastAttachedSink = sink;
            if (ThrowOnAttach)
                throw new InvalidOperationException("attach failed");

            return Task.CompletedTask;
        }

        public Task DetachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEvent> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DetachCalls.Add((lease.ActorId, lease.CommandId));
            if (ThrowOnDetach)
                throw new InvalidOperationException("detach failed");

            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IWorkflowExecutionProjectionLease lease,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReleaseCalls.Add((lease.ActorId, lease.CommandId));
            return Task.CompletedTask;
        }

        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkflowActorSnapshot?>(null);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(int take = 200, CancellationToken ct = default)
        {
            _ = take;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);
        }

        public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(string actorId, int take = 200, CancellationToken ct = default)
        {
            _ = actorId;
            _ = take;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkflowActorTimelineItem>>([]);
        }

        public Task<IReadOnlyList<WorkflowActorGraphEdge>> GetActorGraphEdgesAsync(
            string actorId,
            int take = 200,
            CancellationToken ct = default)
        {
            _ = actorId;
            _ = take;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkflowActorGraphEdge>>([]);
        }

        public Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            CancellationToken ct = default)
        {
            _ = depth;
            _ = take;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new WorkflowActorGraphSubgraph
            {
                RootNodeId = actorId,
            });
        }
    }

    private sealed class ComponentEnvelopeFactory : ICommandEnvelopeFactory<WorkflowChatRunRequest>
    {
        public EventEnvelope CreateEnvelope(WorkflowChatRunRequest command, CommandContext context)
        {
            _ = command;
            return new EventEnvelope
            {
                Id = context.CommandId,
                CorrelationId = context.CorrelationId,
                TargetActorId = context.TargetId,
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(new Empty()),
                PublisherId = "tests",
                Direction = EventDirection.Self,
            };
        }
    }

    private sealed class CapturingRequestExecutor : IWorkflowRunRequestExecutor
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task ExecuteAsync(
            IActor actor,
            string actorId,
            EventEnvelope requestEnvelope,
            IEventSink<WorkflowRunEvent> sink,
            CancellationToken ct = default)
        {
            _ = actor;
            _ = sink;
            ct.ThrowIfCancellationRequested();
            Calls.Add((actorId, requestEnvelope));
            return Task.CompletedTask;
        }
    }

    private sealed class SequenceOutputStreamer : IWorkflowRunOutputStreamer
    {
        private readonly IReadOnlyList<WorkflowOutputFrame> _frames;

        public SequenceOutputStreamer(IReadOnlyList<WorkflowOutputFrame> frames)
        {
            _frames = frames;
        }

        public async Task StreamAsync(
            IEventSink<WorkflowRunEvent> sink,
            Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
            CancellationToken ct = default)
        {
            _ = sink;
            foreach (var frame in _frames)
                await emitAsync(frame, ct);
        }

        public WorkflowOutputFrame Map(WorkflowRunEvent evt) => new WorkflowRunOutputStreamer().Map(evt);
    }

    private sealed class ThrowingOutputStreamer : IWorkflowRunOutputStreamer
    {
        private readonly Exception _exception;

        public ThrowingOutputStreamer(Exception exception)
        {
            _exception = exception;
        }

        public Task StreamAsync(
            IEventSink<WorkflowRunEvent> sink,
            Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
            CancellationToken ct = default)
        {
            _ = sink;
            _ = emitAsync;
            ct.ThrowIfCancellationRequested();
            return Task.FromException(_exception);
        }

        public WorkflowOutputFrame Map(WorkflowRunEvent evt) => new WorkflowRunOutputStreamer().Map(evt);
    }

    private sealed class NoopProjectionQueryPort : IWorkflowExecutionProjectionQueryPort
    {
        public Dictionary<string, WorkflowActorSnapshot> SnapshotByActorId { get; init; } = new(StringComparer.Ordinal);

        public bool EnableActorQueryEndpoints => true;

        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SnapshotByActorId.TryGetValue(actorId, out var snapshot);
            return Task.FromResult<WorkflowActorSnapshot?>(snapshot);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(int take = 200, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);
        }

        public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(string actorId, int take = 200, CancellationToken ct = default)
        {
            _ = actorId;
            _ = take;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkflowActorTimelineItem>>([]);
        }

        public Task<IReadOnlyList<WorkflowActorGraphEdge>> GetActorGraphEdgesAsync(
            string actorId,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
        {
            _ = actorId;
            _ = take;
            _ = options;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkflowActorGraphEdge>>([]);
        }

        public Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
        {
            _ = depth;
            _ = take;
            _ = options;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new WorkflowActorGraphSubgraph
            {
                RootNodeId = actorId,
            });
        }

        public Task<WorkflowActorGraphEnrichedSnapshot?> GetActorGraphEnrichedSnapshotAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
        {
            _ = actorId;
            _ = depth;
            _ = take;
            _ = options;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkflowActorGraphEnrichedSnapshot?>(null);
        }
    }

    private sealed class SpyResourceFinalizer : IWorkflowRunResourceFinalizer
    {
        public List<(WorkflowRunContext RunContext, Task ProcessingTask)> Calls { get; } = [];

        public Task FinalizeAsync(
            WorkflowRunContext runContext,
            Task processingTask,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((runContext, processingTask));
            return Task.CompletedTask;
        }
    }

    private sealed class TrackingSink : IEventSink<WorkflowRunEvent>
    {
        public bool Completed { get; private set; }
        public bool Disposed { get; private set; }

        public void Push(WorkflowRunEvent evt)
        {
            _ = evt;
        }

        public ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void Complete() => Completed = true;

        public async IAsyncEnumerable<WorkflowRunEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ComponentActor : IActor
    {
        public ComponentActor(string id)
        {
            Id = id;
            Agent = new ComponentAgent($"agent-{id}");
        }

        public string Id { get; }
        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ComponentAgent : IAgent
    {
        public ComponentAgent(string id)
        {
            Id = id;
        }

        public string Id { get; }
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("component-agent");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed record ProjectionLease(string ActorId, string CommandId) : IWorkflowExecutionProjectionLease;
}
