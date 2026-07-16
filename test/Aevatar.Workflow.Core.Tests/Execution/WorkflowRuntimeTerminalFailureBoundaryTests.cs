using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowRuntimeTerminalFailureBoundaryTests
{
    [Fact]
    public void RuntimeFailureMessages_ShouldSanitizeExceptionSummary()
    {
        var ex = new InvalidOperationException(
            "Authorization: Bearer secret-token-123\n" +
            "at Namespace.Type.Method() in /tmp/source.cs:line 42\n" +
            "payload={\"token\":\"abc.def.ghi\",\"value\":\"large\"}");

        var message = WorkflowRuntimeFailureMessages.StepExecutorFailed(
            "step-1",
            "tool_call",
            ex);

        message.Should().StartWith("step_executor_failed: step 'step-1' (tool_call) failed during executor: ");
        message.Should().NotContain("secret-token-123");
        message.Should().NotContain("abc.def.ghi");
        message.Should().NotContain("Bearer");
        message.Should().NotContain("Authorization");
        message.Should().NotContain("payload=");
        message.Should().NotContain(" at ");
        message.Should().NotContain("\n");
        message.Length.Should().BeLessThanOrEqualTo(240);
    }

    [Fact]
    public void RuntimeFailureMessages_ShouldFallbackToExceptionType_WhenSummaryIsBlank()
    {
        var message = WorkflowRuntimeFailureMessages.StartDispatchFailed(new InvalidOperationException("   "));

        message.Should().Be("start_dispatch_failed: failed during start_dispatch: InvalidOperationException");
    }

    [Fact]
    public void RuntimeFailureMessages_ShouldSanitizeCompletionStepFallbackAndTruncateLongTokens()
    {
        var longStepId = new string('s', 90);
        var completion = new StepCompletedEvent
        {
            StepId = "  step with spaces'and quote  ",
        };
        var step = new StepDefinition
        {
            Id = longStepId,
            Type = "custom type'with quote",
        };

        var message = WorkflowRuntimeFailureMessages.StepCompletionHandlingFailed(
            step,
            completion,
            new InvalidOperationException(new string('x', 180)));

        message.Should().StartWith(
            "step_completion_handling_failed: step 'step_with_spacesand_quote' (custom_typewith_quote) failed during completion: ");
        message.Should().NotContain("spaces'and");
        message.Should().NotContain("type'with");
        message.Length.Should().BeLessThanOrEqualTo(240);
    }

    [Fact]
    public void RuntimeFailureMessages_ShouldUseDefinitionStep_WhenCompletionStepIdIsBlank()
    {
        var message = WorkflowRuntimeFailureMessages.StepCompletionHandlingFailed(
            new StepDefinition { Id = "definition-step", Type = "notify" },
            new StepCompletedEvent { StepId = " " },
            new InvalidOperationException("boom"));

        message.Should().Be(
            "step_completion_handling_failed: step 'definition-step' (notify) failed during completion: boom");
    }

    [Fact]
    public async Task Bridge_ShouldPublishFailedStepCompletion_WhenSelectedExecutorThrows()
    {
        var module = new WorkflowExecutionBridgeModule(
            [new ThrowingStepExecutor(), new RecordingStepExecutor()],
            new RecordingStateHost { RunId = "run-1" });
        var ctx = new RecordingEventHandlerContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                StepType = "tool_call",
                ExecutionId = "exec-1",
            }),
            ctx,
            CancellationToken.None);

        var failed = ctx.Published.Should().ContainSingle().Subject.Event
            .Unpack<StepCompletedEvent>();
        failed.RunId.Should().Be("run-1");
        failed.StepId.Should().Be("step-1");
        failed.ExecutionId.Should().Be("exec-1");
        failed.Success.Should().BeFalse();
        failed.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        failed.Error.Should().StartWith("step_executor_failed: step 'step-1' (tool_call) failed during executor: ");
        failed.Error.Should().NotContain("super-secret-token");
    }

    [Fact]
    public async Task Kernel_ShouldPublishTerminalFailure_WhenStepDispatchPublishFails()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition { Id = "step-1", Type = "notify" },
            ],
        };
        var host = new RecordingStateHost { RunId = "run-1" };
        var module = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext
        {
            FailPublish = evt => evt is StepRequestEvent,
        };

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-1",
                WorkflowName = "wf",
                Input = "hello",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .Where(x => x.Is(WorkflowCompletedEvent.Descriptor))
            .Select(x => x.Unpack<WorkflowCompletedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        completion.Success.Should().BeFalse();
        completion.Error.Should().StartWith("step_dispatch_failed: step 'step-1' (notify) failed during dispatch: ");
        completion.Error.Should().NotContain("super-secret-token");
    }

    [Fact]
    public async Task Kernel_ShouldQueryRunLedger_WhenCompensableStepDispatchPublishFailsBeforeExecutorReceipt()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "charge",
                    Type = "tool_call",
                    Compensation = "refund",
                },
                new StepDefinition
                {
                    Id = "refund",
                    Type = "tool_call",
                },
            ],
        };
        var host = new RecordingStateHost
        {
            RunId = "run-1",
            StartCompensationWhenLedgerRecorded = true,
        };
        var module = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext
        {
            FailPublish = evt => evt is StepRequestEvent,
        };

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-1",
                WorkflowName = "wf",
                Input = "hello",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .Where(x => x.Is(WorkflowCompletedEvent.Descriptor))
            .Select(x => x.Unpack<WorkflowCompletedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        completion.Success.Should().BeFalse();
        completion.Error.Should().StartWith("step_dispatch_failed: step 'charge' (tool_call) failed during dispatch: ");
        ctx.Published
            .Select(x => x.Event)
            .Should()
            .NotContain(x => x.Is(CompensationRequestEvent.Descriptor));
        host.CompensationStartAttempts.Should().Be(1);
        host.TerminalStepAttempts.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task Kernel_ShouldCompensatePreviouslyCompletedStep_WhenNextStepDispatchPublishFails()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "charge",
                    Type = "tool_call",
                    Compensation = "refund",
                },
                new StepDefinition { Id = "notify", Type = "notify" },
                new StepDefinition { Id = "refund", Type = "tool_call" },
            ],
        };
        var host = new RecordingStateHost
        {
            RunId = "run-1",
            StartCompensationWhenLedgerRecorded = true,
        };
        var module = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext
        {
            FailPublish = evt => evt is StepRequestEvent { StepId: "notify" },
        };

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-1",
                WorkflowName = "wf",
                Input = "hello",
            }),
            ctx,
            CancellationToken.None);

        var chargeRequest = ctx.Published
            .Select(x => x.Event)
            .Where(x => x.Is(StepRequestEvent.Descriptor))
            .Select(x => x.Unpack<StepRequestEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                RunId = "run-1",
                StepId = "charge",
                ExecutionId = chargeRequest.ExecutionId,
                Success = true,
                Output = "charged",
            }),
            ctx,
            CancellationToken.None);

        host.CompensationStartAttempts.Should().Be(1);
        host.CompensableDispatches.Should().ContainSingle()
            .Which.StepId.Should().Be("charge");
        ctx.Published.Select(x => x.Event)
            .Should()
            .ContainSingle(x => x.Is(CompensationRequestEvent.Descriptor));
    }

    [Fact]
    public async Task Kernel_ShouldEnterCompensationDecision_WhenCompensableDispatchFailsAfterExecutorReceipt()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "charge",
                    Type = "tool_call",
                    Compensation = "refund",
                },
                new StepDefinition
                {
                    Id = "refund",
                    Type = "tool_call",
                },
            ],
        };
        var host = new RecordingStateHost
        {
            RunId = "run-1",
            FailRecordCompensableDispatch = true,
        };
        var module = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext();

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-1",
                WorkflowName = "wf",
                Input = "hello",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .Where(x => x.Is(WorkflowCompletedEvent.Descriptor))
            .Select(x => x.Unpack<WorkflowCompletedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        completion.Success.Should().BeFalse();
        completion.Error.Should().StartWith("step_dispatch_failed: step 'charge' (tool_call) failed during dispatch: ");
        host.CompensationStartAttempts.Should().Be(1);
        var terminalStep = host.TerminalStepAttempts.Should()
            .ContainSingle()
            .Subject;
        terminalStep.Should().NotBeNull();
        terminalStep!.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task Kernel_ShouldIgnoreDuplicateFailedCompletion_WhenRetryBackoffIsPending()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "notify",
                    Retry = new StepRetryPolicy
                    {
                        MaxAttempts = 3,
                        DelayMs = 800,
                    },
                },
            ],
        };
        var host = new RecordingStateHost { RunId = "run-1" };
        var module = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext();

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-1",
                WorkflowName = "wf",
                Input = "hello",
            }),
            ctx,
            CancellationToken.None);

        var firstRequest = ctx.Published
            .Select(x => x.Event)
            .Where(x => x.Is(StepRequestEvent.Descriptor))
            .Select(x => x.Unpack<StepRequestEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                Success = false,
                Error = "transient failure",
                ExecutionId = firstRequest.ExecutionId,
            }),
            ctx,
            CancellationToken.None);

        var stateWithBackoff = host.States["workflow_execution_kernel"].Unpack<WorkflowExecutionKernelState>();
        stateWithBackoff.RetryBackoffsByStepId.Should().ContainKey("step-1");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                Success = false,
                Error = "duplicate failure",
                ExecutionId = firstRequest.ExecutionId,
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(x => x.Event)
            .Should()
            .NotContain(x => x.Is(StepRequestEvent.Descriptor) || x.Is(WorkflowCompletedEvent.Descriptor));
        var finalState = host.States["workflow_execution_kernel"].Unpack<WorkflowExecutionKernelState>();
        finalState.RetryBackoffsByStepId.Should().ContainKey("step-1");
    }

    [Fact]
    public async Task Kernel_ShouldPublishTerminalFailure_WhenCompletionHandlingThrowsAfterRunIsActive()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition { Id = "step-1", Type = "notify" },
                new StepDefinition { Id = "step-2", Type = "notify" },
            ],
        };
        var host = new RecordingStateHost { RunId = "run-1" };
        var module = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext();

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-1",
                WorkflowName = "wf",
                Input = "hello",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();
        host.FailSave = true;

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                Success = true,
                Output = "next",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .Where(x => x.Is(WorkflowCompletedEvent.Descriptor))
            .Select(x => x.Unpack<WorkflowCompletedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        completion.Success.Should().BeFalse();
        completion.Error.Should().StartWith("step_completion_handling_failed: step 'step-1' (notify) failed during completion: ");
        completion.Error.Should().NotContain("super-secret-token");
    }

    private static EventEnvelope Envelope(IMessage payload) => new()
    {
        Id = "envelope-1",
        Payload = Any.Pack(payload),
    };

    private sealed class ThrowingStepExecutor : IEventModule<IWorkflowExecutionContext>
    {
        public string Name => "throwing_executor";

        public int Priority => 0;

        public bool CanHandle(EventEnvelope envelope) =>
            envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

        public Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct) =>
            throw new InvalidOperationException("executor failed with bearer super-secret-token");
    }

    private sealed class RecordingStepExecutor : IEventModule<IWorkflowExecutionContext>
    {
        public string Name => "recording_executor";

        public int Priority => 1;

        public bool CanHandle(EventEnvelope envelope) =>
            envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

        public Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct) =>
            ctx.PublishAsync(
                new StepCompletedEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    Success = true,
                },
                TopologyAudience.Self,
                ct);
    }

    private sealed class RecordingEventHandlerContext : IEventHandlerContext
    {
        public string AgentId { get; } = "agent-1";

        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = "inbound-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public IAgent Agent { get; } = new StubAgent("agent-1");

        public IServiceProvider Services { get; set; } = new NullServiceProvider();

        public ILogger Logger { get; set; } = NullLogger.Instance;

        public Func<IMessage, bool>? FailPublish { get; init; }

        public List<(Any Event, TopologyAudience Direction)> Published { get; } = [];

        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (FailPublish?.Invoke(evt) == true)
                throw new InvalidOperationException("publish failed with bearer super-secret-token");

            Published.Add((Any.Pack(evt), direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            Canceled.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStateHost : IWorkflowExecutionStateHost
    {
        public string RunId { get; init; } = "run-1";

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot { get; } = new();

        public bool FailSave { get; set; }

        public bool FailRecordCompensableDispatch { get; init; }

        public bool StartCompensationWhenLedgerRecorded { get; init; }

        public int CompensationStartAttempts { get; private set; }

        public List<CompensableStepDispatchedEvent> CompensableDispatches { get; } = [];

        public List<StepCompletedEvent?> TerminalStepAttempts { get; } = [];

        public Dictionary<string, Any> States { get; } = new(StringComparer.Ordinal);

        public Any? GetExecutionState(string scopeKey) =>
            States.GetValueOrDefault(scopeKey);

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
            States.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            if (FailSave)
                throw new InvalidOperationException("save failed with bearer super-secret-token");

            States[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            States.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Task UpdateExecutionContextAsync(WorkflowRunExecutionContextDelta delta, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ClearExecutionContextAsync(CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<WorkflowCompensationTransitionResult> TryStartCompensationAsync(
            WorkflowCompletedEvent terminalFailure,
            StepCompletedEvent? terminalStep,
            CancellationToken ct)
        {
            TerminalStepAttempts.Add(terminalStep?.Clone());
            CompensationStartAttempts++;
            if (StartCompensationWhenLedgerRecorded && CompensableDispatches.Count > 0)
            {
                var dispatch = CompensableDispatches[^1];
                return Task.FromResult(new WorkflowCompensationTransitionResult(
                    WorkflowCompensationTransitionStatus.Started,
                    dispatch.CompensationStepId,
                    terminalStep?.StepId ?? string.Empty,
                    dispatch.IdempotencyKey,
                    string.Empty,
                    "compensation-exec-1"));
            }

            return Task.FromResult(NoCompensableLedger());
        }

        public Task RecordCompensableStepDispatchAsync(CompensableStepDispatchedEvent evt, CancellationToken ct)
        {
            if (FailRecordCompensableDispatch)
                throw new InvalidOperationException("compensable dispatch record failed with bearer super-secret-token");

            CompensableDispatches.Add(evt);
            return Task.CompletedTask;
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
            CompensationStepCompletedEvent completion,
            CancellationToken ct = default) =>
            Task.FromResult(NoCompensableLedger());

        public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
            string runId,
            string error,
            CancellationToken ct = default) =>
            Task.FromResult(NoCompensableLedger());

        private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
            new(
                WorkflowCompensationTransitionStatus.NoCompensableLedger,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string> GetDescriptionAsync() =>
            Task.FromResult("stub");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
