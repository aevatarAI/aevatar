using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class WorkflowRuntimeModuleBranchTests
{
    [Fact]
    public async Task DelayModule_ShouldValidateIds_AndCompleteImmediatelyForZeroDuration()
    {
        var module = new DelayModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = " ",
                StepType = "delay",
                RunId = " ",
            }),
            ctx,
            CancellationToken.None);

        var invalid = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        invalid.Success.Should().BeFalse();
        invalid.Error.Should().Contain("run_id and step_id");

        ctx.Published.Clear();
        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "delay-now",
                StepType = "delay",
                RunId = "run-delay",
                Input = "payload",
                Parameters = { ["duration_ms"] = "0" },
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be("payload");
        ctx.Scheduled.Should().BeEmpty();
    }

    [Fact]
    public async Task DelayModule_ShouldCancelExistingPending_AndRequireMatchingLease()
    {
        var module = new DelayModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "delay-step",
                StepType = "delay",
                RunId = "run-delay",
                Input = "first",
                Parameters = { ["duration_ms"] = "1000" },
            }),
            ctx,
            CancellationToken.None);
        var first = ctx.Scheduled.Single();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "delay-step",
                StepType = "delay",
                RunId = "run-delay",
                Input = "second",
                Parameters = { ["duration_ms"] = "2000" },
            }),
            ctx,
            CancellationToken.None);
        var second = ctx.Scheduled.Last();

        ctx.Canceled.Should().ContainSingle(x => x.CallbackId == first.CallbackId);
        ctx.Published.Clear();

        await module.HandleAsync(
            Wrap(
                new DelayStepTimeoutFiredEvent
                {
                    RunId = "run-delay",
                    StepId = "delay-step",
                    DurationMs = 2000,
                },
                MetadataFor(second, generation: second.Generation - 1)),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(
            Wrap(
                new DelayStepTimeoutFiredEvent
                {
                    RunId = "run-delay",
                    StepId = "delay-step",
                    DurationMs = 2000,
                },
                MetadataFor(second)),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be("second");
    }

    [Fact]
    public async Task WorkflowCallModule_ShouldValidateMissingFieldsAndLifecycle()
    {
        var module = new WorkflowCallModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = " ",
                StepType = "workflow_call",
                RunId = "parent-run",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single().Error
            .Should().Contain("missing step_id");

        ctx.Published.Clear();
        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "step-a",
                StepType = "workflow_call",
                RunId = "parent-run",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single().Error
            .Should().Contain("missing workflow parameter");

        ctx.Published.Clear();
        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "step-a",
                StepType = "workflow_call",
                RunId = "parent-run",
                Parameters =
                {
                    ["workflow"] = "child_flow",
                    ["lifecycle"] = "invalid",
                },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single().Error
            .Should().Contain(WorkflowCallLifecycle.AllowedValuesText);
    }

    [Fact]
    public async Task WorkflowCallModule_ShouldPublishInvocationForValidRequest()
    {
        var module = new WorkflowCallModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "step-b",
                StepType = "workflow_call",
                RunId = "parent-run",
                Input = "payload",
                Parameters =
                {
                    ["workflow"] = "child_flow",
                    ["lifecycle"] = "scope",
                },
            }),
            ctx,
            CancellationToken.None);

        var invocation = ctx.Published.Select(x => x.Event).OfType<SubWorkflowInvokeRequestedEvent>().Single();
        invocation.ParentRunId.Should().Be("parent-run");
        invocation.ParentStepId.Should().Be("step-b");
        invocation.WorkflowName.Should().Be("child_flow");
        invocation.Input.Should().Be("payload");
        invocation.Lifecycle.Should().Be(WorkflowCallLifecycle.Scope);
        invocation.InvocationId.Should().StartWith("parent-run:workflow_call:step-b:");
    }

    [Fact]
    public async Task LlmCallModule_ShouldPublishDeterministicFailure_WhenStepIdMissing()
    {
        var module = new LLMCallModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "",
                StepType = "llm_call",
                RunId = "run-llm-invalid",
                Input = "prompt",
            }),
            ctx,
            CancellationToken.None);

        var failure = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        failure.Success.Should().BeFalse();
        failure.StepId.Should().BeEmpty();
        failure.Error.Should().Contain("requires non-empty step_id");
    }

    [Fact]
    public async Task ReflectModule_ShouldPublishDeterministicFailure_WhenStepIdMissing()
    {
        var module = new ReflectModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "",
                StepType = "reflect",
                RunId = "run-reflect-invalid",
                Input = "draft",
            }),
            ctx,
            CancellationToken.None);

        var failure = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        failure.Success.Should().BeFalse();
        failure.StepId.Should().BeEmpty();
        failure.Error.Should().Contain("requires non-empty step_id");
    }

    [Fact]
    public async Task DynamicWorkflowModule_ShouldIgnoreUnsupportedPayload_AndValidateYamlBlocks()
    {
        var module = new DynamicWorkflowModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            new EventEnvelope { Payload = Any.Pack(new WorkflowCompletedEvent()) },
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "step-x",
                RunId = "run-x",
                StepType = "transform",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "step-x",
                RunId = "run-x",
                StepType = "dynamic_workflow",
                Input = "no fenced yaml here",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single().Error
            .Should().Contain("No workflow YAML found");

        ctx.Published.Clear();
        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "step-x",
                RunId = "run-x",
                StepType = "dynamic_workflow",
                Input =
                    """
                    ```yaml
                    name: bad
                    roles: []
                    steps:
                      - id: broken
                        type: unknown_step
                    ```
                    """,
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single().Error
            .Should().Contain("Invalid workflow YAML");
    }

    [Fact]
    public async Task DynamicWorkflowModule_ShouldPublishReplaceEventForValidYaml()
    {
        var module = new DynamicWorkflowModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "step-y",
                RunId = "run-y",
                StepType = "dynamic_workflow",
                Input =
                    """
                    preface
                    ```yaml
                    name: wf-a
                    roles: []
                    steps:
                      - id: s1
                        type: transform
                    ```
                    trailing
                    ```yaml
                    name: wf-b
                    roles: []
                    steps:
                      - id: s2
                        type: transform
                    ```
                    """,
                Parameters = { ["original_input"] = "hello" },
            }),
            ctx,
            CancellationToken.None);

        var replace = ctx.Published.Select(x => x.Event).OfType<ReplaceWorkflowDefinitionAndExecuteEvent>().Single();
        replace.Input.Should().Be("hello");
        replace.WorkflowYaml.Should().Contain("name: wf-b");
        DynamicWorkflowModule.ExtractYaml(" ").Should().BeNull();
    }

    [Fact]
    public void DynamicWorkflowModule_ValidateWorkflowYaml_ShouldExpandKnownTypesFromFactory()
    {
        var ctx = new RecordingWorkflowContext(new TestEventModuleFactory("custom_executor"));

        var errors = DynamicWorkflowModule.ValidateWorkflowYaml(
            """
            name: wf-custom
            roles: []
            steps:
              - id: s1
                type: custom_executor
            """,
            ctx);

        errors.Should().BeEmpty();
    }

    private static EventEnvelope Wrap(IMessage evt, EnvelopeCallbackContext? callback = null)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
            Runtime = callback == null
                ? null
                : new EnvelopeRuntime
                {
                    Callback = callback.Clone(),
                },
        };
    }

    private static EnvelopeCallbackContext MetadataFor(
        RecordedCallback callback,
        long? generation = null) =>
        new()
        {
            CallbackId = callback.CallbackId,
            Generation = generation ?? callback.Generation,
            FireIndex = 0,
            FiredAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    private sealed class RecordingWorkflowContext : IWorkflowExecutionContext
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _callbackGenerations = new(StringComparer.Ordinal);
        private readonly IServiceProvider _services;

        public RecordingWorkflowContext(IEventModuleFactory<IWorkflowExecutionContext>? moduleFactory = null)
        {
            _services = new TestServiceProvider(moduleFactory);
        }

        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public string RunId => "run-1";

        public IServiceProvider Services => _services;

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public List<RecordedCallback> Scheduled { get; } = [];

        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            if (!_states.TryGetValue(scopeKey, out var packed) || !packed.Is(new TState().Descriptor))
                return new TState();

            return packed.Unpack<TState>() ?? new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            _states
                .Where(x => string.IsNullOrEmpty(scopeKeyPrefix) || x.Key.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
                .Where(x => x.Value.Is(new TState().Descriptor))
                .Select(x => new KeyValuePair<string, TState>(x.Key, x.Value.Unpack<TState>() ?? new TState()))
                .ToList();

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            ct.ThrowIfCancellationRequested();
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
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
            ct.ThrowIfCancellationRequested();
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(string targetActorId, TEvent evt, CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = dueTime;
            _ = options;
            var generation = _callbackGenerations.GetValueOrDefault(callbackId, 0) + 1;
            _callbackGenerations[callbackId] = generation;
            Scheduled.Add(new RecordedCallback(callbackId, generation, evt));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, generation, RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            ScheduleSelfDurableTimeoutAsync(callbackId, dueTime + period, evt, options, ct);

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Canceled.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed class TestServiceProvider : IServiceProvider
    {
        private readonly IReadOnlyList<IWorkflowModulePack> _modulePacks = [new WorkflowCoreModulePack()];
        private readonly IEventModuleFactory<IWorkflowExecutionContext>? _moduleFactory;

        public TestServiceProvider(IEventModuleFactory<IWorkflowExecutionContext>? moduleFactory)
        {
            _moduleFactory = moduleFactory;
        }

        public object? GetService(global::System.Type serviceType)
        {
            if (serviceType == typeof(IEnumerable<IWorkflowModulePack>))
                return _modulePacks;
            if (serviceType == typeof(IEventModuleFactory<IWorkflowExecutionContext>))
                return _moduleFactory;

            return null;
        }
    }

    private sealed class TestEventModuleFactory(string supportedName) : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
        {
            module = null;
            return string.Equals(name, supportedName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record RecordedCallback(
        string CallbackId,
        long Generation,
        IMessage Event);
}
