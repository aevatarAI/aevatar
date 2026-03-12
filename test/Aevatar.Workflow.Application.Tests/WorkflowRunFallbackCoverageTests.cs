using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;
using ProtobufAny = Google.Protobuf.WellKnownTypes.Any;
using StringValue = Google.Protobuf.WellKnownTypes.StringValue;
using System.Runtime.CompilerServices;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunFallbackCoverageTests
{
    [Theory]
    [InlineData(false, "auto", false, false, false, false)]
    [InlineData(true, "auto", true, false, false, false)]
    [InlineData(true, "analysis", false, true, false, false)]
    [InlineData(true, null, false, false, false, false)]
    [InlineData(true, "direct", false, false, false, false)]
    [InlineData(true, "custom", false, false, false, false)]
    [InlineData(true, "auto", false, false, true, true)]
    public void WorkflowDirectFallbackPolicy_ShouldMatchExpectedConditions(
        bool enableFallback,
        string? workflowName,
        bool operationCanceled,
        bool hasInlineYamls,
        bool whitelistedException,
        bool expected)
    {
        var options = new WorkflowRunBehaviorOptions
        {
            EnableDirectFallback = enableFallback,
        };
        options.DirectFallbackWorkflowWhitelist.Clear();
        options.DirectFallbackWorkflowWhitelist.Add("auto");
        options.DirectFallbackExceptionWhitelist.Clear();
        if (whitelistedException)
            options.DirectFallbackExceptionWhitelist.Add(typeof(WorkflowDirectFallbackTriggerException));

        var policy = new WorkflowDirectFallbackPolicy(options);
        var request = new WorkflowChatRunRequest(
            "hello",
            workflowName,
            null,
            null,
            hasInlineYamls ? ["name: inline"] : null);
        Exception exception = operationCanceled
            ? new OperationCanceledException("cancelled")
            : whitelistedException
                ? new WorkflowDirectFallbackTriggerException("fallback")
                : new InvalidOperationException("boom");

        var result = policy.ShouldFallback(request, exception);

        result.Should().Be(expected);
    }

    [Fact]
    public void WorkflowDirectFallbackPolicy_ToFallbackRequest_ShouldRewriteWorkflowAndDropInlineYamls()
    {
        var policy = new WorkflowDirectFallbackPolicy();
        var request = new WorkflowChatRunRequest(
            "hello",
            "auto",
            "actor-1",
            SessionId: "session-1",
            WorkflowYamls: ["name: inline"]);

        var fallback = policy.ToFallbackRequest(request);

        fallback.WorkflowName.Should().Be(WorkflowRunBehaviorOptions.DirectWorkflowName);
        fallback.WorkflowYamls.Should().BeNull();
        fallback.Prompt.Should().Be(request.Prompt);
        fallback.ActorId.Should().BeNull();
        fallback.SessionId.Should().Be(request.SessionId);
    }

    [Fact]
    public void WorkflowDirectFallbackPolicy_ShouldUseEffectiveWorkflow_WhenRequestOmitsWorkflowName()
    {
        var options = new WorkflowRunBehaviorOptions
        {
            EnableDirectFallback = true,
            UseAutoAsDefaultWhenWorkflowUnspecified = true,
        };
        options.DirectFallbackWorkflowWhitelist.Clear();
        options.DirectFallbackWorkflowWhitelist.Add(WorkflowRunBehaviorOptions.AutoWorkflowName);
        options.DirectFallbackExceptionWhitelist.Clear();
        options.DirectFallbackExceptionWhitelist.Add(typeof(WorkflowDirectFallbackTriggerException));

        var policy = new WorkflowDirectFallbackPolicy(options);

        var shouldFallback = policy.ShouldFallback(
            new WorkflowChatRunRequest("hello", WorkflowName: null, ActorId: "actor-1"),
            new WorkflowDirectFallbackTriggerException("fallback"));

        shouldFallback.Should().BeTrue();
    }

    [Fact]
    public async Task WorkflowRunInteractionService_ShouldRetryWithDirect_WhenFallbackEligibleExceptionOccurs()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var target = CreateBoundTarget(projectionPort, actorPort, receipt.ActorId, receipt.WorkflowName, receipt.CommandId);
        var pipeline = new SequencedDispatchPipeline();
        pipeline.EnqueueException(new WorkflowDirectFallbackTriggerException("retry"));
        pipeline.EnqueueResult(CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>.Success(
            new CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>
            {
                Target = target,
                Context = new CommandContext(receipt.ActorId, receipt.CommandId, receipt.CorrelationId, new Dictionary<string, string>()),
                Envelope = new EventEnvelope { Id = "evt-1" },
                Receipt = receipt,
            }));

        var service = new WorkflowRunInteractionService(
            pipeline,
            new FakeWorkflowRunOutputStreamer
            {
                Events =
                [
                    new WorkflowRunEventEnvelope
                    {
                        RunFinished = new WorkflowRunFinishedEventPayload
                        {
                            ThreadId = receipt.ActorId,
                            Result = ProtobufAny.Pack(new StringValue { Value = "done" }),
                        },
                    },
                ],
            },
            new FakeWorkflowRunCompletionPolicy(),
            new FakeWorkflowRunStateSnapshotEmitter(),
            new FakeWorkflowRunDurableCompletionResolver(),
            new WorkflowDirectFallbackPolicy(),
            logger: null);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", "auto", "actor-requested"),
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        pipeline.Requests.Select(static x => x.WorkflowName).Should().Equal("auto", "direct");
        pipeline.Requests.Select(static x => x.ActorId).Should().Equal("actor-requested", null);
        actorPort.DestroyCalls.Should().ContainSingle().Which.Should().Be("actor-1");
    }

    [Fact]
    public async Task WorkflowRunInteractionService_ShouldKeepActorsAlive_WhenProjectionReportsCustomFailureOnly()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "auto", "cmd-1", "corr-1");
        var target = CreateBoundTarget(projectionPort, actorPort, receipt.ActorId, receipt.WorkflowName, receipt.CommandId);
        var pipeline = new SequencedDispatchPipeline();
        pipeline.EnqueueResult(CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>.Success(
            new CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>
            {
                Target = target,
                Context = new CommandContext(receipt.ActorId, receipt.CommandId, receipt.CorrelationId, new Dictionary<string, string>()),
                Envelope = new EventEnvelope { Id = "evt-1" },
                Receipt = receipt,
            }));

        var service = new WorkflowRunInteractionService(
            pipeline,
            new FakeWorkflowRunOutputStreamer
            {
                Events =
                [
                    new WorkflowRunEventEnvelope
                    {
                        Custom = new WorkflowCustomEventPayload { Name = "aevatar.projection.sink.failure" },
                    },
                ],
            },
            new WorkflowRunCompletionPolicy(),
            new FakeWorkflowRunStateSnapshotEmitter(),
            new FakeWorkflowRunDurableCompletionResolver(),
            new WorkflowDirectFallbackPolicy(),
            logger: null);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", "auto", "actor-requested"),
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FinalizeResult.Should().NotBeNull();
        result.FinalizeResult!.ProjectionCompleted.Should().BeFalse();
        result.FinalizeResult.ProjectionCompletionStatus.Should().Be(WorkflowProjectionCompletionStatus.Unknown);
        actorPort.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunDetachedDispatchService_ShouldRetryWithDirect_WhenFallbackEligibleExceptionOccurs()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var sink = new FakeEventSink();
        var target = CreateBoundTarget(projectionPort, actorPort, receipt.ActorId, receipt.WorkflowName, receipt.CommandId, sink);
        var pipeline = new SequencedDispatchPipeline();
        pipeline.EnqueueException(new WorkflowDirectFallbackTriggerException("retry"));
        pipeline.EnqueueResult(CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>.Success(
            new CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>
            {
                Target = target,
                Context = new CommandContext(receipt.ActorId, receipt.CommandId, receipt.CorrelationId, new Dictionary<string, string>()),
                Envelope = new EventEnvelope { Id = "evt-1" },
                Receipt = receipt,
            }));
        var durableCompletionResolver = new FakeWorkflowRunDurableCompletionResolver();
        durableCompletionResolver.EnqueueResult(new WorkflowRunDurableCompletionObservation(true, WorkflowProjectionCompletionStatus.Completed));
        var service = new WorkflowRunDetachedDispatchService(
            pipeline,
            durableCompletionResolver,
            new WorkflowDirectFallbackPolicy(),
            logger: null);

        var result = await service.DispatchAsync(
            new WorkflowChatRunRequest("hello", "auto", "actor-requested"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        pipeline.Requests.Select(static x => x.WorkflowName).Should().Equal("auto", "direct");
        pipeline.Requests.Select(static x => x.ActorId).Should().Equal("actor-requested", null);
        await actorPort.Destroyed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        actorPort.DestroyCalls.Should().ContainSingle().Which.Should().Be("actor-1");
        projectionPort.Events.Should().Equal("detach:actor-1", "release:actor-1");
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task WorkflowRunDetachedDispatchService_ShouldDetachLiveObservation_BeforeDurableCompletionCleanup()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "auto", "cmd-1", "corr-1");
        var sink = new FakeEventSink();
        var target = CreateBoundTarget(projectionPort, actorPort, receipt.ActorId, receipt.WorkflowName, receipt.CommandId, sink);
        var pipeline = new SequencedDispatchPipeline();
        pipeline.EnqueueResult(CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>.Success(
            new CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>
            {
                Target = target,
                Context = new CommandContext(receipt.ActorId, receipt.CommandId, receipt.CorrelationId, new Dictionary<string, string>()),
                Envelope = new EventEnvelope { Id = "evt-1" },
                Receipt = receipt,
            }));
        var durableCompletionResolver = new FakeWorkflowRunDurableCompletionResolver();
        var terminalResult = durableCompletionResolver.EnqueuePendingResult();
        var service = new WorkflowRunDetachedDispatchService(
            pipeline,
            durableCompletionResolver,
            new WorkflowDirectFallbackPolicy(),
            logger: null);

        var result = await service.DispatchAsync(
            new WorkflowChatRunRequest("hello", "auto", "actor-requested"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        await projectionPort.Detached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await durableCompletionResolver.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        target.LiveSink.Should().BeNull();
        target.ProjectionLease.Should().NotBeNull();
        actorPort.DestroyCalls.Should().BeEmpty();
        projectionPort.Events.Should().Equal("detach:actor-1");
        sink.DisposeCalls.Should().Be(1);

        terminalResult.TrySetResult(new WorkflowRunDurableCompletionObservation(true, WorkflowProjectionCompletionStatus.Completed));

        await actorPort.Destroyed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        projectionPort.Events.Should().Equal("detach:actor-1", "release:actor-1");
    }

    private static WorkflowRunCommandTarget CreateBoundTarget(
        FakeProjectionPort projectionPort,
        FakeWorkflowRunActorPort actorPort,
        string actorId,
        string workflowName,
        string commandId,
        IEventSink<WorkflowRunEventEnvelope>? sink = null)
    {
        var target = new WorkflowRunCommandTarget(
            new FakeActor(actorId),
            workflowName,
            [actorId],
            projectionPort,
            actorPort);
        target.BindLiveObservation(new FakeProjectionLease(actorId, commandId), sink ?? new FakeEventSink());
        return target;
    }

    private sealed class SequencedDispatchPipeline
        : ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        private readonly Queue<object> _results = new();

        public List<WorkflowChatRunRequest> Requests { get; } = [];

        public void EnqueueException(Exception ex) => _results.Enqueue(ex);

        public void EnqueueResult(CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError> result) =>
            _results.Enqueue(result);

        public Task<CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            Requests.Add(command);
            var next = _results.Dequeue();
            if (next is Exception ex)
                throw ex;

            return Task.FromResult((CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>)next);
        }
    }

    private sealed class FakeWorkflowRunOutputStreamer : IWorkflowRunOutputStreamer
    {
        public IReadOnlyList<WorkflowRunEventEnvelope> Events { get; set; } = [];
        public TaskCompletionSource<bool> StreamCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StreamAsync(
            IEventSink<WorkflowRunEventEnvelope> sink,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            CancellationToken ct = default)
        {
            _ = sink;
            try
            {
                foreach (var evt in Events)
                    await emitAsync(evt, ct);

                StreamCompleted.TrySetResult(true);
            }
            catch (Exception ex)
            {
                StreamCompleted.TrySetException(ex);
                throw;
            }
        }

        public WorkflowRunEventEnvelope Map(WorkflowRunEventEnvelope evt) => evt;
    }

    private sealed class FakeWorkflowRunCompletionPolicy : IWorkflowRunCompletionPolicy
    {
        public bool TryResolve(WorkflowRunEventEnvelope evt, out WorkflowProjectionCompletionStatus status)
        {
            status = evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunFinished
                ? WorkflowProjectionCompletionStatus.Completed
                : WorkflowProjectionCompletionStatus.Unknown;
            return evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunFinished;
        }
    }

    private sealed class FakeWorkflowRunStateSnapshotEmitter : IWorkflowRunStateSnapshotEmitter
    {
        public Task EmitAsync(
            WorkflowChatRunAcceptedReceipt receipt,
            WorkflowProjectionCompletionStatus projectionCompletionStatus,
            bool projectionCompleted,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            CancellationToken ct = default)
        {
            _ = receipt;
            _ = projectionCompletionStatus;
            _ = projectionCompleted;
            _ = emitAsync;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkflowRunDurableCompletionResolver : IWorkflowRunDurableCompletionResolver
    {
        private readonly Queue<Func<CancellationToken, Task<WorkflowRunDurableCompletionObservation>>> _responses = new();

        public TaskCompletionSource<bool> Invoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void EnqueueResult(WorkflowRunDurableCompletionObservation observation) =>
            _responses.Enqueue(_ => Task.FromResult(observation));

        public TaskCompletionSource<WorkflowRunDurableCompletionObservation> EnqueuePendingResult()
        {
            var pending = new TaskCompletionSource<WorkflowRunDurableCompletionObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
            _responses.Enqueue(ct => pending.Task.WaitAsync(ct));
            return pending;
        }

        public Task<WorkflowRunDurableCompletionObservation> ResolveAsync(
            string actorId,
            CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            Invoked.TrySetResult(true);
            if (_responses.Count == 0)
                return Task.FromResult(WorkflowRunDurableCompletionObservation.Incomplete);

            return _responses.Dequeue()(ct);
        }
    }

    private sealed class FakeProjectionPort : IWorkflowExecutionProjectionLifecyclePort
    {
        public bool ProjectionEnabled => true;
        public TaskCompletionSource<bool> Detached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Events { get; } = [];

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
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DetachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = sink;
            ct.ThrowIfCancellationRequested();
            Events.Add($"detach:{lease.ActorId}");
            Detached.TrySetResult(true);
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IWorkflowExecutionProjectionLease lease,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Events.Add($"release:{lease.ActorId}");
            Released.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkflowRunActorPort : IWorkflowRunActorPort
    {
        public List<string> DestroyCalls { get; } = [];
        public TaskCompletionSource<bool> Destroyed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default)
        {
            DestroyCalls.Add(actorId);
            Destroyed.TrySetResult(true);
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

    private sealed class FakeProjectionLease(string actorId, string commandId) : IWorkflowExecutionProjectionLease
    {
        public string ActorId { get; } = actorId;
        public string CommandId { get; } = commandId;
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

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
