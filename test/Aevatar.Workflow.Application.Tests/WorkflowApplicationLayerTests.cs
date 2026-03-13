using System.Collections.Concurrent;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;
using Any = Google.Protobuf.WellKnownTypes.Any;
using StringValue = Google.Protobuf.WellKnownTypes.StringValue;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowApplicationLayerTests
{
    [Fact]
    public async Task CommandInteractionService_ShouldReturnError_WhenDispatchFails()
    {
        var pipeline = new FakeDispatchPipeline
        {
            Result = CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>
                .Failure(WorkflowChatRunStartError.AgentNotFound),
        };
        var outputStream = new FakeEventOutputStream();
        var finalizeEmitter = new FakeFinalizeEmitter();
        var service = CreateInteractionService(
            pipeline,
            outputStream,
            new FakeWorkflowRunCompletionPolicy(),
            finalizeEmitter,
            new FakeDurableCompletionResolver());

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", "direct", null),
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.AgentNotFound);
        outputStream.PumpCalls.Should().Be(0);
        finalizeEmitter.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CommandInteractionService_ShouldEmitFramesSnapshotAndReleaseTarget()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort
        {
            ExpectedDestroyCount = 2,
        };
        var target = CreateBoundTarget(projectionPort, actorPort, "actor-1", "direct", "cmd-1", ["definition-1", "actor-1"]);
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var pipeline = new FakeDispatchPipeline
        {
            Result = Success(target, receipt),
        };
        var outputStream = new FakeEventOutputStream
        {
            Events = [BuildEvent("progress"), BuildEvent("done")],
        };
        var completionPolicy = new FakeWorkflowRunCompletionPolicy
        {
            TerminalEventCase = WorkflowRunEventEnvelope.EventOneofCase.RunFinished,
            TerminalStatus = WorkflowProjectionCompletionStatus.Completed,
        };
        var finalizeEmitter = new FakeFinalizeEmitter();
        var service = CreateInteractionService(
            pipeline,
            outputStream,
            completionPolicy,
            finalizeEmitter,
            new FakeDurableCompletionResolver());
        var emittedFrames = new ConcurrentQueue<WorkflowRunEventEnvelope>();
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
        result.FinalizeResult.Should().Be(new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
        acceptedReceipts.Should().ContainSingle().Which.Should().Be(receipt);
        emittedFrames.Should().HaveCount(2);
        finalizeEmitter.Calls.Should().ContainSingle();
        finalizeEmitter.Calls.Single().Receipt.Should().Be(receipt);
        projectionPort.DetachCalls.Should().ContainSingle();
        projectionPort.ReleaseCalls.Should().ContainSingle();
        actorPort.DestroyCalls.Should().Equal("actor-1", "definition-1");
    }

    [Fact]
    public async Task CommandInteractionService_ShouldPreserveSuccess_WhenCleanupFails()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort
        {
            DestroyException = new InvalidOperationException("cleanup failed"),
        };
        var target = CreateBoundTarget(projectionPort, actorPort, "actor-1", "direct", "cmd-1", ["definition-1", "actor-1"]);
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var service = CreateInteractionService(
            new FakeDispatchPipeline { Result = Success(target, receipt) },
            new FakeEventOutputStream { Events = [BuildEvent("done")] },
            new FakeWorkflowRunCompletionPolicy
            {
                TerminalEventCase = WorkflowRunEventEnvelope.EventOneofCase.RunFinished,
                TerminalStatus = WorkflowProjectionCompletionStatus.Completed,
            },
            new FakeFinalizeEmitter(),
            new FakeDurableCompletionResolver());

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", "direct", null),
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FinalizeResult.Should().Be(new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
        projectionPort.DetachCalls.Should().ContainSingle();
        actorPort.DestroyCalls.Should().Equal("actor-1", "definition-1");
    }

    [Fact]
    public async Task CommandInteractionService_ShouldDestroyActors_WhenTerminalFrameMissingButDurableStateIsTerminal()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateBoundTarget(projectionPort, actorPort, "actor-1", "direct", "cmd-1", ["definition-1", "actor-1"]);
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var pipeline = new FakeDispatchPipeline
        {
            Result = Success(target, receipt),
        };
        var service = CreateInteractionService(
            pipeline,
            new FakeEventOutputStream { Events = [BuildEvent("progress")] },
            new FakeWorkflowRunCompletionPolicy { TerminalEventCase = WorkflowRunEventEnvelope.EventOneofCase.RunFinished },
            new FakeFinalizeEmitter(),
            new FakeDurableCompletionResolver(
                new CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>(
                    true,
                    WorkflowProjectionCompletionStatus.Completed)));

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", "direct", null),
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FinalizeResult.Should().Be(new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
        actorPort.DestroyCalls.Should().Equal("actor-1", "definition-1");
    }

    [Fact]
    public async Task CommandInteractionService_ShouldNotDestroyActors_WhenTerminalFrameMissingAndDurableStateIsNonTerminal()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateBoundTarget(projectionPort, actorPort, "actor-1", "direct", "cmd-1", ["definition-1", "actor-1"]);
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var service = CreateInteractionService(
            new FakeDispatchPipeline { Result = Success(target, receipt) },
            new FakeEventOutputStream { Events = [BuildEvent("progress")] },
            new FakeWorkflowRunCompletionPolicy { TerminalEventCase = WorkflowRunEventEnvelope.EventOneofCase.RunFinished },
            new FakeFinalizeEmitter(),
            new FakeDurableCompletionResolver());

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", "direct", null),
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FinalizeResult.Should().Be(new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Unknown, false));
        actorPort.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DetachedCommandDispatchService_ShouldReturnFailure_WhenDispatchFails()
    {
        var pipeline = new FakeDispatchPipeline
        {
            Result = CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>
                .Failure(WorkflowChatRunStartError.WorkflowNotFound),
        };
        var service = CreateDetachedDispatchService(
            pipeline,
            new FakeDetachedCleanupScheduler());

        var result = await service.DispatchAsync(new WorkflowChatRunRequest("hello", "missing", null));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowNotFound);
    }

    [Fact]
    public async Task DetachedCommandDispatchService_ShouldDetachLiveObservation_AndScheduleDurableCleanup()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateBoundTarget(projectionPort, actorPort, "actor-1", "direct", "cmd-1", ["definition-1", "actor-1"]);
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var scheduler = new FakeDetachedCleanupScheduler();
        var service = CreateDetachedDispatchService(
            new FakeDispatchPipeline { Result = Success(target, receipt) },
            scheduler);

        var result = await service.DispatchAsync(new WorkflowChatRunRequest("hello", "direct", null));

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(receipt);
        projectionPort.DetachCalls.Should().ContainSingle();
        projectionPort.ReleaseCalls.Should().BeEmpty();
        actorPort.DestroyCalls.Should().BeEmpty();
        scheduler.Requests.Should().ContainSingle();
        scheduler.Requests.Single().ActorId.Should().Be("actor-1");
        scheduler.Requests.Single().WorkflowName.Should().Be("direct");
        scheduler.Requests.Single().CommandId.Should().Be("cmd-1");
        scheduler.Requests.Single().CreatedActorIds.Should().Equal("definition-1", "actor-1");
    }

    [Fact]
    public async Task DetachedCommandDispatchService_ShouldScheduleDurableCleanup_AfterDetachingLiveObservation()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateBoundTarget(projectionPort, actorPort, "actor-1", "direct", "cmd-1", ["definition-1", "actor-1"]);
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var scheduler = new AssertingDetachedCleanupScheduler(() => projectionPort.DetachCalls.Count);
        var service = CreateDetachedDispatchService(
            new FakeDispatchPipeline { Result = Success(target, receipt) },
            scheduler);

        var result = await service.DispatchAsync(new WorkflowChatRunRequest("hello", "direct", null));

        result.Succeeded.Should().BeTrue();
        projectionPort.DetachCalls.Should().ContainSingle();
        scheduler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task DetachedCommandDispatchService_ShouldScheduleDurableCleanup_WhenDetachFails()
    {
        var projectionPort = new FakeProjectionPort
        {
            DetachException = new InvalidOperationException("detach failed"),
            DetachFailureCount = 1,
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateBoundTarget(projectionPort, actorPort, "actor-1", "direct", "cmd-1", ["definition-1", "actor-1"]);
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var scheduler = new FakeDetachedCleanupScheduler();
        var service = CreateDetachedDispatchService(
            new FakeDispatchPipeline { Result = Success(target, receipt) },
            scheduler);

        var result = await service.DispatchAsync(new WorkflowChatRunRequest("hello", "direct", null));

        result.Succeeded.Should().BeTrue();
        projectionPort.DetachCalls.Should().ContainSingle();
        projectionPort.ReleaseCalls.Should().BeEmpty();
        actorPort.DestroyCalls.Should().BeEmpty();
        scheduler.Requests.Should().ContainSingle();
        target.LiveSink.Should().BeNull();
    }

    [Fact]
    public async Task DetachedCommandDispatchService_ShouldBubble_WhenDurableCleanupCannotBeScheduled()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateBoundTarget(projectionPort, actorPort, "actor-1", "direct", "cmd-1", ["definition-1", "actor-1"]);
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var service = CreateDetachedDispatchService(
            new FakeDispatchPipeline { Result = Success(target, receipt) },
            new ThrowingDetachedCleanupScheduler(new InvalidOperationException("schedule failed")));

        var act = async () => await service.DispatchAsync(new WorkflowChatRunRequest("hello", "direct", null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("schedule failed");
        projectionPort.DetachCalls.Should().ContainSingle();
    }

    private static ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> CreateInteractionService(
        ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> pipeline,
        IEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope> outputStream,
        ICommandCompletionPolicy<WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> completionPolicy,
        ICommandFinalizeEmitter<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus, WorkflowRunEventEnvelope> finalizeEmitter,
        ICommandDurableCompletionResolver<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus> durableCompletionResolver) =>
        new DefaultCommandInteractionService<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>(
            pipeline,
            outputStream,
            completionPolicy,
            finalizeEmitter,
            durableCompletionResolver);

    private static ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> CreateDetachedDispatchService(
        ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> pipeline,
        IWorkflowRunDetachedCleanupScheduler cleanupScheduler) =>
        new WorkflowRunDetachedDispatchService(
            pipeline,
            cleanupScheduler,
            logger: null);

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
        target.BindLiveObservation(new FakeProjectionLease(actorId, commandId), new EventChannel<WorkflowRunEventEnvelope>());
        return target;
    }

    private static WorkflowRunEventEnvelope BuildEvent(string type) =>
        string.Equals(type, "done", StringComparison.Ordinal)
            ? new WorkflowRunEventEnvelope
            {
                RunFinished = new WorkflowRunFinishedEventPayload
                {
                    ThreadId = "actor-1",
                    Result = Any.Pack(new WorkflowRunResultPayload { Output = type }),
                },
            }
            : new WorkflowRunEventEnvelope
            {
                Custom = new WorkflowCustomEventPayload
                {
                    Name = type,
                    Payload = Any.Pack(new StringValue { Value = type }),
                },
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

    private sealed class FakeEventOutputStream : IEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>
    {
        public IReadOnlyList<WorkflowRunEventEnvelope> Events { get; set; } = [];
        public int PumpCalls { get; private set; }
        public TaskCompletionSource<bool> PumpStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PumpAsync(
            IAsyncEnumerable<WorkflowRunEventEnvelope> events,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowRunEventEnvelope, bool>? shouldStop = null,
            CancellationToken ct = default)
        {
            _ = events;
            PumpCalls++;
            PumpStarted.TrySetResult(true);

            foreach (var evt in Events)
            {
                await emitAsync(evt, ct);
                if (shouldStop?.Invoke(evt) == true)
                    break;
            }
        }
    }

    private sealed class FakeWorkflowRunCompletionPolicy : ICommandCompletionPolicy<WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>
    {
        public WorkflowRunEventEnvelope.EventOneofCase? TerminalEventCase { get; set; }
        public WorkflowProjectionCompletionStatus TerminalStatus { get; set; } = WorkflowProjectionCompletionStatus.Completed;
        public WorkflowProjectionCompletionStatus IncompleteCompletion => WorkflowProjectionCompletionStatus.Unknown;

        public bool TryResolve(WorkflowRunEventEnvelope evt, out WorkflowProjectionCompletionStatus status)
        {
            if (TerminalEventCase.HasValue && evt.EventCase == TerminalEventCase.Value)
            {
                status = TerminalStatus;
                return true;
            }

            status = WorkflowProjectionCompletionStatus.Unknown;
            return false;
        }
    }

    private sealed class FakeFinalizeEmitter : ICommandFinalizeEmitter<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus, WorkflowRunEventEnvelope>
    {
        public List<(WorkflowChatRunAcceptedReceipt Receipt, WorkflowProjectionCompletionStatus Status, bool Completed)> Calls { get; } = [];

        public Task EmitAsync(
            WorkflowChatRunAcceptedReceipt receipt,
            WorkflowProjectionCompletionStatus completion,
            bool completed,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            CancellationToken ct = default)
        {
            _ = emitAsync;
            ct.ThrowIfCancellationRequested();
            Calls.Add((receipt, completion, completed));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDurableCompletionResolver(
        CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>? observation = null) : ICommandDurableCompletionResolver<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>
    {
        private readonly CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus> _observation =
            observation ?? CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete;

        public int Calls { get; private set; }

        public Task<CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>> ResolveAsync(
            WorkflowChatRunAcceptedReceipt receipt,
            CancellationToken ct = default)
        {
            _ = receipt;
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(_observation);
        }
    }

    private sealed class FakeDetachedCleanupScheduler : IWorkflowRunDetachedCleanupScheduler
    {
        public List<WorkflowRunDetachedCleanupRequest> Requests { get; } = [];

        public Task ScheduleAsync(
            WorkflowRunDetachedCleanupRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class AssertingDetachedCleanupScheduler(Func<int> detachCallCountAccessor)
        : IWorkflowRunDetachedCleanupScheduler
    {
        public List<WorkflowRunDetachedCleanupRequest> Requests { get; } = [];

        public Task ScheduleAsync(
            WorkflowRunDetachedCleanupRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ct.ThrowIfCancellationRequested();
            detachCallCountAccessor().Should().Be(1);
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingDetachedCleanupScheduler(Exception exception) : IWorkflowRunDetachedCleanupScheduler
    {
        public Task ScheduleAsync(
            WorkflowRunDetachedCleanupRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ct.ThrowIfCancellationRequested();
            return Task.FromException(exception);
        }
    }

    private sealed class FakeProjectionPort : IWorkflowExecutionProjectionPort
    {
        public bool ProjectionEnabled => true;
        public List<(IWorkflowExecutionProjectionLease Lease, IEventSink<WorkflowRunEventEnvelope> Sink)> DetachCalls { get; } = [];
        public List<IWorkflowExecutionProjectionLease> ReleaseAttempts { get; } = [];
        public List<IWorkflowExecutionProjectionLease> ReleaseCalls { get; } = [];
        public TaskCompletionSource<bool> Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? DetachException { get; set; }
        public Exception? ReleaseException { get; set; }
        public int DetachFailureCount { get; set; }
        public int ReleaseFailureCount { get; set; }
        public int ReleaseAttemptCount => ReleaseAttempts.Count;

        public Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(
            string rootActorId,
            string workflowName,
            string input,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult<IWorkflowExecutionProjectionLease?>(new FakeProjectionLease(rootActorId, commandId));

        public Task AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            if (lease is FakeProjectionLease trackingLease)
                trackingLease.LiveSinkAttached = true;

            return Task.CompletedTask;
        }

        public Task DetachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            DetachCalls.Add((lease, sink));
            if (DetachFailureCount > 0)
            {
                DetachFailureCount--;
                throw DetachException ?? new InvalidOperationException("detach failed");
            }

            if (lease is FakeProjectionLease trackingLease)
                trackingLease.LiveSinkAttached = false;

            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IWorkflowExecutionProjectionLease lease,
            CancellationToken ct = default)
        {
            ReleaseAttempts.Add(lease);
            if (lease is FakeProjectionLease trackingLease &&
                trackingLease.LiveSinkAttached)
            {
                return Task.CompletedTask;
            }

            if (ReleaseFailureCount > 0)
            {
                ReleaseFailureCount--;
                throw ReleaseException ?? new InvalidOperationException("release failed");
            }

            ReleaseCalls.Add(lease);
            Released.TrySetResult(true);
            if (lease is FakeProjectionLease releasedLease)
                releasedLease.Released = true;

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
        public bool LiveSinkAttached { get; set; } = true;
        public bool Released { get; set; }
    }

    private sealed class FakeWorkflowRunActorPort : IWorkflowRunActorPort
    {
        public List<string> DestroyCalls { get; } = [];
        public int ExpectedDestroyCount { get; set; } = int.MaxValue;
        public TaskCompletionSource<bool> DestroyCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? DestroyException { get; set; }

        public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default)
        {
            DestroyCalls.Add(actorId);
            if (DestroyCalls.Count >= ExpectedDestroyCount)
                DestroyCompleted.TrySetResult(true);
            if (DestroyException != null)
                throw DestroyException;

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
