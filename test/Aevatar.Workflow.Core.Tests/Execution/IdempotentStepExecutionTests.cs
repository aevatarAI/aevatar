using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class IdempotentStepExecutionTests
{
    private static WorkflowDefinition SingleStepWorkflow(string stepId = "step-1") => new()
    {
        Name = "test-workflow",
        Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
        Steps = [new StepDefinition { Id = stepId, Type = "llm_call", TargetRole = "worker" }],
    };

    private static EventEnvelope Wrap(IMessage msg) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(msg),
    };

    [Fact]
    public async Task StepRequest_ShouldContainExecutionId()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        var request = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .FirstOrDefault(r => r.StepId == "step-1");

        request.Should().NotBeNull();
        request!.ExecutionId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task StepCompleted_MatchingId_ShouldAccept()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        // Start workflow → dispatches step-1
        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        var executionId = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .First(r => r.StepId == "step-1")
            .ExecutionId;

        ctx.Published.Clear();

        // Complete step-1 with matching execution_id → should be accepted
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = true,
                Output = "done",
                ExecutionId = executionId,
            }),
            ctx, CancellationToken.None);

        // Should NOT have published a StaleStepCompletionRejectedEvent
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StaleStepCompletionRejectedEvent.Descriptor))
            .Should().BeEmpty();

        // Should have published a WorkflowCompletedEvent (single-step workflow done)
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Should().NotBeEmpty();
    }

    [Fact]
    public async Task StepCompleted_MismatchId_ShouldReject()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        ctx.Published.Clear();

        // Complete step-1 with WRONG execution_id → should be rejected
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = true,
                Output = "stale",
                ExecutionId = "wrong-execution-id",
            }),
            ctx, CancellationToken.None);

        var rejection = ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StaleStepCompletionRejectedEvent.Descriptor))
            .Select(e => e.Unpack<StaleStepCompletionRejectedEvent>())
            .FirstOrDefault();

        rejection.Should().NotBeNull();
        rejection!.StepId.Should().Be("step-1");
        rejection.ReceivedExecutionId.Should().Be("wrong-execution-id");

        // Should NOT have completed the workflow
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task StepCompleted_EmptyId_ShouldAcceptForBackwardsCompat()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        ctx.Published.Clear();

        // Complete step-1 with empty execution_id (backwards-compatible old worker)
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = true,
                Output = "done",
                ExecutionId = "",
            }),
            ctx, CancellationToken.None);

        // Should NOT reject — empty execution_id is backwards compatible
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StaleStepCompletionRejectedEvent.Descriptor))
            .Should().BeEmpty();

        // Should complete
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Should().NotBeEmpty();
    }

    [Fact]
    public async Task StaleCompletion_ShouldPublishRejectionEvent()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        var executionId = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .First(r => r.StepId == "step-1")
            .ExecutionId;

        ctx.Published.Clear();

        // Send stale completion
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = true,
                Output = "stale",
                ExecutionId = "old-execution-id",
            }),
            ctx, CancellationToken.None);

        var rejection = ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StaleStepCompletionRejectedEvent.Descriptor))
            .Select(e => e.Unpack<StaleStepCompletionRejectedEvent>())
            .Single();

        rejection.StepId.Should().Be("step-1");
        rejection.ExpectedExecutionId.Should().Be(executionId);
        rejection.ReceivedExecutionId.Should().Be("old-execution-id");
    }

    [Fact]
    public async Task StepRetry_ShouldGenerateNewExecutionId()
    {
        // Workflow with retry policy
        var workflow = new WorkflowDefinition
        {
            Name = "test-retry",
            Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "llm_call",
                    TargetRole = "worker",
                    Retry = new StepRetryPolicy { MaxAttempts = 3, Backoff = "fixed", DelayMs = 0 },
                },
            ],
        };

        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(workflow, host);

        // Start workflow → first dispatch
        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        var firstExecutionId = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .First(r => r.StepId == "step-1")
            .ExecutionId;

        ctx.Published.Clear();

        // Fail step-1 → triggers retry (delayMs=0 → immediate re-dispatch)
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = false,
                Error = "transient error",
                ExecutionId = firstExecutionId,
            }),
            ctx, CancellationToken.None);

        var secondRequest = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .FirstOrDefault(r => r.StepId == "step-1");

        secondRequest.Should().NotBeNull("retry with delayMs=0 should immediately re-dispatch");
        secondRequest!.ExecutionId.Should().NotBeNullOrEmpty();
        secondRequest.ExecutionId.Should().NotBe(firstExecutionId, "retry must generate a new execution_id");
    }

    // ──── Test infrastructure ────

    private sealed class RecordingEventHandlerContext : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public IAgent Agent { get; } = new StubAgent("agent-1");

        public IServiceProvider Services { get; } = new NullServiceProvider();

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(Any Event, TopologyAudience Direction)> Published { get; } = [];

        public List<(string TargetActorId, Any Event)> Sent { get; } = [];

        public List<RecordedCallback> ScheduledTimeouts { get; } = [];

        public List<RecordedTimer> ScheduledTimers { get; } = [];

        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((Any.Pack(evt), direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add((targetActorId, Any.Pack(evt)));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScheduledTimeouts.Add(new RecordedCallback(callbackId, dueTime, Any.Pack(evt), options));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScheduledTimers.Add(new RecordedTimer(callbackId, dueTime, period, Any.Pack(evt), options));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 2, RuntimeCallbackBackend.InMemory));
        }

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Canceled.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStateHost : IWorkflowExecutionStateHost
    {
        public string RunId { get; set; } = "run-1";

        public Dictionary<string, Any> States { get; } = new(StringComparer.Ordinal);

        public Any? GetExecutionState(string scopeKey) =>
            States.TryGetValue(scopeKey, out var state) ? state : null;

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
            States.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            States[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            States.Remove(scopeKey);
            return Task.CompletedTask;
        }
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }

    internal record RecordedCallback(string CallbackId, TimeSpan DueTime, Any Event, EventEnvelopePublishOptions? Options);
    internal record RecordedTimer(string CallbackId, TimeSpan DueTime, TimeSpan Period, Any Event, EventEnvelopePublishOptions? Options);
}
