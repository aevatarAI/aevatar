using System.Collections.Concurrent;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowApplicationLayerTests
{
    [Fact]
    public async Task WorkflowRunInteractionService_ShouldReturnError_WhenDispatchFails()
    {
        var pipeline = new FakeDispatchPipeline
        {
            Result = CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>
                .Failure(WorkflowChatRunStartError.AgentNotFound),
        };
        var outputStreamer = new FakeWorkflowRunOutputStreamer();
        var snapshotEmitter = new FakeWorkflowRunStateSnapshotEmitter();
        var service = new WorkflowRunInteractionService(
            pipeline,
            outputStreamer,
            new FakeWorkflowRunCompletionPolicy(),
            snapshotEmitter,
            new WorkflowDirectFallbackPolicy(new WorkflowRunBehaviorOptions()));

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", "direct", null),
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.AgentNotFound);
        outputStreamer.StreamCalls.Should().Be(0);
        snapshotEmitter.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunInteractionService_ShouldEmitFramesSnapshotAndReleaseTarget()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateBoundTarget(projectionPort, actorPort, "actor-1", "direct", "cmd-1", ["definition-1", "actor-1"]);
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var pipeline = new FakeDispatchPipeline
        {
            Result = Success(target, receipt),
        };
        var outputStreamer = new FakeWorkflowRunOutputStreamer
        {
            Frames = [BuildFrame("progress"), BuildFrame("done")],
        };
        var completionPolicy = new FakeWorkflowRunCompletionPolicy
        {
            TerminalFrameType = "done",
            TerminalStatus = WorkflowProjectionCompletionStatus.Completed,
        };
        var snapshotEmitter = new FakeWorkflowRunStateSnapshotEmitter();
        var service = new WorkflowRunInteractionService(
            pipeline,
            outputStreamer,
            completionPolicy,
            snapshotEmitter,
            new WorkflowDirectFallbackPolicy(new WorkflowRunBehaviorOptions()));
        var emittedFrames = new ConcurrentQueue<WorkflowOutputFrame>();
        var acceptedReceipts = new ConcurrentQueue<WorkflowChatRunAcceptedReceipt>();

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", "direct", null),
            (frame, _) =>
            {
                emittedFrames.Enqueue(frame);
                return ValueTask.CompletedTask;
            },
            (accepted, _) =>
            {
                acceptedReceipts.Enqueue(accepted);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(receipt);
        result.FinalizeResult.Should().Be(new WorkflowChatRunFinalizeResult(WorkflowProjectionCompletionStatus.Completed, true));
        acceptedReceipts.Should().ContainSingle().Which.Should().Be(receipt);
        emittedFrames.Should().HaveCount(2);
        snapshotEmitter.Calls.Should().ContainSingle();
        snapshotEmitter.Calls.Single().Receipt.Should().Be(receipt);
        projectionPort.DetachCalls.Should().ContainSingle();
        projectionPort.ReleaseCalls.Should().ContainSingle();
        actorPort.DestroyCalls.Should().Equal("actor-1", "definition-1");
    }

    [Fact]
    public async Task WorkflowRunInteractionService_ShouldNotDestroyActors_WhenTerminalFrameNotObserved()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateBoundTarget(projectionPort, actorPort, "actor-1", "direct", "cmd-1", ["definition-1", "actor-1"]);
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var pipeline = new FakeDispatchPipeline
        {
            Result = Success(target, receipt),
        };
        var service = new WorkflowRunInteractionService(
            pipeline,
            new FakeWorkflowRunOutputStreamer { Frames = [BuildFrame("progress")] },
            new FakeWorkflowRunCompletionPolicy { TerminalFrameType = "done" },
            new FakeWorkflowRunStateSnapshotEmitter(),
            new WorkflowDirectFallbackPolicy(new WorkflowRunBehaviorOptions()));

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", "direct", null),
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FinalizeResult.Should().Be(new WorkflowChatRunFinalizeResult(WorkflowProjectionCompletionStatus.Failed, false));
        actorPort.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunDetachedDispatchService_ShouldReturnFailure_WhenDispatchFails()
    {
        var pipeline = new FakeDispatchPipeline
        {
            Result = CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>
                .Failure(WorkflowChatRunStartError.WorkflowNotFound),
        };
        var service = new WorkflowRunDetachedDispatchService(
            pipeline,
            new FakeWorkflowRunOutputStreamer(),
            new FakeWorkflowRunCompletionPolicy(),
            new WorkflowDirectFallbackPolicy(new WorkflowRunBehaviorOptions()));

        var result = await service.DispatchAsync(new WorkflowChatRunRequest("hello", "missing", null));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowNotFound);
    }

    [Fact]
    public async Task WorkflowRunDetachedDispatchService_ShouldDrainInBackgroundAndReleaseTarget()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateBoundTarget(projectionPort, actorPort, "actor-1", "direct", "cmd-1", ["definition-1", "actor-1"]);
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var outputStreamer = new FakeWorkflowRunOutputStreamer
        {
            Frames = [BuildFrame("progress"), BuildFrame("done")],
        };
        var service = new WorkflowRunDetachedDispatchService(
            new FakeDispatchPipeline { Result = Success(target, receipt) },
            outputStreamer,
            new FakeWorkflowRunCompletionPolicy { TerminalFrameType = "done" },
            new WorkflowDirectFallbackPolicy(new WorkflowRunBehaviorOptions()));

        var result = await service.DispatchAsync(new WorkflowChatRunRequest("hello", "direct", null));

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(receipt);
        await outputStreamer.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await projectionPort.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        projectionPort.DetachCalls.Should().ContainSingle();
        actorPort.DestroyCalls.Should().Equal("actor-1", "definition-1");
    }

    private static CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError> Success(
        WorkflowRunCommandTarget target,
        WorkflowChatRunAcceptedReceipt receipt) =>
        CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>.Success(
            new CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>
            {
                Target = target,
                Context = new CommandContext(target.ActorId, receipt.CommandId, receipt.CorrelationId, new Dictionary<string, string>()),
                Envelope = new EventEnvelope { Id = "evt-1" },
                Receipt = receipt,
            });

    private static WorkflowRunCommandTarget CreateBoundTarget(
        FakeProjectionPort projectionPort,
        FakeWorkflowRunActorPort actorPort,
        string actorId,
        string workflowName,
        string commandId,
        IReadOnlyList<string>? createdActorIds = null)
    {
        var target = new WorkflowRunCommandTarget(
            new FakeActor(actorId),
            workflowName,
            createdActorIds ?? [],
            projectionPort,
            actorPort);
        target.BindLiveObservation(new FakeProjectionLease(actorId, commandId), new EventChannel<WorkflowRunEvent>());
        return target;
    }

    private static WorkflowOutputFrame BuildFrame(string type) =>
        new()
        {
            Type = type,
        };

    private sealed class FakeDispatchPipeline
        : ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError> Result { get; set; } =
            CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>
                .Failure(WorkflowChatRunStartError.AgentNotFound);

        public Task<CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            _ = command;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeWorkflowRunOutputStreamer : IWorkflowRunOutputStreamer
    {
        public IReadOnlyList<WorkflowOutputFrame> Frames { get; set; } = [];
        public int StreamCalls { get; private set; }
        public TaskCompletionSource<bool> StreamStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StreamAsync(
            IEventSink<WorkflowRunEvent> sink,
            Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
            CancellationToken ct = default)
        {
            _ = sink;
            StreamCalls++;
            StreamStarted.TrySetResult(true);

            foreach (var frame in Frames)
                await emitAsync(frame, ct);
        }

        public WorkflowOutputFrame Map(WorkflowRunEvent evt)
        {
            _ = evt;
            return BuildFrame("mapped");
        }
    }

    private sealed class FakeWorkflowRunCompletionPolicy : IWorkflowRunCompletionPolicy
    {
        public string? TerminalFrameType { get; set; }
        public WorkflowProjectionCompletionStatus TerminalStatus { get; set; } = WorkflowProjectionCompletionStatus.Completed;

        public bool TryResolve(WorkflowOutputFrame frame, out WorkflowProjectionCompletionStatus status)
        {
            if (!string.IsNullOrWhiteSpace(TerminalFrameType) &&
                string.Equals(frame.Type, TerminalFrameType, StringComparison.Ordinal))
            {
                status = TerminalStatus;
                return true;
            }

            status = WorkflowProjectionCompletionStatus.Unknown;
            return false;
        }
    }

    private sealed class FakeWorkflowRunStateSnapshotEmitter : IWorkflowRunStateSnapshotEmitter
    {
        public List<(WorkflowChatRunAcceptedReceipt Receipt, WorkflowProjectionCompletionStatus Status, bool ProjectionCompleted)> Calls { get; } = [];

        public Task EmitAsync(
            WorkflowChatRunAcceptedReceipt receipt,
            WorkflowProjectionCompletionStatus projectionCompletionStatus,
            bool projectionCompleted,
            Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
            CancellationToken ct = default)
        {
            _ = emitAsync;
            ct.ThrowIfCancellationRequested();
            Calls.Add((receipt, projectionCompletionStatus, projectionCompleted));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProjectionPort : IWorkflowExecutionProjectionLifecyclePort
    {
        public bool ProjectionEnabled => true;
        public List<(IWorkflowExecutionProjectionLease Lease, IEventSink<WorkflowRunEvent> Sink)> DetachCalls { get; } = [];
        public List<IWorkflowExecutionProjectionLease> ReleaseCalls { get; } = [];
        public TaskCompletionSource<bool> Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(
            string rootActorId,
            string workflowName,
            string input,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult<IWorkflowExecutionProjectionLease?>(new FakeProjectionLease(rootActorId, commandId));

        public Task AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEvent> sink,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DetachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEvent> sink,
            CancellationToken ct = default)
        {
            DetachCalls.Add((lease, sink));
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IWorkflowExecutionProjectionLease lease,
            CancellationToken ct = default)
        {
            ReleaseCalls.Add(lease);
            Released.TrySetResult(true);
            return Task.CompletedTask;
        }
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

    private sealed class FakeWorkflowRunActorPort : IWorkflowRunActorPort
    {
        public List<string> DestroyCalls { get; } = [];

        public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default)
        {
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
