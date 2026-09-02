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
            Prompt: "hello",
            Source: hasInlineYamls
                ? WorkflowChatSource.InlineYamlBundle(["name: inline"], workflowName)
                : string.IsNullOrWhiteSpace(workflowName)
                    ? WorkflowChatSource.Direct()
                    : WorkflowChatSource.CatalogWorkflow(workflowName),
            ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
            SessionId: null,
            InputParts: null);
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
            WorkflowChatSource.InlineYamlBundle(["name: inline"], "auto", "actor-1"),
            ExternalCapabilityExecutionMode.Interactive,
            SessionId: "session-1",
            Metadata: null);

        var fallback = policy.ToFallbackRequest(request);

        fallback.Source.Should().BeEquivalentTo(WorkflowChatSource.CatalogWorkflow(WorkflowRunBehaviorOptions.DirectWorkflowName));
        fallback.Prompt.Should().Be(request.Prompt);
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
            new WorkflowChatRunRequest("hello", WorkflowChatSource.DefinitionActor("actor-1"), ExternalCapabilityExecutionMode.Interactive),
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
                new FakeEventOutputStream(),
                new FakeWorkflowRunCompletionPolicy(),
                new FakeFinalizeEmitter(),
                new FakeDurableCompletionResolver()),
            new WorkflowDirectFallbackPolicy(),
            logger: null);

        target.RequireLiveSink().Push(new WorkflowRunEventEnvelope
        {
            RunFinished = new WorkflowRunFinishedEventPayload
            {
                ThreadId = receipt.ActorId,
                Result = ProtobufAny.Pack(new StringValue { Value = "done" }),
            },
        });
        target.RequireLiveSink().Complete();

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.DefinitionActor("actor-requested", "auto"), ExternalCapabilityExecutionMode.Interactive),
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        pipeline.Requests.Select(static x => x.Source.WorkflowName).Should().Equal("auto", "direct");
        pipeline.Requests.Select(static x => x.Source.ActorId).Should().Equal("actor-requested", null);
        await target.PendingReclaimTask;
        actorPort.DestroyCalls.Should().ContainSingle().Which.Should().Be("actor-1");
    }

    [Fact]
    public async Task FallbackCommandDispatchService_ShouldRetryWithDirect_WhenFallbackEligibleExceptionOccurs()
    {
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        var dispatchService = new SequencedCommandDispatchService();
        dispatchService.EnqueueException(new WorkflowDirectFallbackTriggerException("retry"));
        dispatchService.EnqueueResult(CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(receipt));

        var service = new FallbackCommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>(
            dispatchService,
            new WorkflowDirectFallbackPolicy(),
            logger: null);

        var result = await service.DispatchAsync(
            new WorkflowChatRunRequest("hello", WorkflowChatSource.DefinitionActor("actor-requested", "auto"), ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        dispatchService.Requests.Select(static x => x.Source.WorkflowName).Should().Equal("auto", "direct");
        dispatchService.Requests.Select(static x => x.Source.ActorId).Should().Equal("actor-requested", null);
    }

    private static WorkflowRunCommandTarget CreateBoundTarget(
        FakeProjectionPort projectionPort,
        FakeWorkflowRunActorPort actorPort,
        string actorId,
        string workflowName,
        string commandId)
    {
        var target = new WorkflowRunCommandTarget(
            actorId,
            workflowName,
            [actorId],
            projectionPort,
            actorPort,
            new WorkflowRunDurableCompletionResolver(new NoopCurrentStateQueryPort()),
            // 06-20-observatory-run-state-feed (R2): run the scheduled created-actor reclaim inline so the
            // destroy is observed deterministically within the test.
            detachedReclaimLauncher: reclaim => reclaim());
        target.BindLiveObservation(
            new FakeProjectionLease(actorId, commandId),
            new FakeLiveSinkLease(),
            new EventChannel<WorkflowRunEventEnvelope>());
        return target;
    }

    private sealed class SequencedCommandDispatchService
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        private readonly Queue<object> _results = new();

        public List<WorkflowChatRunRequest> Requests { get; } = [];

        public void EnqueueException(Exception ex) => _results.Enqueue(ex);

        public void EnqueueResult(CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> result) =>
            _results.Enqueue(result);

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(command);
            var next = _results.Dequeue();
            if (next is Exception ex)
                throw ex;

            return Task.FromResult((CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>)next);
        }
    }

    private sealed class SequencedDispatchPipeline
        : ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        private readonly Queue<object> _results = new();

        public List<WorkflowChatRunRequest> Requests { get; } = [];
        public List<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>> PreparedDispatches { get; } = [];

        public void EnqueueException(Exception ex) => _results.Enqueue(ex);

        public void EnqueueResult(CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError> result) =>
            _results.Enqueue(result);

        public Task<CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>> PrepareAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default) =>
            PrepareCoreAsync(command, ct);

        public Task<DispatchAdmission> DispatchPreparedAsync(
            CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt> execution,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(execution);
            ct.ThrowIfCancellationRequested();
            PreparedDispatches.Add(execution);
            return Task.FromResult(DispatchAdmissionFactory.Create(execution.Target.TargetId, execution.Envelope));
        }

        public Task<CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default) =>
            DispatchAsyncCore(command, ct);

        private async Task<CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>> DispatchAsyncCore(
            WorkflowChatRunRequest command,
            CancellationToken ct)
        {
            var prepared = await PrepareCoreAsync(command, ct);
            if (!prepared.Succeeded || prepared.Target == null)
                return prepared;

            var admission = await DispatchPreparedAsync(prepared.Target, ct);
            return CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>.Success(
                prepared.Target with { Admission = admission });
        }

        private Task<CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>> PrepareCoreAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(command);
            var next = _results.Dequeue();
            if (next is Exception ex)
                throw ex;

            return Task.FromResult((CommandTargetResolution<CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowChatRunStartError>)next);
        }
    }

    private sealed class FakeEventOutputStream : IEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>
    {
        public async Task PumpAsync(
            IAsyncEnumerable<WorkflowRunEventEnvelope> events,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowRunEventEnvelope, bool>? shouldStop = null,
            CancellationToken ct = default)
        {
            await foreach (var evt in events.WithCancellation(ct))
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

    private sealed class FakeProjectionPort
        : IWorkflowExecutionProjectionPort
    {
        public bool ProjectionEnabled => true;
        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable?>(null);

        public Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
            string rootActorId,
            string commandId,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default) =>
            Task.FromResult<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?>(null);

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReleaseActorProjectionAsync(
            IWorkflowExecutionProjectionLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeWorkflowRunActorPort : IWorkflowRunProvisioningPort
    {
        public List<string> DestroyCalls { get; } = [];
        public TaskCompletionSource<bool> Destroyed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WorkflowRunCreationReceipt> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
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
            string? scopeId = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task MarkStoppedAsync(
            string actorId,
            string runId,
            string reason,
            CancellationToken ct = default) =>
            Task.CompletedTask;

    }

    private sealed class FakeProjectionLease(string actorId, string commandId) : IWorkflowExecutionProjectionLease
    {
        public string ActorId { get; } = actorId;
        public string CommandId { get; } = commandId;
    }

    private sealed class FakeLiveSinkLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
