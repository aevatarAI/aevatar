using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowExecutionContextAdapterTests
{
    [Fact]
    public void Create_ShouldValidateArguments()
    {
        var inner = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();

        FluentActions.Invoking(() => WorkflowExecutionContextAdapter.Create(null!, host))
            .Should()
            .Throw<ArgumentNullException>();
        FluentActions.Invoking(() => WorkflowExecutionContextAdapter.Create(inner, null!))
            .Should()
            .Throw<ArgumentNullException>();
    }

    [Fact]
    public void RuntimeContext_ShouldExposeStateHostRuntimeContext()
    {
        var host = new RecordingStateHost();
        host.RuntimeContext.RequestPassthroughMetadata.Set("trace-id", "abc");

        var adapter = WorkflowExecutionContextAdapter.Create(new RecordingEventHandlerContext(), host);

        adapter.RuntimeContext.Should().BeSameAs(host.RuntimeContext);
        adapter.RuntimeContext.RequestPassthroughMetadata.Values["trace-id"].Should().Be("abc");
    }

    [Fact]
    public void ScopeId_ShouldExposeStateHostScopeId()
    {
        var host = new RecordingStateHost { ScopeId = "scope-1" };

        var adapter = WorkflowExecutionContextAdapter.Create(new RecordingEventHandlerContext(), host);

        adapter.ScopeId.Should().Be("scope-1");
    }

    [Fact]
    public void ScopeId_ShouldDefaultToEmptyWhenStateHostDoesNotOverride()
    {
        IWorkflowExecutionStateHost host = new DefaultScopeStateHost();

        var adapter = WorkflowExecutionContextAdapter.Create(new RecordingEventHandlerContext(), host);

        host.ScopeId.Should().BeEmpty();
        adapter.ScopeId.Should().BeEmpty();
    }

    [Fact]
    public async Task ScopeId_ShouldFlowFromWorkflowRunStateHostToAdapter()
    {
        var agent = new WorkflowRunGAgent(
            new UnsupportedActorRuntime(),
            new UnsupportedActorRuntime(),
            new EmptyEventModuleFactory(),
            [])
        {
            EventSourcingBehaviorFactory = new InMemoryEventSourcingBehaviorFactory<WorkflowRunState>(),
        };
        SetAgentId(agent, "workflow-run-scope-bridge");
        var stateHost = (IWorkflowExecutionStateHost)agent;
        var adapterBeforeBind = WorkflowExecutionContextAdapter.Create(new RecordingEventHandlerContext(), stateHost);

        stateHost.ScopeId.Should().BeEmpty();
        adapterBeforeBind.ScopeId.Should().BeEmpty();

        await agent.BindWorkflowRunDefinitionAsync(
            definitionActorId: "definition-1",
            workflowYaml: "",
            workflowName: "wf_scope",
            inlineWorkflowYamls: null,
            runId: "run-1",
            scopeId: " scope-1 ",
            runOrigin: null,
            scheduleId: null,
            capabilityAdmissionPlan: null,
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive);
        var adapter = WorkflowExecutionContextAdapter.Create(new RecordingEventHandlerContext(), stateHost);

        stateHost.ScopeId.Should().Be("scope-1");
        adapter.ScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task EnsureWorkflowRunDefinition_ShouldBeIdempotentWithoutResettingState()
    {
        var (agent, runtime) = CreateBareWorkflowRunAgent("work-order-run-1");
        var command = BuildEnsureWorkflowRunDefinition();

        await agent.HandleEnsureWorkflowRunDefinitionAsync(command);
        var boundState = agent.State.Clone();
        await agent.HandleEnsureWorkflowRunDefinitionAsync(command.Clone());

        agent.State.Equals(boundState).Should().BeTrue();
        agent.State.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        runtime.Links.Should().OnlyContain(link =>
            link.ParentId == "definition-1" &&
            link.ChildId == "work-order-run-1");
    }

    [Fact]
    public async Task EnsureWorkflowRunDefinition_ShouldRejectConflictingBinding()
    {
        var (agent, runtime) = CreateBareWorkflowRunAgent("work-order-run-1");
        await agent.HandleEnsureWorkflowRunDefinitionAsync(BuildEnsureWorkflowRunDefinition());
        var linksBeforeConflict = runtime.Links.Count;
        var conflicting = BuildEnsureWorkflowRunDefinition();
        conflicting.Binding.WorkflowYaml = "name: changed\nroles: []\nsteps: []\n";
        conflicting.ExecutionRequest = new WorkflowChatRequestEvent
        {
            Prompt = "must not execute against the existing definition",
        };

        var act = () => agent.HandleEnsureWorkflowRunDefinitionAsync(conflicting);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already bound to a different definition or identity*");
        runtime.Links.Should().HaveCount(linksBeforeConflict);
        agent.State.LastCommandId.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureWorkflowRunDefinition_WhenModeChanges_ShouldRejectAndPreserveFirstMode()
    {
        var (agent, runtime) = CreateBareWorkflowRunAgent("work-order-run-1");
        await agent.HandleEnsureWorkflowRunDefinitionAsync(BuildEnsureWorkflowRunDefinition());
        var linksBeforeConflict = runtime.Links.Count;
        var conflicting = BuildEnsureWorkflowRunDefinition();
        conflicting.Binding.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Durable;

        var act = () => agent.HandleEnsureWorkflowRunDefinitionAsync(conflicting);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different definition or identity*");
        agent.State.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        runtime.Links.Should().HaveCount(linksBeforeConflict);
    }

    [Fact]
    public void WorkflowRunStateContract_ShouldCarryExpectedExecutionMode()
    {
        WorkflowRunState.Descriptor.FindFieldByName("expected_execution_mode")!.FieldNumber.Should().Be(44);
    }

    [Fact]
    public void CallerNyxIdAuthority_ShouldExposeNormalizedSnapshotWithoutLeakingMutableState()
    {
        var host = new RecordingStateHost();
        host.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            NyxIdAuthority = new WorkflowCallerNyxIdAuthority
            {
                Platform = " nyxid ",
                Tenant = " tenant-a ",
                ExternalUserId = " user-42 ",
                Scope = " proxy ",
            },
        };
        var adapter = WorkflowExecutionContextAdapter.Create(new RecordingEventHandlerContext(), host);

        var authority = adapter.CallerNyxIdAuthority;

        authority.Should().BeEquivalentTo(new WorkflowCallerNyxIdAuthority
        {
            Platform = "nyxid",
            Tenant = "tenant-a",
            ExternalUserId = "user-42",
            Scope = "proxy",
        });
        authority!.ExternalUserId = "mutated";
        adapter.CallerNyxIdAuthority!.ExternalUserId.Should().Be("user-42");
        host.ExecutionContextState.CallerCredential.NyxIdAuthority.ExternalUserId.Should().Be(" user-42 ");
    }

    [Fact]
    public void CallerNyxIdAuthority_ShouldFailClosedForIncompleteState()
    {
        var host = new RecordingStateHost();
        host.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            NyxIdAuthority = new WorkflowCallerNyxIdAuthority
            {
                Platform = "nyxid",
                ExternalUserId = "user-42",
            },
        };
        var adapter = WorkflowExecutionContextAdapter.Create(new RecordingEventHandlerContext(), host);

        adapter.CallerNyxIdAuthority.Should().BeNull();
    }

    [Fact]
    public void LoadState_ShouldReturnSavedValue_AndFallbackToDefault()
    {
        var adapter = WorkflowExecutionContextAdapter.Create(
            new RecordingEventHandlerContext(),
            new RecordingStateHost
            {
                States =
                {
                    ["matched"] = Any.Pack(new StringValue { Value = "ready" }),
                    ["mismatch"] = Any.Pack(new Int32Value { Value = 7 }),
                },
            });

        adapter.LoadState<StringValue>("matched").Value.Should().Be("ready");
        adapter.LoadState<StringValue>("missing").Value.Should().BeEmpty();
        adapter.LoadState<StringValue>("mismatch").Value.Should().BeEmpty();

        FluentActions.Invoking(() => adapter.LoadState<StringValue>(" "))
            .Should()
            .Throw<ArgumentException>();
    }

    [Fact]
    public void LoadStates_ShouldFilterByPrefix_AndPayloadType()
    {
        var adapter = WorkflowExecutionContextAdapter.Create(
            new RecordingEventHandlerContext(),
            new RecordingStateHost
            {
                States =
                {
                    ["scope.alpha"] = Any.Pack(new StringValue { Value = "a" }),
                    ["scope.beta"] = Any.Pack(new StringValue { Value = "b" }),
                    ["scope.gamma"] = Any.Pack(new Int32Value { Value = 3 }),
                    ["other.delta"] = Any.Pack(new StringValue { Value = "d" }),
                },
            });

        var scoped = adapter.LoadStates<StringValue>("scope.");
        scoped.Should().HaveCount(2);
        scoped.Select(x => x.Key).Should().BeEquivalentTo("scope.alpha", "scope.beta");
        scoped.Select(x => x.Value.Value).Should().BeEquivalentTo("a", "b");

        var all = adapter.LoadStates<StringValue>();
        all.Should().HaveCount(3);
        all.Select(x => x.Key).Should().Contain("other.delta");
    }

    [Fact]
    public async Task SaveAndClearState_ShouldValidateArguments_AndPersistThroughStateHost()
    {
        var stateHost = new RecordingStateHost();
        var adapter = WorkflowExecutionContextAdapter.Create(
            new RecordingEventHandlerContext(),
            stateHost);

        await adapter.SaveStateAsync("scope.a", new StringValue { Value = "saved" }, CancellationToken.None);
        stateHost.States["scope.a"].Unpack<StringValue>().Value.Should().Be("saved");

        await adapter.ClearStateAsync("scope.a", CancellationToken.None);
        stateHost.States.Should().NotContainKey("scope.a");

        var saveWithBlankScope = () => adapter.SaveStateAsync(" ", new StringValue(), CancellationToken.None);
        await saveWithBlankScope.Should().ThrowAsync<ArgumentException>();

        var saveWithNullState = () => adapter.SaveStateAsync<StringValue>("scope.b", null!, CancellationToken.None);
        await saveWithNullState.Should().ThrowAsync<ArgumentNullException>();

        var clearWithBlankScope = () => adapter.ClearStateAsync(" ", CancellationToken.None);
        await clearWithBlankScope.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ForwardingApis_ShouldDelegateToInnerContext()
    {
        var inner = new RecordingEventHandlerContext();
        var adapter = WorkflowExecutionContextAdapter.Create(inner, new RecordingStateHost { RunId = "run-42" });
        var timeoutEvent = new Empty();
        var cancelLease = new RuntimeCallbackLease("agent-1", "cancel-me", 7, RuntimeCallbackBackend.InMemory);

        adapter.AgentId.Should().Be("agent-1");
        adapter.RunId.Should().Be("run-42");
        adapter.InboundEnvelope.Should().BeSameAs(inner.InboundEnvelope);
        adapter.Services.Should().BeSameAs(inner.Services);
        adapter.Logger.Should().BeSameAs(inner.Logger);
        adapter.UtcNow.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        adapter.GetElapsedTime(adapter.GetTimestamp()).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);

        await adapter.PublishAsync(new StringValue { Value = "published" }, TopologyAudience.Self, CancellationToken.None);
        await adapter.SendToAsync("child-1", new Int32Value { Value = 3 }, CancellationToken.None);

        var timeoutLease = await adapter.ScheduleSelfDurableTimeoutAsync(
            "timeout-1",
            TimeSpan.FromSeconds(5),
            timeoutEvent,
            new EventEnvelopePublishOptions
            {
                Propagation = new EventEnvelopePropagationOverrides
                {
                    Baggage = { ["mode"] = "timeout" },
                },
            },
            CancellationToken.None);
        await adapter.CancelDurableCallbackAsync(cancelLease, CancellationToken.None);

        inner.Published.Should().ContainSingle(x =>
            x.Direction == TopologyAudience.Self &&
            x.Event.Unpack<StringValue>().Value == "published");
        inner.Sent.Should().ContainSingle(x =>
            x.TargetActorId == "child-1" &&
            x.Event.Unpack<Int32Value>().Value == 3);
        timeoutLease.CallbackId.Should().Be("timeout-1");
        inner.ScheduledTimeouts.Should().ContainSingle(x => x.CallbackId == "timeout-1");
        inner.Canceled.Should().ContainSingle(x => x.CallbackId == "cancel-me");
    }

    [Fact]
    public void ClockApis_ShouldUseInjectedTimeProvider()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-05-20T10:00:00Z"));
        var services = new SingleServiceProvider(typeof(TimeProvider), timeProvider);
        var adapter = WorkflowExecutionContextAdapter.Create(
            new RecordingEventHandlerContext { ServicesOverride = services },
            new RecordingStateHost());

        var startedAt = adapter.GetTimestamp();
        timeProvider.Advance(TimeSpan.FromMilliseconds(42));

        adapter.UtcNow.Should().Be(DateTimeOffset.Parse("2026-05-20T10:00:00.042Z"));
        adapter.GetElapsedTime(startedAt).Should().Be(TimeSpan.FromMilliseconds(42));
    }

    private sealed class RecordingEventHandlerContext : IEventHandlerContext
    {
        private readonly IServiceProvider _defaultServices = new NullServiceProvider();

        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public IAgent Agent { get; } = new StubAgent("agent-1");

        public IServiceProvider Services => ServicesOverride ?? _defaultServices;

        public IServiceProvider? ServicesOverride { get; init; }

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(Any Event, TopologyAudience Direction)> Published { get; } = [];

        public List<(string TargetActorId, Any Event)> Sent { get; } = [];

        public List<RecordedCallback> ScheduledTimeouts { get; } = [];

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

        public string ScopeId { get; set; } = string.Empty;

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public Task UpdateExecutionContextAsync(WorkflowRunExecutionContextDelta delta, CancellationToken ct = default)
        {
            ApplyDelta(ExecutionContextState, delta);
            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ExecutionContextState.Llm = null;
            ExecutionContextState.CallerCredential = null;
            return Task.CompletedTask;
        }

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

        Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
            WorkflowCompletedEvent terminalFailure,
            StepCompletedEvent? terminalStep,
            CancellationToken ct)
        {
            _ = terminalFailure;
            _ = terminalStep;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
            CompensableStepDispatchedEvent evt,
            CancellationToken ct)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
            CompensationStepCompletedEvent completion,
            CancellationToken ct = default)
        {
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
            string runId,
            string error,
            CancellationToken ct = default)
        {
            _ = runId;
            _ = error;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
            new(
                WorkflowCompensationTransitionStatus.NoCompensableLedger,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
    }

    private sealed class DefaultScopeStateHost : IWorkflowExecutionStateHost
    {
        public string RunId => "run-1";

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot { get; } = new();

        public Task UpdateExecutionContextAsync(WorkflowRunExecutionContextDelta delta, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = delta;
            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Any? GetExecutionState(string scopeKey)
        {
            _ = scopeKey;
            return null;
        }

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() => [];

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = scopeKey;
            _ = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = scopeKey;
            return Task.CompletedTask;
        }

        Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
            WorkflowCompletedEvent terminalFailure,
            StepCompletedEvent? terminalStep,
            CancellationToken ct)
        {
            _ = terminalFailure;
            _ = terminalStep;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
            CompensableStepDispatchedEvent evt,
            CancellationToken ct)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
            CompensationStepCompletedEvent completion,
            CancellationToken ct = default)
        {
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
            string runId,
            string error,
            CancellationToken ct = default)
        {
            _ = runId;
            _ = error;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
            new(
                WorkflowCompensationTransitionStatus.NoCompensableLedger,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<global::System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<global::System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(global::System.Type serviceType) => null;
    }

    private static void ApplyDelta(
        WorkflowRunExecutionContextState state,
        WorkflowRunExecutionContextDelta delta)
    {
        if (delta.ClearLlm)
            state.Llm = null;
        if (delta.ClearCallerCredential)
            state.CallerCredential = null;
        if (delta.Llm != null)
        {
            state.Llm = new WorkflowLlmExecutionContextState
            {
                ModelOverride = delta.Llm.ModelOverride,
                UserMemoryPrompt = delta.Llm.UserMemoryPrompt,
                RoutePreference = delta.Llm.RoutePreference,
            };
            if (delta.Llm.HasMaxToolRoundsOverride)
                state.Llm.MaxToolRoundsOverride = delta.Llm.MaxToolRoundsOverride;
        }

        if (delta.CallerCredential != null)
        {
            state.CallerCredential = new WorkflowCallerCredentialState
            {
                BearerToken = delta.CallerCredential.BearerToken,
            };
        }
    }

    private sealed class SingleServiceProvider(global::System.Type serviceType, object service) : IServiceProvider
    {
        public object? GetService(global::System.Type requestedServiceType) =>
            requestedServiceType == serviceType ? service : null;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan elapsed)
        {
            _utcNow = _utcNow.Add(elapsed);
            _timestamp += elapsed.Ticks;
        }
    }

    private sealed record RecordedCallback(
        string CallbackId,
        TimeSpan DueTime,
        Any Event,
        EventEnvelopePublishOptions? Options);

    private static void SetAgentId(GAgentBase agent, string agentId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [agentId]);
    }

    private static (WorkflowRunGAgent Agent, UnsupportedActorRuntime Runtime) CreateBareWorkflowRunAgent(
        string actorId)
    {
        var runtime = new UnsupportedActorRuntime();
        var agent = new WorkflowRunGAgent(
            runtime,
            runtime,
            new EmptyEventModuleFactory(),
            [])
        {
            EventSourcingBehaviorFactory = new InMemoryEventSourcingBehaviorFactory<WorkflowRunState>(),
        };
        SetAgentId(agent, actorId);
        return (agent, runtime);
    }

    private static EnsureWorkflowRunDefinitionEvent BuildEnsureWorkflowRunDefinition() =>
        new()
        {
            Binding = new BindWorkflowRunDefinitionEvent
            {
                DefinitionActorId = "definition-1",
                WorkflowYaml = "name: direct\nroles: []\nsteps: []\n",
                WorkflowName = "direct",
                RunId = "work-order-run-1",
                ScopeId = "scope-1",
                RunOrigin = WorkflowRunOrigins.WorkOrder,
                ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            },
        };

    private sealed class InMemoryEventSourcingBehaviorFactory<TState>
        : IEventSourcingBehaviorFactory<TState>
        where TState : class, IMessage<TState>, new()
    {
        public IEventSourcingBehavior<TState> Create(
            string agentId,
            global::System.Type actorType,
            Func<TState, IMessage, TState> transitionState)
        {
            _ = actorType;
            return new InMemoryEventSourcingBehavior<TState>(agentId, transitionState);
        }
    }

    private sealed class InMemoryEventSourcingBehavior<TState>(
        string agentId,
        Func<TState, IMessage, TState> transitionState)
        : IEventSourcingBehavior<TState>
        where TState : class, IMessage<TState>, new()
    {
        private readonly List<IMessage> _pending = [];
        private TState _state = new();

        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt)
            where TEvent : IMessage =>
            _pending.Add(evt);

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var result = new EventStoreCommitResult
            {
                AgentId = agentId,
            };
            foreach (var evt in _pending)
            {
                _state = transitionState(_state, evt);
                CurrentVersion++;
                result.CommittedEvents.Add(new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
                    Version = CurrentVersion,
                    EventType = evt.Descriptor.FullName,
                    EventData = Google.Protobuf.WellKnownTypes.Any.Pack(evt),
                    AgentId = agentId,
                });
            }

            result.LatestVersion = CurrentVersion;
            _pending.Clear();
            return Task.FromResult(result);
        }

        public Task PersistSnapshotAsync(TState currentState, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<TState?> ReplayAsync(string replayAgentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<TState?>(_state.Clone());
        }

        public void DiscardPendingEvents() => _pending.Clear();

        public TState TransitionState(TState current, IMessage evt) =>
            transitionState(current, evt);
    }

    private sealed class EmptyEventModuleFactory : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
        {
            _ = name;
            module = null;
            return false;
        }
    }

    private sealed class UnsupportedActorRuntime : IActorRuntime, IActorDispatchPort
    {
        public List<(string ParentId, string ChildId)> Links { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(global::System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string id) =>
            throw new NotSupportedException();

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Links.Add((parentId, childId));
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
