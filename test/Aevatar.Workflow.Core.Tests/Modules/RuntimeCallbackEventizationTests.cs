using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;

namespace Aevatar.Workflow.Core.Tests.Modules;

public class RuntimeCallbackEventizationTests
{
    [Fact]
    public async Task DelayModule_ShouldCompleteOnlyOnMatchingGeneration()
    {
        var module = new DelayModule();
        var ctx = new SchedulingContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "delay-step",
                StepType = "delay",
                RunId = "run-1",
                Input = "payload",
                Parameters = { ["duration_ms"] = "1200" },
            }),
            ctx,
            CancellationToken.None);

        ctx.Scheduled.Should().ContainSingle();
        ctx.Published.Should().NotContain(x => x.Event is StepCompletedEvent);
        var scheduled = ctx.Scheduled.Single();

        await module.HandleAsync(
            Wrap(
                new DelayStepTimeoutFiredEvent
                {
                    RunId = "run-1",
                    StepId = "delay-step",
                    DurationMs = 1200,
                },
                MetadataFor(scheduled with { CallbackId = "other-callback" })),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().NotContain(x => x.Event is StepCompletedEvent);

        await module.HandleAsync(
            Wrap(
                new DelayStepTimeoutFiredEvent
                {
                    RunId = "run-1",
                    StepId = "delay-step",
                    DurationMs = 1200,
                },
                MetadataFor(scheduled, generation: scheduled.Generation - 1)),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().NotContain(x => x.Event is StepCompletedEvent);

        await module.HandleAsync(
            Wrap(
                new DelayStepTimeoutFiredEvent
                {
                    RunId = "run-1",
                    StepId = "delay-step",
                    DurationMs = 1200,
                },
                MetadataFor(scheduled)),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published
            .Select(x => x.Event)
            .OfType<StepCompletedEvent>()
            .Single();
        completed.Success.Should().BeTrue();
        completed.RunId.Should().Be("run-1");
        completed.StepId.Should().Be("delay-step");
        completed.Output.Should().Be("payload");
    }

    [Fact]
    public async Task WaitSignalModule_ShouldIgnoreStaleTimeoutWithoutDroppingPending()
    {
        var module = new WaitSignalModule();
        var ctx = new SchedulingContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "wait-1",
                StepType = "wait_signal",
                RunId = "run-1",
                Input = "default-output",
                Parameters =
                {
                    ["signal_name"] = "approve",
                    ["timeout_ms"] = "5000",
                },
            }),
            ctx,
            CancellationToken.None);

        var scheduled = ctx.Scheduled.Single(x => x.Event is WaitSignalTimeoutFiredEvent);
        ctx.Published.Clear();

        await module.HandleAsync(
            Wrap(
                new WaitSignalTimeoutFiredEvent
                {
                    RunId = "run-1",
                    StepId = "wait-1",
                    SignalName = "approve",
                    TimeoutMs = 5000,
                },
                MetadataFor(scheduled, generation: scheduled.Generation - 1)),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().NotContain(x => x.Event is StepCompletedEvent);

        await module.HandleAsync(
            Wrap(new SignalReceivedEvent
            {
                RunId = "run-1",
                StepId = "wait-1",
                SignalName = "approve",
                Payload = "approved",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .OfType<StepCompletedEvent>()
            .Single();
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be("approved");

        ctx.Canceled.Should().ContainSingle(x =>
            x.CallbackId == scheduled.CallbackId &&
            x.ExpectedGeneration == scheduled.Generation);
    }

    [Fact]
    public async Task WorkflowLoop_ShouldReplayTimeoutCompletion_WhenPublishFailsTransiently()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "llm_call",
                    TimeoutMs = 800,
                },
            ],
        });
        var ctx = new SchedulingContext
        {
            FailPublishOnce = evt => evt is StepCompletedEvent completed &&
                                     string.Equals(completed.StepId, "step-1", StringComparison.Ordinal) &&
                                     completed.Error.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase),
        };

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf",
                RunId = "run-timeout-replay",
                Input = "input-v1",
            }),
            ctx,
            CancellationToken.None);
        var scheduled = ctx.Scheduled.Single(x => x.Event is WorkflowStepTimeoutFiredEvent);
        ctx.Published.Clear();

        var timeoutEnvelope = Wrap(
            new WorkflowStepTimeoutFiredEvent
            {
                RunId = "run-timeout-replay",
                StepId = "step-1",
                TimeoutMs = 800,
            },
            MetadataFor(scheduled));

        await FluentActions
            .Invoking(() => module.HandleAsync(timeoutEnvelope, ctx, CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("transient publish failure");

        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(timeoutEnvelope, ctx, CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .OfType<StepCompletedEvent>()
            .Single();
        completion.Success.Should().BeFalse();
        completion.Error.Should().Contain("TIMEOUT");
    }

    [Fact]
    public async Task WorkflowLoop_ShouldScheduleRetryBackoffAndDispatchOnMatchingGeneration()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "transform",
                    Retry = new StepRetryPolicy
                    {
                        MaxAttempts = 3,
                        Backoff = "fixed",
                        DelayMs = 800,
                    },
                },
            ],
        });
        var ctx = new SchedulingContext();

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf",
                RunId = "run-1",
                Input = "input-v1",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();
        ctx.Scheduled.Clear();

        await module.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = false,
                Error = "boom",
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().NotContain(x => x.Event is StepRequestEvent);
        var scheduled = ctx.Scheduled.Single(x => x.Event is WorkflowStepRetryBackoffFiredEvent);
        (scheduled.Event as WorkflowStepRetryBackoffFiredEvent)!.NextAttempt.Should().Be(2);

        await module.HandleAsync(
            Wrap(
                new WorkflowStepRetryBackoffFiredEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    DelayMs = 800,
                    NextAttempt = 2,
                },
                MetadataFor(scheduled, generation: scheduled.Generation - 1)),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().NotContain(x => x.Event is StepRequestEvent);

        await module.HandleAsync(
            Wrap(
                new WorkflowStepRetryBackoffFiredEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    DelayMs = 800,
                    NextAttempt = 2,
                },
                MetadataFor(scheduled)),
            ctx,
            CancellationToken.None);

        var retryStepRequest = ctx.Published
            .Select(x => x.Event)
            .OfType<StepRequestEvent>()
            .Single();
        retryStepRequest.RunId.Should().Be("run-1");
        retryStepRequest.StepId.Should().Be("step-1");
        retryStepRequest.Input.Should().Be("input-v1");
    }

    [Fact]
    public async Task WorkflowLoop_ShouldReplayRetryBackoff_WhenRedispatchFailsTransiently()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "transform",
                    TimeoutMs = 1500,
                    Retry = new StepRetryPolicy
                    {
                        MaxAttempts = 3,
                        Backoff = "fixed",
                        DelayMs = 800,
                    },
                },
            ],
        });
        var ctx = new SchedulingContext();

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf",
                RunId = "run-retry-replay",
                Input = "input-v1",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();
        ctx.Scheduled.Clear();

        await module.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-retry-replay",
                Success = false,
                Error = "boom",
            }),
            ctx,
            CancellationToken.None);

        var scheduled = ctx.Scheduled.Single(x => x.Event is WorkflowStepRetryBackoffFiredEvent);
        ctx.Published.Clear();
        ctx.Canceled.Clear();
        ctx.FailPublishOnce = evt => evt is StepRequestEvent request &&
                                     string.Equals(request.StepId, "step-1", StringComparison.Ordinal);

        var backoffEnvelope = Wrap(
            new WorkflowStepRetryBackoffFiredEvent
            {
                RunId = "run-retry-replay",
                StepId = "step-1",
                DelayMs = 800,
                NextAttempt = 2,
            },
            MetadataFor(scheduled));

        await FluentActions
            .Invoking(() => module.HandleAsync(backoffEnvelope, ctx, CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("transient publish failure");

        ctx.Published.Should().BeEmpty();
        ctx.Canceled.Should().ContainSingle(x =>
            x.CallbackId == "workflow-step-timeout:run-retry-replay:step-1" &&
            x.ExpectedGeneration == 2);

        await module.HandleAsync(backoffEnvelope, ctx, CancellationToken.None);

        var retryStepRequest = ctx.Published
            .Select(x => x.Event)
            .OfType<StepRequestEvent>()
            .Single();
        retryStepRequest.RunId.Should().Be("run-retry-replay");
        retryStepRequest.StepId.Should().Be("step-1");
        retryStepRequest.Input.Should().Be("input-v1");
    }

    private static EventEnvelope Wrap(
        IMessage evt,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = "test",
            Direction = EventDirection.Self,
        };

        if (metadata != null)
        {
            foreach (var pair in metadata)
                envelope.Metadata[pair.Key] = pair.Value;
        }

        return envelope;
    }

    private static IReadOnlyDictionary<string, string> MetadataFor(
        ScheduledCallback callback,
        long? generation = null,
        long fireIndex = 0) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RuntimeCallbackMetadataKeys.CallbackId] = callback.CallbackId,
            [RuntimeCallbackMetadataKeys.CallbackGeneration] = (generation ?? callback.Generation)
                .ToString(CultureInfo.InvariantCulture),
            [RuntimeCallbackMetadataKeys.CallbackFireIndex] = fireIndex.ToString(CultureInfo.InvariantCulture),
            [RuntimeCallbackMetadataKeys.CallbackFiredAtUnixTimeMs] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                .ToString(CultureInfo.InvariantCulture),
        };

    private sealed class SchedulingContext : IEventHandlerContext
    {
        private readonly Dictionary<string, long> _generations = new(StringComparer.Ordinal);
        private bool _publishFailureConsumed;

        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public IAgent Agent { get; } = new StubWorkflowRunAgent("agent-1", "test-run");

        public IServiceProvider Services { get; } = new NullServiceProvider();

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(IMessage Event, EventDirection Direction)> Published { get; } = [];

        public List<ScheduledCallback> Scheduled { get; } = [];

        public List<CanceledCallback> Canceled { get; } = [];

        public Func<IMessage, bool>? FailPublishOnce { get; set; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default)
            where TEvent : IMessage
        {
            _ = ct;
            if (!_publishFailureConsumed && FailPublishOnce?.Invoke(evt) == true)
            {
                _publishFailureConsumed = true;
                throw new InvalidOperationException("transient publish failure");
            }

            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default)
        {
            _ = dueTime;
            _ = metadata;
            _ = ct;
            var generation = _generations.GetValueOrDefault(callbackId, 0) + 1;
            _generations[callbackId] = generation;
            Scheduled.Add(new ScheduledCallback(callbackId, generation, evt));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, generation, RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default)
        {
            _ = dueTime;
            _ = period;
            _ = metadata;
            _ = ct;
            var generation = _generations.GetValueOrDefault(callbackId, 0) + 1;
            _generations[callbackId] = generation;
            Scheduled.Add(new ScheduledCallback(callbackId, generation, evt));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, generation, RuntimeCallbackBackend.InMemory));
        }

        public Task CancelDurableCallbackAsync(
            RuntimeCallbackLease lease,
            CancellationToken ct = default)
        {
            _ = ct;
            Canceled.Add(new CanceledCallback(lease));
            return Task.CompletedTask;
        }
    }

    private sealed class StubWorkflowRunAgent(string id, string runId) : IAgent, IWorkflowRunModuleStateHost
    {
        private readonly Dictionary<string, string> _moduleStateJson = new(StringComparer.Ordinal);

        public string Id => id;

        public string RunId { get; } = runId;

        public string? GetModuleStateJson(string moduleName) =>
            _moduleStateJson.TryGetValue(moduleName, out var json) ? json : null;

        public Task UpsertModuleStateJsonAsync(string moduleName, string stateJson, CancellationToken ct = default)
        {
            _ = ct;
            _moduleStateJson[moduleName] = stateJson;
            return Task.CompletedTask;
        }

        public Task ClearModuleStateAsync(string moduleName, CancellationToken ct = default)
        {
            _ = ct;
            _moduleStateJson.Remove(moduleName);
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }

    private sealed record ScheduledCallback(
        string CallbackId,
        long Generation,
        IMessage Event);

    private sealed record CanceledCallback(
        RuntimeCallbackLease Lease)
    {
        public string CallbackId => Lease.CallbackId;

        public long ExpectedGeneration => Lease.Generation;
    }
}
