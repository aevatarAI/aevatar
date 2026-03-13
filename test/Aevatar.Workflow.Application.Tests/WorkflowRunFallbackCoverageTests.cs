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
using ProtobufAny = Google.Protobuf.WellKnownTypes.Any;
using StringValue = Google.Protobuf.WellKnownTypes.StringValue;

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
    public async Task FallbackCommandInteractionService_ShouldRetryWithDirect_WhenFallbackEligibleExceptionOccurs()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var target = CreateBoundTarget(projectionPort, actorPort, receipt.ActorId, receipt.WorkflowName, receipt.CommandId);
        var pipeline = new SequencedDispatchPipeline();
        pipeline.EnqueueException(new WorkflowDirectFallbackTriggerException("retry"));
        pipeline.EnqueueResult(
            CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>.Success(
                new CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>
                {
                    Target = target,
                    Context = new CommandContext(receipt.ActorId, receipt.CommandId, receipt.CorrelationId, new Dictionary<string, string>()),
                    Envelope = new EventEnvelope { Id = "evt-1" },
                    Receipt = receipt,
                }));

        var service = new FallbackCommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>(
            new DefaultCommandInteractionService<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>(
                pipeline,
                new FakeEventOutputStream
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
                new FakeFinalizeEmitter(),
                new FakeDurableCompletionResolver()),
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
    public async Task FallbackCommandDispatchService_ShouldRetryWithDirect_WhenFallbackEligibleExceptionOccurs()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var target = CreateBoundTarget(projectionPort, actorPort, receipt.ActorId, receipt.WorkflowName, receipt.CommandId);
        var pipeline = new SequencedDispatchPipeline();
        var cleanupScheduler = new RecordingDetachedCleanupScheduler();
        pipeline.EnqueueException(new WorkflowDirectFallbackTriggerException("retry"));
        pipeline.EnqueueResult(
            CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>.Success(
                new CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>
                {
                    Target = target,
                    Context = new CommandContext(receipt.ActorId, receipt.CommandId, receipt.CorrelationId, new Dictionary<string, string>()),
                    Envelope = new EventEnvelope { Id = "evt-1" },
                    Receipt = receipt,
                }));

        var service = new FallbackCommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>(
            new WorkflowRunDetachedDispatchService(
                pipeline,
                cleanupScheduler,
                logger: null),
            new WorkflowDirectFallbackPolicy(),
            logger: null);

        var result = await service.DispatchAsync(
            new WorkflowChatRunRequest("hello", "auto", "actor-requested"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        pipeline.Requests.Select(static x => x.WorkflowName).Should().Equal("auto", "direct");
        pipeline.Requests.Select(static x => x.ActorId).Should().Equal("actor-requested", null);
        cleanupScheduler.Requests.Should().ContainSingle();
        cleanupScheduler.Requests.Single().ActorId.Should().Be("actor-1");
        cleanupScheduler.Requests.Single().WorkflowName.Should().Be("direct");
        cleanupScheduler.Requests.Single().CommandId.Should().Be("cmd-1");
        cleanupScheduler.Requests.Single().CreatedActorIds.Should().Equal("actor-1");
    }

    private static WorkflowRunCommandTarget CreateBoundTarget(
        FakeProjectionPort projectionPort,
        FakeWorkflowRunActorPort actorPort,
        string actorId,
        string workflowName,
        string commandId)
    {
        var target = new WorkflowRunCommandTarget(
            new FakeActor(actorId),
            workflowName,
            [actorId],
            projectionPort,
            actorPort);
        target.BindLiveObservation(new FakeProjectionLease(actorId, commandId), new EventChannel<WorkflowRunEventEnvelope>());
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

    private sealed class FakeEventOutputStream : IEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>
    {
        public IReadOnlyList<WorkflowRunEventEnvelope> Events { get; set; } = [];

        public async Task PumpAsync(
            IAsyncEnumerable<WorkflowRunEventEnvelope> events,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowRunEventEnvelope, bool>? shouldStop = null,
            CancellationToken ct = default)
        {
            _ = events;
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
        public WorkflowProjectionCompletionStatus IncompleteCompletion => WorkflowProjectionCompletionStatus.Unknown;

        public bool TryResolve(WorkflowRunEventEnvelope evt, out WorkflowProjectionCompletionStatus status)
        {
            status = evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunFinished
                ? WorkflowProjectionCompletionStatus.Completed
                : WorkflowProjectionCompletionStatus.Unknown;
            return evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunFinished;
        }
    }

    private sealed class FakeFinalizeEmitter : ICommandFinalizeEmitter<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus, WorkflowRunEventEnvelope>
    {
        public Task EmitAsync(
            WorkflowChatRunAcceptedReceipt receipt,
            WorkflowProjectionCompletionStatus completion,
            bool completed,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            CancellationToken ct = default)
        {
            _ = receipt;
            _ = completion;
            _ = completed;
            _ = emitAsync;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDurableCompletionResolver
        : ICommandDurableCompletionResolver<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>
    {
        private readonly CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus> _observation;

        public FakeDurableCompletionResolver(
            CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>? observation = null)
        {
            _observation = observation ?? CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete;
        }

        public Task<CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>> ResolveAsync(
            WorkflowChatRunAcceptedReceipt receipt,
            CancellationToken ct = default)
        {
            _ = receipt;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_observation);
        }
    }

    private sealed class RecordingDetachedCleanupScheduler : IWorkflowRunDetachedCleanupScheduler
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

    private sealed class FakeProjectionPort : IWorkflowExecutionProjectionPort
    {
        public bool ProjectionEnabled => true;

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
