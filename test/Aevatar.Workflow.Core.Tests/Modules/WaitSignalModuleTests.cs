using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class WaitSignalModuleTests
{
    [Fact]
    public async Task HandleAsync_WhenSameRunAndSignalHaveMultipleWaiters_ShouldRequireStepIdForPreciseResume()
    {
        var module = new WaitSignalModule();
        var context = new RecordingEventHandlerContext(
            new EmptyServiceProvider(),
            new StubAgent("workflow-1"),
            NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-a",
                StepType = "wait_signal",
                RunId = "run-shared",
                Input = "fallback-a",
                Parameters = { ["signal_name"] = "approval" },
            }),
            context,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-b",
                StepType = "wait_signal",
                RunId = "run-shared",
                Input = "fallback-b",
                Parameters = { ["signal_name"] = "approval" },
            }),
            context,
            CancellationToken.None);
        context.Published.Clear();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                RunId = "run-shared",
                SignalName = "approval",
                Payload = "ambiguous",
            }),
            context,
            CancellationToken.None);
        context.Published.Should().BeEmpty();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                RunId = "run-shared",
                SignalName = "approval",
                StepId = "wait-b",
                Payload = "resolved-b",
            }),
            context,
            CancellationToken.None);

        var completion = context.Published.Select(item => item.Event).OfType<StepCompletedEvent>().Single();
        completion.StepId.Should().Be("wait-b");
        completion.RunId.Should().Be("run-shared");
        completion.Output.Should().Be("resolved-b");
    }

    [Fact]
    public async Task HandleAsync_WhenSignalArrivesBeforeWaitStep_ShouldBufferAndConsumeOnActivation()
    {
        var module = new WaitSignalModule();
        var context = new RecordingEventHandlerContext(
            new EmptyServiceProvider(),
            new StubAgent("workflow-early"),
            NullLogger.Instance)
        {
            UtcNow = DateTimeOffset.Parse("2026-05-20T10:00:00Z"),
        };

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                RunId = "run-early",
                StepId = "wait-approval",
                SignalName = "approval",
                Payload = "early-payload",
            }),
            context,
            CancellationToken.None);

        var bufferedEvent = context.Published.Select(item => item.Event).OfType<WorkflowSignalBufferedEvent>().Should().ContainSingle().Subject;
        bufferedEvent.ReceivedAtUnixTimeMs.Should().Be(context.UtcNow.ToUnixTimeMilliseconds());

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-approval",
                StepType = "wait_signal",
                RunId = "run-early",
                Input = "fallback",
                Parameters = { ["signal_name"] = "approval" },
            }),
            context,
            CancellationToken.None);

        var completion = context.Published.Select(item => item.Event).OfType<StepCompletedEvent>().Last();
        completion.Success.Should().BeTrue();
        completion.RunId.Should().Be("run-early");
        completion.StepId.Should().Be("wait-approval");
        completion.Output.Should().Be("early-payload");

        context.Published.Select(item => item.Event).OfType<WaitingForSignalEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenSignalMissingStepId_ShouldNotBuffer()
    {
        var module = new WaitSignalModule();
        var context = new RecordingEventHandlerContext(
            new EmptyServiceProvider(),
            new StubAgent("workflow-early"),
            NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                RunId = "run-no-step",
                SignalName = "approval",
                Payload = "payload",
            }),
            context,
            CancellationToken.None);

        context.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenExternalApprovalWaitStarts_ShouldSavePendingThenPublishRegisteredFact()
    {
        var module = new WaitSignalModule();
        var context = new RecordingEventHandlerContext(
            new EmptyServiceProvider(),
            new StubAgent("workflow-approval"),
            NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-approval",
                StepType = "wait_signal",
                RunId = "run-approval",
                Input = "fallback",
                StepParameters = new WorkflowStepParameters
                {
                    ExternalApproval = new WorkflowExternalApprovalWaitOptions
                    {
                        SourceId = "NyxID",
                        ExternalIdKind = "Instance_Code",
                        ExternalId = "APP-42",
                        SignalName = "approval-terminal",
                        CallbackIdempotencyKey = "idem-42",
                        RequestId = "request-42",
                    },
                },
            }),
            context,
            CancellationToken.None);

        var state = context.LoadState<WaitSignalModuleState>("wait_signal");
        var pending = state.Pending.Values.Should().ContainSingle().Subject;
        pending.SignalName.Should().Be("approval-terminal");
        pending.ExternalApproval.Should().NotBeNull();
        pending.ExternalApproval.SourceId.Should().Be("NyxID");
        pending.ExternalApproval.ExternalIdKind.Should().Be("Instance_Code");
        pending.ExternalApproval.ExternalId.Should().Be("APP-42");

        var registered = context.Published.Select(item => item.Event)
            .OfType<WorkflowExternalApprovalContinuationRegisteredEvent>()
            .Should()
            .ContainSingle()
            .Subject;
        registered.RunId.Should().Be("run-approval");
        registered.StepId.Should().Be("wait-approval");
        registered.SignalName.Should().Be("approval-terminal");
        registered.CallbackIdempotencyKey.Should().Be("idem-42");
        registered.RequestId.Should().Be("request-42");
        WorkflowArtifactFactBuilder.TryBuild(
                Envelope(registered),
                "workflow-approval",
                "run-approval",
                out var committedFact)
            .Should()
            .BeTrue();
        committedFact.Should().BeOfType<WorkflowExternalApprovalContinuationRegisteredEvent>();
    }

    [Fact]
    public async Task HandleAsync_WhenExternalApprovalSignalCompletes_ShouldPublishClearedFactOnce()
    {
        var module = new WaitSignalModule();
        var context = new RecordingEventHandlerContext(
            new EmptyServiceProvider(),
            new StubAgent("workflow-approval"),
            NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-approval",
                StepType = "wait_signal",
                RunId = "run-approval",
                StepParameters = new WorkflowStepParameters
                {
                    ExternalApproval = new WorkflowExternalApprovalWaitOptions
                    {
                        SourceId = "nyxid",
                        ExternalIdKind = "instance_code",
                        ExternalId = "app-42",
                        SignalName = "approval-terminal",
                        CallbackIdempotencyKey = "idem-42",
                        RequestId = "request-42",
                    },
                },
            }),
            context,
            CancellationToken.None);
        context.Published.Clear();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                RunId = "run-approval",
                StepId = "wait-approval",
                SignalName = "approval-terminal",
                Payload = "approved",
            }),
            context,
            CancellationToken.None);

        context.Published.Select(item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should()
            .ContainSingle()
            .Subject
            .Output.Should().Be("approved");

        var cleared = context.Published.Select(item => item.Event)
            .OfType<WorkflowExternalApprovalContinuationClearedEvent>()
            .Should()
            .ContainSingle()
            .Subject;
        cleared.RunId.Should().Be("run-approval");
        cleared.StepId.Should().Be("wait-approval");
        cleared.SignalName.Should().Be("approval-terminal");
        cleared.CallbackIdempotencyKey.Should().Be("idem-42");
    }

    [Fact]
    public async Task HandleAsync_WhenExternalApprovalWaitTimesOut_ShouldFailStepAndPublishClearedFactOnce()
    {
        var module = new WaitSignalModule();
        var context = new RecordingEventHandlerContext(
            new EmptyServiceProvider(),
            new StubAgent("workflow-approval"),
            NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-approval",
                StepType = "wait_signal",
                RunId = "run-approval",
                StepParameters = new WorkflowStepParameters
                {
                    Parameters = { ["timeout_ms"] = "5000" },
                    ExternalApproval = new WorkflowExternalApprovalWaitOptions
                    {
                        SourceId = "nyxid",
                        ExternalIdKind = "instance_code",
                        ExternalId = "app-42",
                        SignalName = "approval-terminal",
                        CallbackIdempotencyKey = "idem-42",
                        RequestId = "request-42",
                    },
                },
            }),
            context,
            CancellationToken.None);
        var scheduled = context.Scheduled.Should().ContainSingle().Subject;
        context.Published.Clear();

        await module.HandleAsync(
            Envelope(
                new WaitSignalTimeoutFiredEvent
                {
                    RunId = "run-approval",
                    StepId = "wait-approval",
                    SignalName = "approval-terminal",
                    TimeoutMs = 5000,
                },
                scheduled.Lease),
            context,
            CancellationToken.None);

        var failed = context.Published.Select(item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should()
            .ContainSingle()
            .Subject;
        failed.RunId.Should().Be("run-approval");
        failed.StepId.Should().Be("wait-approval");
        failed.Success.Should().BeFalse();
        failed.Error.Should().Contain("approval-terminal");

        var cleared = context.Published.Select(item => item.Event)
            .OfType<WorkflowExternalApprovalContinuationClearedEvent>()
            .Should()
            .ContainSingle()
            .Subject;
        cleared.RunId.Should().Be("run-approval");
        cleared.StepId.Should().Be("wait-approval");
        cleared.SignalName.Should().Be("approval-terminal");
        cleared.SourceId.Should().Be("nyxid");
        cleared.ExternalIdKind.Should().Be("instance_code");
        cleared.ExternalId.Should().Be("app-42");
        cleared.CallbackIdempotencyKey.Should().Be("idem-42");
        cleared.RequestId.Should().Be("request-42");
        WorkflowArtifactFactBuilder.TryBuild(
                Envelope(cleared),
                "workflow-approval",
                "run-approval",
                out var committedFact)
            .Should()
            .BeTrue();
        committedFact.Should().BeOfType<WorkflowExternalApprovalContinuationClearedEvent>();
    }

    [Fact]
    public async Task HandleAsync_WhenTimeoutIsTwentyFourHours_ShouldScheduleDurableCallback()
    {
        var module = new WaitSignalModule();
        var context = new RecordingEventHandlerContext(
            new EmptyServiceProvider(),
            new StubAgent("workflow-24h"),
            NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-24h",
                StepType = "wait_signal",
                RunId = "run-24h",
                Parameters =
                {
                    ["signal_name"] = "callback",
                    ["timeout_ms"] = "86400000",
                },
            }),
            context,
            CancellationToken.None);

        var scheduled = context.Scheduled.Should().ContainSingle().Subject;
        var timeout = scheduled.Event.Should().BeOfType<WaitSignalTimeoutFiredEvent>().Subject;
        timeout.TimeoutMs.Should().Be(86_400_000);
        context.Published.Select(item => item.Event)
            .OfType<WaitingForSignalEvent>()
            .Should()
            .ContainSingle()
            .Subject
            .TimeoutMs.Should().Be(86_400_000);
    }

    [Fact]
    public async Task HandleAsync_WhenTimeoutExceedsTwentyFourHours_ShouldClampDurableCallback()
    {
        var module = new WaitSignalModule();
        var context = new RecordingEventHandlerContext(
            new EmptyServiceProvider(),
            new StubAgent("workflow-clamp"),
            NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-clamp",
                StepType = "wait_signal",
                RunId = "run-clamp",
                Parameters =
                {
                    ["signal_name"] = "callback",
                    ["timeout_ms"] = "172800000",
                },
            }),
            context,
            CancellationToken.None);

        var timeout = context.Scheduled.Should().ContainSingle().Subject.Event
            .Should()
            .BeOfType<WaitSignalTimeoutFiredEvent>()
            .Subject;
        timeout.TimeoutMs.Should().Be(86_400_000);
    }

    [Fact]
    public async Task HandleAsync_WhenExternalApprovalWaiterIsReplaced_ShouldClearOldBinding()
    {
        var module = new WaitSignalModule();
        var context = new RecordingEventHandlerContext(
            new EmptyServiceProvider(),
            new StubAgent("workflow-approval"),
            NullLogger.Instance);

        await module.HandleAsync(
            Envelope(ExternalApprovalRequest("app-old", "idem-old")),
            context,
            CancellationToken.None);
        context.Published.Clear();

        await module.HandleAsync(
            Envelope(ExternalApprovalRequest("app-new", "idem-new")),
            context,
            CancellationToken.None);

        var facts = context.Published.Select(item => item.Event).ToList();
        facts.OfType<WorkflowExternalApprovalContinuationClearedEvent>()
            .Should()
            .ContainSingle()
            .Subject
            .ExternalId.Should().Be("app-old");
        facts.OfType<WorkflowExternalApprovalContinuationRegisteredEvent>()
            .Should()
            .ContainSingle()
            .Subject
            .ExternalId.Should().Be("app-new");
    }

    private static StepRequestEvent ExternalApprovalRequest(string externalId, string idempotencyKey) =>
        new()
        {
            StepId = "wait-approval",
            StepType = "wait_signal",
            RunId = "run-approval",
            StepParameters = new WorkflowStepParameters
            {
                ExternalApproval = new WorkflowExternalApprovalWaitOptions
                {
                    SourceId = "nyxid",
                    ExternalIdKind = "instance_code",
                    ExternalId = externalId,
                    SignalName = "approval-terminal",
                    CallbackIdempotencyKey = idempotencyKey,
                },
            },
        };

    private static EventEnvelope Envelope(IMessage evt, RuntimeCallbackLease? lease = null)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };

        if (lease != null)
        {
            envelope.Runtime = new EnvelopeRuntime
            {
                Callback = new EnvelopeCallbackContext
                {
                    CallbackId = lease.CallbackId,
                    Generation = lease.Generation,
                    FiredAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SlotEpoch = lease.SlotEpoch,
                },
            };
        }

        return envelope;
    }

    private sealed class RecordingEventHandlerContext : IWorkflowExecutionContext
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);

        public RecordingEventHandlerContext(IServiceProvider services, IAgent agent, ILogger logger)
        {
            Services = services;
            Agent = agent;
            Logger = logger;
            InboundEnvelope = new EventEnvelope();
        }

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];
        public List<ScheduledCallbackRecord> Scheduled { get; } = [];
        public EventEnvelope InboundEnvelope { get; }
        public string AgentId => Agent.Id;
        public IAgent Agent { get; }
        public IServiceProvider Services { get; }
        public ILogger Logger { get; }
        public string RunId => AgentId;
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

        public long GetTimestamp() => 1;

        public TimeSpan GetElapsedTime(long startingTimestamp)
        {
            _ = startingTimestamp;
            return TimeSpan.Zero;
        }

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            if (!_states.TryGetValue(scopeKey, out var packed) || !packed.Is(new TState().Descriptor))
                return new TState();

            return packed.Unpack<TState>() ?? new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new()
        {
            var states = new List<KeyValuePair<string, TState>>();
            foreach (var (scopeKey, packed) in _states)
            {
                if (!string.IsNullOrEmpty(scopeKeyPrefix) &&
                    !scopeKey.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!packed.Is(new TState().Descriptor))
                    continue;

                states.Add(new KeyValuePair<string, TState>(scopeKey, packed.Unpack<TState>() ?? new TState()));
            }

            return states;
        }

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = options;
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            _ = dueTime;
            _ = options;
            _ = ct;
            var lease = new RuntimeCallbackLease(AgentId, callbackId, Scheduled.Count + 1, RuntimeCallbackBackend.InMemory);
            Scheduled.Add(new ScheduledCallbackRecord(lease, dueTime, evt));
            return Task.FromResult(lease);
        }

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;
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

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }

    private sealed record ScheduledCallbackRecord(RuntimeCallbackLease Lease, TimeSpan DueTime, IMessage Event);
}
