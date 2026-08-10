using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowRunToolCallPublicationRecoveryTests
{
    [Fact]
    public async Task Activation_ShouldScheduleAndDrainPersistedCompletionOutboxWithoutExecutingTool()
    {
        const string actorId = "run-tool-completion-recovery";
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(new ToolCallModuleState
            {
                Completions =
                {
                    new WorkflowToolCallCompletionOutboxEntry
                    {
                        RunId = actorId,
                        StepId = "tool-step",
                        CallId = $"workflow:{actorId}:tool-step:exec-1",
                        ExecutionId = "exec-1",
                        TerminalDecision = WorkflowToolCallTerminalDecision.NoApproval,
                        ToolCompletion = new WorkflowToolCallCompletedEvent
                        {
                            RunId = actorId,
                            StepId = "tool-step",
                            CallId = $"workflow:{actorId}:tool-step:exec-1",
                            Success = true,
                            ResultJson = "{}",
                        },
                        StepCompletion = new StepCompletedEvent
                        {
                            RunId = actorId,
                            StepId = "tool-step",
                            ExecutionId = "exec-1",
                            Success = true,
                            Output = "{}",
                        },
                    },
                },
            }));

        var scheduler = new RecordingCallbackScheduler();
        var recovered = CreateAgent(actorId, store, scheduler, out var tool, out var publisher);
        await recovered.ActivateAsync();

        var scheduled = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        var retry = scheduled.TriggerEnvelope.Payload!
            .Unpack<WorkflowToolCallPublicationRetryFiredEvent>();
        retry.PublicationKind.Should().Be(WorkflowToolCallPublicationKind.Completion);
        retry.RunId.Should().Be(actorId);
        retry.StepId.Should().Be("tool-step");
        retry.ExecutionId.Should().Be("exec-1");

        await recovered.HandleEventAsync(scheduled.TriggerEnvelope);

        tool.ExecuteCalls.Should().Be(0);
        publisher.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Should().ContainSingle();
        publisher.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().ContainSingle();
        var state = recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey].Unpack<ToolCallModuleState>();
        state.Completions.Should().BeEmpty();
        state.CompletionTombstones.Should().ContainSingle();
    }

    [Fact]
    public async Task Activation_ShouldRebuildAndDrainLegacyApprovalSuspensionWithoutExecutingTool()
    {
        const string actorId = "run-tool-suspension-recovery";
        var store = new InMemoryEventStore();
        var callId = $"workflow:{actorId}:tool-step:exec-1";
        var pending = new PendingToolCallApprovalState
        {
            RunId = actorId,
            StepId = "tool-step",
            ExecutionId = "exec-1",
            ToolName = "counting_tool",
            ToolCallId = callId,
            ApprovalRequestId = "approval-1",
            ArgumentsJson = "{}",
        };
        var seededState = new ToolCallModuleState();
        seededState.PendingApprovals[$"{actorId}:tool-step:exec-1:{callId}:approval-1"] = pending;
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(seededState));

        var scheduler = new RecordingCallbackScheduler();
        var recovered = CreateAgent(actorId, store, scheduler, out var tool, out var publisher);
        await recovered.ActivateAsync();

        var scheduled = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        var retry = scheduled.TriggerEnvelope.Payload!
            .Unpack<WorkflowToolCallPublicationRetryFiredEvent>();
        retry.PublicationKind.Should().Be(WorkflowToolCallPublicationKind.Suspension);
        retry.ApprovalRequestId.Should().Be("approval-1");

        await recovered.HandleEventAsync(scheduled.TriggerEnvelope);

        tool.ExecuteCalls.Should().Be(0);
        publisher.Published.Select(x => x.Event).OfType<WorkflowSuspendedEvent>().Should().ContainSingle();
        recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .PendingApprovals.Values.Should().ContainSingle()
            .Which.Should().Match<PendingToolCallApprovalState>(value =>
                value.SuspensionPublished &&
                value.Suspension != null &&
                value.Suspension.ToolApproval.ApprovalRequestId == "approval-1");
    }

    [Fact]
    public async Task Activation_ShouldPublishSingleTypedContinuation_WhenRecoverySchedulingFails()
    {
        const string actorId = "run-tool-activation-scheduler-failure";
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(new ToolCallModuleState
            {
                Completions =
                {
                    new WorkflowToolCallCompletionOutboxEntry
                    {
                        RunId = actorId,
                        StepId = "tool-step",
                        CallId = $"workflow:{actorId}:tool-step:exec-1",
                        ExecutionId = "exec-1",
                        TerminalDecision = WorkflowToolCallTerminalDecision.NoApproval,
                        ToolCompletion = new WorkflowToolCallCompletedEvent
                        {
                            RunId = actorId,
                            StepId = "tool-step",
                            CallId = $"workflow:{actorId}:tool-step:exec-1",
                            Success = true,
                            ResultJson = "{}",
                        },
                        StepCompletion = new StepCompletedEvent
                        {
                            RunId = actorId,
                            StepId = "tool-step",
                            ExecutionId = "exec-1",
                            Success = true,
                            Output = "{}",
                        },
                    },
                },
            }));

        var scheduler = new RecordingCallbackScheduler(failSchedule: true);
        var recovered = CreateAgent(actorId, store, scheduler, out var tool, out var publisher);
        await recovered.ActivateAsync();

        scheduler.ScheduleAttempts.Should().Be(1);
        publisher.Published.Select(x => x.Event)
            .OfType<WorkflowToolCallPublicationRetryFiredEvent>()
            .Should().ContainSingle();
        tool.ExecuteCalls.Should().Be(0);
        publisher.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Should().BeEmpty();
        publisher.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
        var state = recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey].Unpack<ToolCallModuleState>();
        state.Completions.Should().ContainSingle();
        state.CompletionTombstones.Should().BeEmpty();
    }

    private static WorkflowRunGAgent CreateAgent(
        string actorId,
        InMemoryEventStore store,
        RecordingCallbackScheduler scheduler,
        out RecordingWorkflowTool tool,
        out RecordingEventPublisher publisher)
    {
        tool = new RecordingWorkflowTool("counting_tool");
        var module = new ToolCallModule(
            [new SingleWorkflowToolSource(tool)],
            NullLogger<ToolCallModule>.Instance);
        var moduleFactory = new ToolModuleFactory(module);
        var runtime = new UnsupportedActorRuntime();
        var pack = new ToolModulePack();
        publisher = new RecordingEventPublisher();
        var agent = new WorkflowRunGAgent(runtime, runtime, moduleFactory, [pack])
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkflowRunState>(store),
            EventPublisher = publisher,
            Services = new TestServiceProvider(scheduler),
            Logger = NullLogger.Instance,
        };
        SetAgentId(agent, actorId);
        return agent;
    }

    private static Task BindToolWorkflowAsync(WorkflowRunGAgent agent, string runId) =>
        agent.BindWorkflowRunDefinitionAsync(
            "definition-tool-recovery",
            """
            name: tool_recovery
            roles: []
            steps:
              - id: tool-step
                type: tool_call
                parameters:
                  tool: counting_tool
            """,
            "tool_recovery",
            null,
            runId,
            "scope-1",
            null,
            null,
            null,
            null,
            0,
            null,
            ExternalCapabilityExecutionMode.Interactive);

    private static void SetAgentId(GAgentBase agent, string agentId)
    {
        var method = typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(agent, [agentId]);
    }

    private sealed class ToolModulePack : IWorkflowModulePack
    {
        public string Name => "test.tool";

        public IReadOnlyList<WorkflowModuleRegistration> Modules { get; } =
        [
            WorkflowModuleRegistration.Create<ToolCallModule>("tool_call"),
        ];

        public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders { get; } =
        [
            new WorkflowStepTypeModuleDependencyExpander(),
        ];

        public IReadOnlyList<IWorkflowModuleConfigurator> Configurators { get; } = [];
    }

    private sealed class ToolModuleFactory(ToolCallModule module) : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? created)
        {
            created = string.Equals(name, "tool_call", StringComparison.OrdinalIgnoreCase) ? module : null;
            return created != null;
        }
    }

    private sealed class RecordingWorkflowTool(string name) : IWorkflowTool
    {
        public string Name { get; } = name;

        public int ExecuteCalls { get; private set; }

        public Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCalls++;
            return Task.FromResult(WorkflowToolExecutionResult.Success("{}"));
        }
    }

    private sealed class SingleWorkflowToolSource(IWorkflowTool tool) : IWorkflowToolSource
    {
        public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IWorkflowTool>>([tool]);
        }
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<(IMessage Event, TopologyAudience Audience)> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((evt.Descriptor.Parser.ParseFrom(evt.ToByteArray()), audience));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;
    }

    private sealed class RecordingCallbackScheduler(bool failSchedule = false) : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public int ScheduleAttempts { get; private set; }

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScheduleAttempts++;
            if (failSchedule)
                throw new InvalidOperationException("schedule failed");

            TimeoutRequests.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                TimeoutRequests.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class TestServiceProvider(IActorRuntimeCallbackScheduler scheduler) : IServiceProvider
    {
        public object? GetService(global::System.Type serviceType)
        {
            if (serviceType == typeof(IActorRuntimeCallbackScheduler))
                return scheduler;
            if (serviceType == typeof(IEnumerable<IGAgentExecutionHook>))
                return Array.Empty<IGAgentExecutionHook>();
            if (serviceType == typeof(IEnumerable<ICommittedStatePublicationHook>))
                return Array.Empty<ICommittedStatePublicationHook>();
            return null;
        }
    }

    private sealed class UnsupportedActorRuntime : IActorRuntime, IActorDispatchPort
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => throw new NotSupportedException();

        public Task<IActor> CreateAsync(global::System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default) =>
            Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }
}
