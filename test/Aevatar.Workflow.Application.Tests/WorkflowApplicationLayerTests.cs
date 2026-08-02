using System.Collections.Concurrent;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Abstractions;
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
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), ExternalCapabilityExecutionMode.Interactive),
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
        target.RequireLiveSink().Push(BuildEvent("progress"));
        target.RequireLiveSink().Push(BuildEvent("done"));
        target.RequireLiveSink().Complete();
        var outputStream = new FakeEventOutputStream();
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
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), ExternalCapabilityExecutionMode.Interactive),
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
    public async Task CommandInteractionService_ShouldNotifyAcceptedAfterPreparedDispatchBeforeEmittingOutput()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateBoundTarget(projectionPort, actorPort, "actor-1", "direct", "cmd-1", ["definition-1", "actor-1"]);
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var dispatchRelease = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new FakeDispatchPipeline
        {
            Result = Success(target, receipt),
            DispatchPreparedRelease = dispatchRelease,
        };
        var outputStream = new FakeEventOutputStream();
        var accepted = new TaskCompletionSource<WorkflowChatRunAcceptedReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedRelease = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var emitted = new ConcurrentQueue<WorkflowRunEventEnvelope>();
        var service = CreateInteractionService(
            pipeline,
            outputStream,
            new FakeWorkflowRunCompletionPolicy
            {
                TerminalEventCase = WorkflowRunEventEnvelope.EventOneofCase.RunFinished,
                TerminalStatus = WorkflowProjectionCompletionStatus.Completed,
            },
            new FakeFinalizeEmitter(),
            new FakeDurableCompletionResolver());

        var executeTask = service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), ExternalCapabilityExecutionMode.Interactive),
            (frame, _) =>
            {
                emitted.Enqueue(frame);
                return ValueTask.CompletedTask;
            },
            async (acceptedReceipt, _) =>
            {
                accepted.SetResult(acceptedReceipt);
                await acceptedRelease.Task.ConfigureAwait(false);
            },
            CancellationToken.None);

        pipeline.DispatchPreparedCalls.Should().Be(1);
        outputStream.PumpCalls.Should().Be(1);
        emitted.Should().BeEmpty();

        await target.RequireLiveSink().PushAsync(BuildEvent("done"), CancellationToken.None);
        (await outputStream.EventRead.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        emitted.Should().BeEmpty();

        dispatchRelease.SetResult(null);
        (await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(receipt);
        pipeline.DispatchPreparedCalls.Should().Be(1);
        emitted.Should().BeEmpty();

        acceptedRelease.SetResult(null);
        target.RequireLiveSink().Complete();
        var result = await executeTask.WaitAsync(TimeSpan.FromSeconds(5));

        result.Succeeded.Should().BeTrue();
        emitted.Should().ContainSingle();
    }

    [Fact]
    public async Task CommandInteractionService_ShouldSucceed_WhenDetachedReclaimDestroyFails()
    {
        // 06-20-observatory-run-state-feed (R2): created-actor reclaim is detached from the interaction, so a
        // reclaim/destroy failure must NOT fail the run. The interaction succeeds; the lease/sink are still
        // released in-request; the destroy failure is confined to the detached reclaim task.
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort
        {
            DestroyException = new InvalidOperationException("cleanup failed"),
        };
        var target = CreateBoundTarget(projectionPort, actorPort, "actor-1", "direct", "cmd-1", ["definition-1", "actor-1"]);
        target.RequireLiveSink().Push(BuildEvent("done"));
        target.RequireLiveSink().Complete();
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var service = CreateInteractionService(
            new FakeDispatchPipeline { Result = Success(target, receipt) },
            new FakeEventOutputStream(),
            new FakeWorkflowRunCompletionPolicy
            {
                TerminalEventCase = WorkflowRunEventEnvelope.EventOneofCase.RunFinished,
                TerminalStatus = WorkflowProjectionCompletionStatus.Completed,
            },
            new FakeFinalizeEmitter(),
            new FakeDurableCompletionResolver());

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), ExternalCapabilityExecutionMode.Interactive),
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        projectionPort.DetachCalls.Should().ContainSingle();
        // The detached reclaim attempted the destroy (which threw) without failing the interaction.
        var reclaim = () => target.PendingReclaimTask;
        await reclaim.Should().ThrowAsync<InvalidOperationException>();
        actorPort.DestroyCalls.Should().Equal("actor-1", "definition-1");
    }

    [Fact]
    public async Task CommandInteractionService_ShouldDestroyActors_WhenTerminalFrameMissingButDurableStateIsTerminal()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateBoundTarget(projectionPort, actorPort, "actor-1", "direct", "cmd-1", ["definition-1", "actor-1"]);
        target.RequireLiveSink().Push(BuildEvent("progress"));
        target.RequireLiveSink().Complete();
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var pipeline = new FakeDispatchPipeline
        {
            Result = Success(target, receipt),
        };
        var service = CreateInteractionService(
            pipeline,
            new FakeEventOutputStream(),
            new FakeWorkflowRunCompletionPolicy { TerminalEventCase = WorkflowRunEventEnvelope.EventOneofCase.RunFinished },
            new FakeFinalizeEmitter(),
            new FakeDurableCompletionResolver(
                new CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>(
                    true,
                    WorkflowProjectionCompletionStatus.Completed)));

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), ExternalCapabilityExecutionMode.Interactive),
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
        var target = CreateBoundTarget(
            projectionPort,
            actorPort,
            "actor-1",
            "direct",
            "cmd-1",
            ["definition-1", "actor-1"]);
        target.RequireLiveSink().Push(BuildEvent("progress"));
        target.RequireLiveSink().Complete();
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var service = CreateInteractionService(
            new FakeDispatchPipeline { Result = Success(target, receipt) },
            new FakeEventOutputStream(),
            new FakeWorkflowRunCompletionPolicy { TerminalEventCase = WorkflowRunEventEnvelope.EventOneofCase.RunFinished },
            new FakeFinalizeEmitter(),
            new FakeDurableCompletionResolver());

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), ExternalCapabilityExecutionMode.Interactive),
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FinalizeResult.Should().Be(new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Unknown, false));
        projectionPort.DetachCalls.Should().ContainSingle();
        projectionPort.ReleaseCalls.Should().ContainSingle();
        actorPort.DestroyCalls.Should().BeEmpty();
        target.ProjectionLease.Should().BeNull();
    }

    [Fact]
    public async Task AcceptedOnlyCommandDispatchService_ShouldReturnFailure_WhenDispatchFails()
    {
        var pipeline = new FakeAcceptedDispatchPipeline
        {
            Result = CommandTargetResolution<CommandDispatchExecution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>
                .Failure(WorkflowChatRunStartError.WorkflowNotFound),
        };
        var service = CreateAcceptedOnlyDispatchService(
            pipeline);

        var result = await service.DispatchAsync(new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("missing"), ExternalCapabilityExecutionMode.Interactive));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowNotFound);
    }

    [Fact]
    public async Task AcceptedOnlyCommandDispatchService_ShouldReturnReceipt_WithoutConsumingRunEvents()
    {
        var actorPort = new FakeWorkflowRunActorPort();
        var target = new WorkflowRunAcceptedCommandTarget("actor-1",
            "direct",
            ["definition-1", "actor-1"],
            actorPort);
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var service = CreateAcceptedOnlyDispatchService(
            new FakeAcceptedDispatchPipeline { Result = AcceptedSuccess(target, receipt) });

        var result = await service.DispatchAsync(new WorkflowChatRunRequest("hello", WorkflowChatSource.CatalogWorkflow("direct"), ExternalCapabilityExecutionMode.Interactive));

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(receipt);
        actorPort.DestroyCalls.Should().BeEmpty();
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

    private static ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> CreateAcceptedOnlyDispatchService(
        ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> pipeline) =>
        new DefaultCommandDispatchService<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>(pipeline);

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

    private static CommandTargetResolution<CommandDispatchExecution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError> AcceptedSuccess(
        WorkflowRunAcceptedCommandTarget target,
        WorkflowChatRunAcceptedReceipt receipt) =>
        CommandTargetResolution<CommandDispatchExecution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>.Success(
            new CommandDispatchExecution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt>
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
            actorId,
            workflowName,
            createdActorIds ?? [],
            projectionPort,
            actorPort,
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()),
            // 06-20-observatory-run-state-feed (R2): run the scheduled created-actor reclaim inline so the
            // interaction-service tests observe the destroy deterministically (no fire-and-forget race).
            detachedReclaimLauncher: reclaim => reclaim());
        var projectionLease = new FakeProjectionLease(actorId, commandId);
        target.BindLiveObservation(
            projectionLease,
            new FakeLiveSinkLease(projectionLease),
            new EventChannel<WorkflowRunEventEnvelope>());
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
        public Exception? PrepareException { get; set; }
        public Exception? DispatchPreparedException { get; set; }
        public bool CleanupOnDispatchPreparedFailure { get; set; } = true;
        public Action? AfterDispatchPrepared { get; set; }
        public TaskCompletionSource<object?>? DispatchPreparedRelease { get; set; }
        public int PrepareCalls { get; private set; }
        public int DispatchPreparedCalls { get; private set; }
        public int DispatchCalls { get; private set; }

        public Task<CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>> PrepareAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            _ = command;
            ct.ThrowIfCancellationRequested();
            PrepareCalls++;
            if (PrepareException != null)
                return Task.FromException<CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>>(PrepareException);

            return Task.FromResult(Result);
        }

        public async Task<DispatchAdmission> DispatchPreparedAsync(
            CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt> execution,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(execution);
            ct.ThrowIfCancellationRequested();
            DispatchPreparedCalls++;
            if (DispatchPreparedException == null)
            {
                if (DispatchPreparedRelease != null)
                    await DispatchPreparedRelease.Task.ConfigureAwait(false);
                AfterDispatchPrepared?.Invoke();
                return DispatchAdmissionFactory.Create(execution.Target.TargetId, execution.Envelope);
            }

            if (CleanupOnDispatchPreparedFailure && execution.Target is ICommandDispatchCleanupAware cleanupAware)
                await cleanupAware.CleanupAfterDispatchFailureAsync(CancellationToken.None);

            throw DispatchPreparedException;
        }

        public Task<CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default) =>
            DispatchAsyncCore(command, ct);

        private async Task<CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>> DispatchAsyncCore(
            WorkflowChatRunRequest command,
            CancellationToken ct)
        {
            DispatchCalls++;
            var prepared = await PrepareAsync(command, ct);
            if (!prepared.Succeeded || prepared.Target == null)
                return prepared;

            var admission = await DispatchPreparedAsync(prepared.Target, ct);
            return CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>.Success(
                prepared.Target with { Admission = admission });
        }
    }

    private sealed class FakeAcceptedDispatchPipeline
        : ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public CommandTargetResolution<CommandDispatchExecution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError> Result { get; set; } =
            CommandTargetResolution<CommandDispatchExecution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>
                .Failure(WorkflowChatRunStartError.AgentNotFound);

        public Task<CommandTargetResolution<CommandDispatchExecution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>> PrepareAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            _ = command;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }

        public Task<DispatchAdmission> DispatchPreparedAsync(
            CommandDispatchExecution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt> execution,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(execution);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(DispatchAdmissionFactory.Create(execution.Target.TargetId, execution.Envelope));
        }

        public Task<CommandTargetResolution<CommandDispatchExecution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>> DispatchAsync(
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
        public int PumpCalls { get; private set; }
        public TaskCompletionSource<bool> PumpStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> EventRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PumpAsync(
            IAsyncEnumerable<WorkflowRunEventEnvelope> events,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowRunEventEnvelope, bool>? shouldStop = null,
            CancellationToken ct = default)
        {
            PumpCalls++;
            PumpStarted.TrySetResult(true);

            await foreach (var evt in events.WithCancellation(ct))
            {
                EventRead.TrySetResult(true);
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

    private sealed class FakeProjectionPort
        : IWorkflowExecutionProjectionPort
    {
        public bool ProjectionEnabled => true;
        public List<IAsyncDisposable?> DetachCalls { get; } = [];
        public List<IWorkflowExecutionProjectionLease> ReleaseAttempts { get; } = [];
        public List<IWorkflowExecutionProjectionLease> ReleaseCalls { get; } = [];
        public TaskCompletionSource<bool> Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? DetachException { get; set; }
        public Exception? ReleaseException { get; set; }
        public int DetachFailureCount { get; set; }
        public int ReleaseFailureCount { get; set; }
        public int ReleaseAttemptCount => ReleaseAttempts.Count;
        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            if (lease is FakeProjectionLease trackingLease)
            {
                trackingLease.LiveSinkAttached = true;
                return Task.FromResult<IAsyncDisposable?>(new FakeLiveSinkLease(trackingLease));
            }

            return Task.FromResult<IAsyncDisposable?>(null);
        }

        public async Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
            string rootActorId,
            string commandId,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            var lease = new FakeProjectionLease(rootActorId, commandId);
            var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct);
            return liveSinkLease == null
                ? null
                : new EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>(lease, liveSinkLease);
        }

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default)
        {
            DetachCalls.Add(liveSinkLease);
            if (liveSinkLease is FakeLiveSinkLease fakeLease)
            {
                fakeLease.ProjectionLease.LiveSinkAttached = false;
                return fakeLease.DisposeAsync().AsTask();
            }

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

    private sealed class FakeLiveSinkLease(FakeProjectionLease projectionLease) : IAsyncDisposable
    {
        public FakeProjectionLease ProjectionLease { get; } = projectionLease;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWorkflowRunActorPort : IWorkflowRunProvisioningPort, IWorkflowDefinitionParser
    {
        public List<string> DestroyCalls { get; } = [];
        public int ExpectedDestroyCount { get; set; } = int.MaxValue;
        public TaskCompletionSource<bool> DestroyCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? DestroyException { get; set; }
        public Task<WorkflowRunCreationReceipt> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
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
            string? scopeId = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task MarkStoppedAsync(
            string actorId,
            string runId,
            string reason,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
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
