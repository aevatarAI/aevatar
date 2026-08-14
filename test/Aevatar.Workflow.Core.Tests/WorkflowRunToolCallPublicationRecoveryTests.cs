using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
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
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests;

#pragma warning disable CS0612 // Recovery coverage intentionally seeds and inspects legacy payload fields.
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
        const string legacyPayloadMarker = "legacy-tool-payload-must-not-be-rewritten";
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
            ArgumentsJson = legacyPayloadMarker,
            Input = legacyPayloadMarker,
            IdempotencyKey = legacyPayloadMarker,
            DisplayName = legacyPayloadMarker,
            ExternalInvocation = new ExternalToolInvocationSpec
            {
                CallSiteId = legacyPayloadMarker,
                ToolName = legacyPayloadMarker,
            },
            TimeoutMs = 60_000,
            TimeoutDeadlineUnixMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            ContinuationId = "continuation-1",
            ExecutionPhase = WorkflowToolCallExecutionPhase.ApprovalPending,
            InputFileRefs =
            {
                new WorkflowFileRef { FileId = legacyPayloadMarker },
            },
        };
        var seededState = new ToolCallModuleState();
        seededState.PendingApprovals[$"{actorId}:tool-step:exec-1:{callId}:approval-1"] = pending;
        var legacyExecution = CreatePendingExecution(
            actorId,
            2,
            WorkflowToolCallExecutionPhase.Unspecified);
        legacyExecution.ArgumentsJson = legacyPayloadMarker;
        legacyExecution.IdempotencyKey = legacyPayloadMarker;
        legacyExecution.DisplayName = legacyPayloadMarker;
        legacyExecution.ExternalInvocation = new ExternalToolInvocationSpec
        {
            CallSiteId = legacyPayloadMarker,
            ToolName = legacyPayloadMarker,
        };
        legacyExecution.InputFileRefs.Add(new WorkflowFileRef { FileId = legacyPayloadMarker });
        legacyExecution.TimeoutLease = new WorkflowRuntimeCallbackLeaseState
        {
            ActorId = actorId,
            CallbackId = legacyExecution.TimeoutCallbackId,
            Generation = 1,
            Backend = WorkflowRuntimeCallbackBackendState.Dedicated,
        };
        seededState.PendingExecutions[$"{legacyExecution.CallId}|{legacyExecution.ExecutionId}"] =
            legacyExecution;
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await PersistForTestAsync(seed, new WorkflowExecutionStateUpsertedEvent
        {
            ScopeKey = ToolCallModule.ModuleStateKey,
            State = Any.Pack(seededState),
        });

        var scheduler = new RecordingCallbackScheduler();
        var recovered = CreateAgent(actorId, store, scheduler, out var tool, out var publisher);
        await recovered.ActivateAsync();

        var scheduled = scheduler.TimeoutRequests
            .Where(request => request.TriggerEnvelope.Payload?.Is(
                WorkflowToolCallPublicationRetryFiredEvent.Descriptor) == true)
            .Should().ContainSingle().Subject;
        var retry = scheduled.TriggerEnvelope.Payload!
            .Unpack<WorkflowToolCallPublicationRetryFiredEvent>();
        retry.PublicationKind.Should().Be(WorkflowToolCallPublicationKind.Suspension);
        retry.ApprovalRequestId.Should().Be("approval-1");
        var approvalWatchdog = scheduler.TimeoutRequests
            .Where(request => request.TriggerEnvelope.Payload?.Is(
                WorkflowToolCallTimeoutFiredEvent.Descriptor) == true)
            .Should().ContainSingle().Subject;
        approvalWatchdog.TriggerEnvelope.Payload!
            .Unpack<WorkflowToolCallTimeoutFiredEvent>()
            .ContinuationId.Should().Be("continuation-1");

        var recoveredToolState = recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>();
        var recoveredPending = recoveredToolState.PendingApprovals.Values.Should().ContainSingle().Subject;
        recoveredPending.TimeoutCallbackId.Should().Be(approvalWatchdog.CallbackId);
        recoveredPending.TimeoutLease.Should().NotBeNull();
        recoveredPending.TimeoutLease.CallbackId.Should().Be(approvalWatchdog.CallbackId);
        AssertLegacyPayloadFieldsScrubbed(recoveredToolState);

        var persistedToolStates = (await store.GetEventsAsync(actorId))
            .Where(evt => evt.EventData?.Is(WorkflowExecutionStateUpsertedEvent.Descriptor) == true)
            .Select(evt => evt.EventData!.Unpack<WorkflowExecutionStateUpsertedEvent>())
            .Where(evt =>
                evt.ScopeKey == ToolCallModule.ModuleStateKey &&
                evt.State?.Is(ToolCallModuleState.Descriptor) == true)
            .Select(evt => evt.State!.Unpack<ToolCallModuleState>())
            .ToList();
        persistedToolStates.Should().HaveCountGreaterThan(1);
        persistedToolStates[0].PendingApprovals.Values.Single().ArgumentsJson
            .Should().Be(legacyPayloadMarker, "the fixture must contain a real legacy journal payload");
        AssertLegacyPayloadFieldsScrubbed(persistedToolStates[^1]);

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
    public async Task Activation_WhenApprovalChangesDuringWatchdogSchedule_ShouldCancelOrphanLease()
    {
        const string actorId = "run-tool-approval-watchdog-orphan";
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreatePendingApprovalToolState(actorId)));

        WorkflowRunGAgent? recovered = null;
        var stateRemoved = false;
        var scheduler = new RecordingCallbackScheduler(
            afterSchedule: async (request, ct) =>
            {
                if (stateRemoved ||
                    request.TriggerEnvelope.Payload?.Is(WorkflowToolCallTimeoutFiredEvent.Descriptor) != true)
                {
                    return;
                }

                stateRemoved = true;
                await ((IWorkflowExecutionStateHost)recovered!).ClearExecutionStateAsync(
                    ToolCallModule.ModuleStateKey,
                    ct);
            });
        recovered = CreateAgent(actorId, store, scheduler, out var tool, out _);

        await recovered.ActivateAsync();

        var watchdog = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        scheduler.CancelledLeases.Should().ContainSingle().Which.CallbackId.Should().Be(watchdog.CallbackId);
        recovered.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        tool.ExecuteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Activation_WhenApprovalWatchdogCheckpointPublicationFails_ShouldKeepCommittedLease()
    {
        const string actorId = "run-tool-approval-watchdog-committed";
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreatePendingApprovalToolState(actorId)));

        var scheduler = new RecordingCallbackScheduler();
        var hook = new FailOnceCommittedPublicationHook { FailNext = true };
        var recovered = CreateAgent(
            actorId,
            store,
            scheduler,
            out var tool,
            out _,
            publicationHook: hook);

        await FluentActions.Awaiting(() => recovered.ActivateAsync())
            .Should().ThrowAsync<CommittedStatePublicationException>();

        var watchdog = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        scheduler.CancelledLeases.Should().BeEmpty();
        var pending = recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .PendingApprovals.Values.Should().ContainSingle().Subject;
        pending.TimeoutCallbackId.Should().Be(watchdog.CallbackId);
        pending.TimeoutLease.Should().NotBeNull();
        pending.TimeoutLease.CallbackId.Should().Be(watchdog.CallbackId);
        tool.ExecuteCalls.Should().Be(0);
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

    [Fact]
    public async Task RuntimePublicationRetry_WithPendingToolState_ShouldRestoreAllActorLocalContinuationsInOrder()
    {
        const string actorId = "run-tool-runtime-recovery";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var hook = new FailOnceCommittedPublicationHook();
        var agent = CreateAgent(actorId, store, scheduler, out var tool, out _, publicationHook: hook);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);

        hook.FailNext = true;
        var pendingState = CreateRecoverableToolState(actorId);
        var publicationFailure = await FluentActions.Awaiting(() =>
                ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
                    ToolCallModule.ModuleStateKey,
                    Any.Pack(pendingState)))
            .Should().ThrowAsync<CommittedStatePublicationException>();
        publicationFailure.Which.Stage.Should().Be(CommittedStatePublicationFailureStage.AdapterAcceptance);
        scheduler.TimeoutRequests.Clear();

        await agent.HandleEventAsync(CreatePublicationRetryEnvelope(
            actorId,
            new WorkflowToolCallExecutionRecoveryFiredEvent()));

        var scheduledPayloads = scheduler.TimeoutRequests
            .Select(static request => request.TriggerEnvelope.Payload)
            .ToArray();
        scheduledPayloads.Should().HaveCount(5);
        scheduledPayloads.Take(2).Should().OnlyContain(payload =>
            payload != null && payload.Is(WorkflowToolCallTimeoutFiredEvent.Descriptor));
        scheduledPayloads[2]!.Is(WorkflowToolCallRetryFiredEvent.Descriptor).Should().BeTrue();
        scheduledPayloads[3]!.Is(WorkflowToolCallExecutionRecoveryFiredEvent.Descriptor).Should().BeTrue();
        scheduledPayloads[4]!.Is(WorkflowToolCallPublicationRetryFiredEvent.Descriptor).Should().BeTrue();
        scheduler.TimeoutRequests.Should().OnlyContain(request => request.ActorId == actorId);
        hook.FailureCount.Should().Be(1);
        tool.ExecuteCalls.Should().Be(0);
        agent.GetModules().Should().NotBeEmpty();
    }

    [Fact]
    public async Task BindPublicationRetry_ShouldRebuildCompiledDefinitionAndExecutionModules()
    {
        const string actorId = "run-tool-bind-publication-recovery";
        var store = new InMemoryEventStore();
        var hook = new FailOnceCommittedPublicationHook { FailNext = true };
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            publicationHook: hook);
        await agent.ActivateAsync();
        var bind = CreateBindEvent(actorId);
        var original = EnvelopeFrom("workflow-run-actor-port", bind);

        await FluentActions.Awaiting(() => agent.HandleEventAsync(original))
            .Should().ThrowAsync<CommittedStatePublicationException>();

        agent.State.Compiled.Should().BeTrue();
        agent.State.WorkflowYaml.Should().Be(bind.WorkflowYaml);
        GetCompiledWorkflow(agent).Should().BeNull();
        agent.GetModules().Should().BeEmpty();

        await agent.HandleEventAsync(CreatePublicationRetryEnvelope(original));

        GetCompiledWorkflow(agent).Should().NotBeNull();
        GetCompiledWorkflow(agent)!.Name.Should().Be("tool_recovery");
        agent.GetModules().Should().Contain(module => module.Name == "workflow_execution_bridge");
        agent.State.WorkflowYaml.Should().Be(bind.WorkflowYaml);
        hook.FailureCount.Should().Be(1);
    }

    [Fact]
    public async Task DynamicBindPublicationRetry_ShouldDrainCommittedStartContinuationExactlyOnce()
    {
        const string actorId = "run-tool-dynamic-bind-recovery";
        const string replacementInput = "replacement-input";
        var store = new InMemoryEventStore();
        var hook = new FailOnceCommittedPublicationHook();
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out var publisher,
            publicationHook: hook);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        hook.FailNext = true;
        var original = EnvelopeFrom("workflow-runtime", new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = """
                           name: tool_replacement
                           roles: []
                           steps:
                             - id: tool-step
                               type: tool_call
                               parameters:
                                 tool: counting_tool
                           """,
            Input = replacementInput,
        });

        await FluentActions.Awaiting(() => agent.HandleEventAsync(original))
            .Should().ThrowAsync<CommittedStatePublicationException>();

        agent.State.Status.Should().Be("bound");
        agent.State.PendingDefinitionBindingContinuation.Should().NotBeNull();
        publisher.Published.Select(static item => item.Event)
            .OfType<StartWorkflowEvent>().Should().BeEmpty();

        await agent.HandleEventAsync(CreatePublicationRetryEnvelope(original));

        agent.State.Status.Should().Be("running");
        agent.State.Input.Should().Be(replacementInput);
        agent.State.PendingDefinitionBindingContinuation.Should().BeNull();
        GetCompiledWorkflow(agent).Should().NotBeNull();
        GetCompiledWorkflow(agent)!.Name.Should().Be("tool_replacement");
        publisher.Published.Select(static item => item.Event)
            .OfType<StartWorkflowEvent>().Should().ContainSingle()
            .Which.Input.Should().Be(replacementInput);
        var events = await store.GetEventsAsync(actorId);
        events.Count(evt => evt.EventData?.Is(BindWorkflowRunDefinitionEvent.Descriptor) == true)
            .Should().Be(2);
        events.Count(evt => evt.EventData?.Is(WorkflowRunExecutionStartedEvent.Descriptor) == true)
            .Should().Be(1);
        hook.FailureCount.Should().Be(1);
    }

    [Fact]
    public async Task DynamicBind_WithPendingToolState_ShouldRejectWithoutClearingExecution()
    {
        const string actorId = "run-tool-dynamic-bind-pending";
        var store = new InMemoryEventStore();
        var secretStore = new RecordingRuntimeSecretStore();
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out var publisher,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        var pending = CreateTerminalToolState(
            actorId,
            CreateProtectedReference("material-dynamic-active", actorId, 1));
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(pending));
        var workflowYamlBefore = agent.State.WorkflowYaml;

        await agent.HandleEventAsync(EnvelopeFrom(
            "workflow-runtime",
            new ReplaceWorkflowDefinitionAndExecuteEvent
            {
                WorkflowYaml = """
                               name: rejected_replacement
                               roles: []
                               steps:
                                 - id: tool-step
                                   type: tool_call
                                   parameters:
                                     tool: counting_tool
                               """,
                Input = "must-not-start",
            }));

        agent.State.WorkflowYaml.Should().Be(workflowYamlBefore);
        agent.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>().Should().BeEquivalentTo(pending);
        agent.State.PendingDefinitionBindingContinuation.Should().BeNull();
        secretStore.RevokeRequests.Should().BeEmpty();
        publisher.Published.Select(static item => item.Event)
            .OfType<StartWorkflowEvent>().Should().BeEmpty();
        publisher.Published.Select(static item => item.Event)
            .OfType<WorkflowLlmInvocationCompletedEvent>().Should().ContainSingle()
            .Which.Success.Should().BeFalse();
        (await store.GetEventsAsync(actorId))
            .Count(evt => evt.EventData?.Is(BindWorkflowRunDefinitionEvent.Descriptor) == true)
            .Should().Be(1);
    }

    [Fact]
    public async Task Bind_WithPendingDefinitionContinuation_ShouldRejectBeforeCommitWithoutOverwritingContinuation()
    {
        const string actorId = "run-tool-bind-continuation-pending";
        var store = new InMemoryEventStore();
        var hook = new FailOnceCommittedPublicationHook { FailNext = true };
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            publicationHook: hook);
        await agent.ActivateAsync();
        var original = EnvelopeFrom("workflow-run-actor-port", CreateBindEvent(actorId));

        await FluentActions.Awaiting(() => agent.HandleEventAsync(original))
            .Should().ThrowAsync<CommittedStatePublicationException>();

        agent.State.PendingDefinitionBindingContinuation.Should().NotBeNull();
        var pending = agent.State.PendingDefinitionBindingContinuation!.Clone();
        var eventCountBefore = (await store.GetEventsAsync(actorId)).Count;

        await FluentActions.Awaiting(() => BindToolWorkflowAsync(agent, actorId))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*definition binding cleanup is pending*");

        agent.State.PendingDefinitionBindingContinuation.Should().BeEquivalentTo(pending);
        (await store.GetEventsAsync(actorId)).Should().HaveCount(eventCountBefore);
    }

    [Fact]
    public async Task DynamicBind_WithPendingDefinitionContinuation_ShouldRejectWithoutOverwritingContinuation()
    {
        const string actorId = "run-tool-dynamic-bind-continuation-pending";
        var store = new InMemoryEventStore();
        var hook = new FailOnceCommittedPublicationHook { FailNext = true };
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out var publisher,
            publicationHook: hook);
        await agent.ActivateAsync();

        await FluentActions.Awaiting(() => agent.HandleEventAsync(
                EnvelopeFrom("workflow-run-actor-port", CreateBindEvent(actorId))))
            .Should().ThrowAsync<CommittedStatePublicationException>();

        agent.State.PendingDefinitionBindingContinuation.Should().NotBeNull();
        var pending = agent.State.PendingDefinitionBindingContinuation!.Clone();
        var eventCountBefore = (await store.GetEventsAsync(actorId)).Count;

        await agent.HandleEventAsync(EnvelopeFrom(
            "workflow-runtime",
            new ReplaceWorkflowDefinitionAndExecuteEvent
            {
                WorkflowYaml = """
                               name: rejected_replacement
                               roles: []
                               steps:
                                 - id: tool-step
                                   type: tool_call
                                   parameters:
                                     tool: counting_tool
                               """,
                Input = "must-not-start",
            }));

        agent.State.PendingDefinitionBindingContinuation.Should().BeEquivalentTo(pending);
        (await store.GetEventsAsync(actorId)).Should().HaveCount(eventCountBefore);
        publisher.Published.Select(static item => item.Event)
            .OfType<StartWorkflowEvent>().Should().BeEmpty();
        publisher.Published.Select(static item => item.Event)
            .OfType<WorkflowLlmInvocationCompletedEvent>().Should().ContainSingle()
            .Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Bind_WithPendingToolState_ShouldFailBeforeCommitWithoutRevokingActiveMaterial()
    {
        const string actorId = "run-tool-bind-pending";
        var store = new InMemoryEventStore();
        var secretStore = new RecordingRuntimeSecretStore();
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(actorId, CreateProtectedReference("material-active", actorId, 1))));

        await FluentActions.Awaiting(() => BindToolWorkflowAsync(agent, actorId))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*tool call cleanup is pending*");

        secretStore.RevokeRequests.Should().BeEmpty();
        agent.State.WorkflowYaml.Should().BeEmpty();
        agent.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);
        (await store.GetEventsAsync(actorId)).Should().ContainSingle();
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("stopped")]
    [InlineData("run-stopped")]
    public async Task TerminalPublicationRetry_ShouldCleanupPendingToolStateAndDisableModules(string terminalKind)
    {
        const string actorId = "run-tool-terminal-publication-recovery";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var secretStore = new RecordingRuntimeSecretStore();
        var hook = new FailOnceCommittedPublicationHook();
        var agent = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out _,
            runtimeSecretStore: secretStore,
            publicationHook: hook);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(actorId, CreateProtectedReference("material-terminal", actorId, 1))));
        hook.FailNext = true;
        var terminal = terminalKind switch
        {
            "completed" => new WorkflowCompletedEvent
            {
                RunId = actorId,
                WorkflowName = "tool_recovery",
                Success = true,
                Output = "done",
            },
            "stopped" => new WorkflowStoppedEvent
            {
                RunId = actorId,
                WorkflowName = "tool_recovery",
                Reason = "operator stop",
            },
            _ => (IMessage)new WorkflowRunStoppedEvent
            {
                RunId = actorId,
                Reason = "runtime stop",
            },
        };
        var original = EnvelopeFrom(actorId, terminal);

        await FluentActions.Awaiting(() => agent.HandleEventAsync(original))
            .Should().ThrowAsync<CommittedStatePublicationException>();

        agent.State.Status.Should().Be(terminalKind == "completed" ? "completed" : "stopped");
        agent.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);
        scheduler.CancelledLeases.Should().BeEmpty();
        secretStore.RevokeRequests.Should().BeEmpty();
        agent.GetModules().Should().NotBeEmpty();

        await agent.HandleEventAsync(CreatePublicationRetryEnvelope(original));

        scheduler.CancelledLeases.Select(static lease => lease.CallbackId)
            .Should().BeEquivalentTo("timeout-1", "retry-1");
        secretStore.RevokeRequests.Should().ContainSingle(request => request.Ref == "material-terminal");
        agent.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        agent.GetModules().Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalCleanup_ShouldCancelPendingApprovalWatchdog()
    {
        const string actorId = "run-tool-terminal-approval-watchdog";
        const string callbackId = "approval-timeout-1";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(actorId, store, scheduler, out var tool, out var publisher);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        var toolState = CreatePendingApprovalToolState(actorId);
        var pending = toolState.PendingApprovals.Values.Should().ContainSingle().Subject;
        pending.TimeoutCallbackId = callbackId;
        pending.TimeoutLease = new WorkflowRuntimeCallbackLeaseState
        {
            ActorId = actorId,
            CallbackId = callbackId,
            Generation = 7,
            SlotEpoch = 11,
            Backend = WorkflowRuntimeCallbackBackendState.Dedicated,
        };
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(toolState));

        await agent.HandleEventAsync(EnvelopeFrom(actorId, new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        }));

        var cancelled = scheduler.CancelledLeases.Should().ContainSingle().Subject;
        cancelled.ActorId.Should().Be(actorId);
        cancelled.CallbackId.Should().Be(callbackId);
        cancelled.Generation.Should().Be(7);
        cancelled.SlotEpoch.Should().Be(11);
        agent.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        agent.State.Status.Should().Be("completed");
        agent.GetModules().Should().BeEmpty();
        tool.ExecuteCalls.Should().Be(0);
        publisher.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task AdoptedTerminalPublicationRetry_ShouldCleanupLocalToolStateWithoutReexecutingAdoption()
    {
        const string actorId = "run-tool-adopted-publication-recovery";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var secretStore = new RecordingRuntimeSecretStore();
        var hook = new FailOnceCommittedPublicationHook();
        var agent = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out _,
            runtimeSecretStore: secretStore,
            publicationHook: hook);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await PersistForTestAsync(agent, new WorkflowRunExecutionStartedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            ScopeId = "scope-1",
            StartedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(actorId, CreateProtectedReference("material-adopted", actorId, 1))));
        hook.FailNext = true;
        var original = EnvelopeFrom("inner-workflow-executor", new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = false,
            Error = "inner failure",
        });

        await FluentActions.Awaiting(() => agent.HandleEventAsync(original))
            .Should().ThrowAsync<CommittedStatePublicationException>();
        agent.State.Status.Should().Be("failed");
        agent.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);

        await agent.HandleEventAsync(CreatePublicationRetryEnvelope(original));

        secretStore.RevokeRequests.Should().ContainSingle(request => request.Ref == "material-adopted");
        scheduler.CancelledLeases.Should().HaveCount(2);
        agent.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        agent.GetModules().Should().BeEmpty();
        (await store.GetEventsAsync(actorId))
            .Count(evt => evt.EventData?.Is(WorkflowCompletedEvent.Descriptor) == true)
            .Should().Be(1);
    }

    [Fact]
    public async Task TerminalCleanup_WhenOnlySomeRevocationsSucceed_ShouldPersistFailedReferencesForActivationRetry()
    {
        const string actorId = "run-tool-partial-revocation";
        var store = new InMemoryEventStore();
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.FailReferences.Add("material-b");
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(
                actorId,
                CreateProtectedReference("material-a", actorId, 1),
                CreateProtectedReference("material-b", actorId, 2))));

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        var retained = agent.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>();
        retained.PendingExecutions.Values.Should().ContainSingle(pending =>
            pending.ProtectedMaterialReference != null &&
            pending.ProtectedMaterialReference.Ref == "material-b");
        retained.PendingExecutions.Values.Should().ContainSingle(pending =>
            pending.ProtectedMaterialReference == null);
        agent.GetModules().Should().BeEmpty();

        secretStore.FailReferences.Clear();
        var recovered = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            runtimeSecretStore: secretStore);
        await recovered.ActivateAsync();

        secretStore.RevokeRequests.Count(request => request.Ref == "material-a").Should().Be(1);
        secretStore.RevokeRequests.Count(request => request.Ref == "material-b").Should().Be(2);
        recovered.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        recovered.GetModules().Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalCleanup_WhenCompletionOutboxRevocationFails_ShouldRetainHandleForActivationRetry()
    {
        const string actorId = "run-tool-outbox-revocation";
        var store = new InMemoryEventStore();
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.FailReferences.Add("material-outbox");
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        var toolState = new ToolCallModuleState
        {
            Completions =
            {
                new WorkflowToolCallCompletionOutboxEntry
                {
                    RunId = actorId,
                    StepId = "tool-step",
                    CallId = $"workflow:{actorId}:tool-step:exec-outbox",
                    ExecutionId = "exec-outbox",
                    TerminalDecision = WorkflowToolCallTerminalDecision.NoApproval,
                    ProtectedMaterialReference = CreateProtectedReference("material-outbox", actorId, 1),
                },
            },
        };
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(toolState));

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        var retained = agent.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>();
        retained.Completions.Should().ContainSingle()
            .Which.ProtectedMaterialReference!.Ref.Should().Be("material-outbox");
        agent.GetModules().Should().BeEmpty();

        secretStore.FailReferences.Clear();
        var recovered = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            runtimeSecretStore: secretStore);
        await recovered.ActivateAsync();

        secretStore.RevokeRequests.Count(request => request.Ref == "material-outbox").Should().Be(2);
        recovered.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        recovered.GetModules().Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalCleanup_WhenRevocationFailsTransiently_ShouldRetryWithinSameActivation()
    {
        const string actorId = "run-tool-terminal-cleanup-retry";
        const string materialReference = "material-transient";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.ThrowReferences.Add(materialReference);
        var agent = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out _,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(
                actorId,
                CreateProtectedReference(materialReference, actorId, 1))));

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        agent.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);
        var scheduled = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        scheduled.CallbackId.Should().Contain("workflow-tool-terminal-cleanup-retry");
        scheduled.TriggerEnvelope.Payload!
            .Unpack<WorkflowToolCallTerminalCleanupRetryFiredEvent>()
            .RunId.Should().Be(actorId);

        secretStore.ThrowReferences.Clear();
        await agent.HandleEventAsync(scheduled.TriggerEnvelope);

        secretStore.RevokeRequests.Count(request => request.Ref == materialReference).Should().Be(2);
        secretStore.ResolveRequests.Count(request => request.Ref == materialReference).Should().Be(1);
        agent.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        agent.GetModules().Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalCleanup_WhenProtectedMaterialIsAlreadyUnavailable_ShouldClearStateWithoutRetry()
    {
        const string actorId = "run-tool-terminal-cleanup-unavailable";
        const string materialReference = "material-unavailable";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.UnavailableReferences.Add(materialReference);
        var agent = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out _,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(
                actorId,
                CreateProtectedReference(materialReference, actorId, 1))));

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        secretStore.RevokeRequests.Should().ContainSingle(request => request.Ref == materialReference);
        secretStore.ResolveRequests.Should().ContainSingle(request => request.Ref == materialReference);
        scheduler.TimeoutRequests.Should().BeEmpty();
        agent.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        agent.GetModules().Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalCleanup_WhenRetrySchedulingFails_ShouldPublishTypedSelfContinuation()
    {
        const string actorId = "run-tool-terminal-cleanup-scheduler-failure";
        const string materialReference = "material-scheduler-failure";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler(failSchedule: true);
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.ThrowReferences.Add(materialReference);
        var agent = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out var publisher,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(
                actorId,
                CreateProtectedReference(materialReference, actorId, 1))));

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        scheduler.ScheduleAttempts.Should().Be(1);
        publisher.Published
            .Where(item =>
                item.Audience == TopologyAudience.Self &&
                item.Event is WorkflowToolCallTerminalCleanupRetryFiredEvent)
            .Should().ContainSingle()
            .Which.Event.Should().BeOfType<WorkflowToolCallTerminalCleanupRetryFiredEvent>()
            .Which.RunId.Should().Be(actorId);
        agent.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);
    }

    [Fact]
    public async Task TerminalCleanup_WhenTypedFallbackCannotScheduleAgain_ShouldNotRepublishImmediateContinuation()
    {
        const string actorId = "run-tool-terminal-cleanup-fallback-exhausted";
        const string materialReference = "material-fallback-exhausted";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler(failSchedule: true);
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.ThrowReferences.Add(materialReference);
        var agent = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out var publisher,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(
                actorId,
                CreateProtectedReference(materialReference, actorId, 1))));

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });
        var fallback = publisher.Published
            .Select(static item => item.Event)
            .OfType<WorkflowToolCallTerminalCleanupRetryFiredEvent>()
            .Should().ContainSingle().Subject;

        var failure = await FluentActions.Awaiting(() =>
                agent.HandleEventAsync(EnvelopeFrom(actorId, fallback)))
            .Should().ThrowAsync<WorkflowDurablePublicationPendingException>();

        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        scheduler.ScheduleAttempts.Should().Be(2);
        publisher.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallTerminalCleanupRetryFiredEvent>()
            .Should().ContainSingle();
        agent.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);
    }

    [Fact]
    public async Task TerminalCleanup_WhenCompletedEnvelopeIsRedelivered_ShouldFinishWithoutRepeatingTerminalSideEffects()
    {
        const string actorId = "run-tool-terminal-cleanup-redelivery";
        const string materialReference = "material-terminal-cleanup-redelivery";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler(failSchedule: true);
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.ThrowReferences.Add(materialReference);
        var agent = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out var publisher,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(
                actorId,
                CreateProtectedReference(materialReference, actorId, 1))));
        publisher.FailNextPublishType = typeof(WorkflowToolCallTerminalCleanupRetryFiredEvent);
        var original = EnvelopeFrom(actorId, new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        var firstFailure = await FluentActions.Awaiting(() => agent.HandleEventAsync(original))
            .Should().ThrowAsync<WorkflowDurablePublicationPendingException>();

        firstFailure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        agent.State.Status.Should().Be("completed");
        agent.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);
        agent.GetModules().Should().BeEmpty();
        publisher.Published.Count(item =>
                item.Audience == TopologyAudience.Parent &&
                item.Event is WorkflowCompletedEvent)
            .Should().Be(1);
        publisher.Published.Count(item =>
                item.Audience == TopologyAudience.Parent &&
                item.Event is WorkflowLlmInvocationCompletedEvent)
            .Should().Be(1);
        (await store.GetEventsAsync(actorId))
            .Count(evt => evt.EventData?.Is(WorkflowCompletedEvent.Descriptor) == true)
            .Should().Be(1);

        secretStore.ThrowReferences.Clear();
        var redelivery = original.Clone();
        redelivery.EnsureRuntime().Retry = new EnvelopeRetryContext
        {
            OriginEventId = original.Id,
            Attempt = 1,
            LastErrorType = nameof(WorkflowDurablePublicationPendingException),
        };
        await agent.HandleEventAsync(redelivery);

        secretStore.RevokeRequests.Count(request => request.Ref == materialReference).Should().Be(2);
        agent.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        agent.GetModules().Should().BeEmpty();
        publisher.Published.Count(item =>
                item.Audience == TopologyAudience.Parent &&
                item.Event is WorkflowCompletedEvent)
            .Should().Be(1);
        publisher.Published.Count(item =>
                item.Audience == TopologyAudience.Parent &&
                item.Event is WorkflowLlmInvocationCompletedEvent)
            .Should().Be(1);
        publisher.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallTerminalCleanupRetryFiredEvent>()
            .Should().BeEmpty();
        (await store.GetEventsAsync(actorId))
            .Count(evt => evt.EventData?.Is(WorkflowCompletedEvent.Descriptor) == true)
            .Should().Be(1);
    }

    [Fact]
    public async Task TerminalActivationRecovery_WhenCleanupSchedulerIsUnavailable_ShouldNotPublishImmediateContinuation()
    {
        const string actorId = "run-tool-terminal-activation-cleanup";
        const string materialReference = "material-terminal-activation-cleanup";
        var store = new InMemoryEventStore();
        var seed = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(
                actorId,
                CreateProtectedReference(materialReference, actorId, 1))));
        await PersistForTestAsync(seed, new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        var scheduler = new RecordingCallbackScheduler(failSchedule: true);
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.ThrowReferences.Add(materialReference);
        var recovered = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out var publisher,
            runtimeSecretStore: secretStore);

        var failure = await FluentActions.Awaiting(() => recovered.ActivateAsync())
            .Should().ThrowAsync<WorkflowDurablePublicationPendingException>();

        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        scheduler.ScheduleAttempts.Should().Be(1);
        recovered.State.Status.Should().Be("completed");
        recovered.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);
        recovered.GetModules().Should().BeEmpty();
        publisher.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallTerminalCleanupRetryFiredEvent>()
            .Should().BeEmpty();
    }

    private static ToolCallModuleState CreateRecoverableToolState(string runId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var state = new ToolCallModuleState();
        var executing = CreatePendingExecution(runId, 1, WorkflowToolCallExecutionPhase.ExecutionPending);
        var retrying = CreatePendingExecution(runId, 2, WorkflowToolCallExecutionPhase.RetryPending);
        retrying.RetryCallbackId = "retry-recovery-2";
        retrying.RetryDueUnixMs = now + 30_000;
        state.PendingExecutions[$"{executing.CallId}|{executing.ExecutionId}"] = executing;
        state.PendingExecutions[$"{retrying.CallId}|{retrying.ExecutionId}"] = retrying;
        state.Completions.Add(new WorkflowToolCallCompletionOutboxEntry
        {
            RunId = runId,
            StepId = "tool-step",
            CallId = $"workflow:{runId}:tool-step:exec-completed",
            ExecutionId = "exec-completed",
            TerminalDecision = WorkflowToolCallTerminalDecision.NoApproval,
            ToolCompletion = new WorkflowToolCallCompletedEvent
            {
                RunId = runId,
                StepId = "tool-step",
                CallId = $"workflow:{runId}:tool-step:exec-completed",
                Success = true,
                ResultJson = "{}",
            },
            StepCompletion = new StepCompletedEvent
            {
                RunId = runId,
                StepId = "tool-step",
                ExecutionId = "exec-completed",
                Success = true,
                Output = "{}",
            },
        });
        return state;
    }

    private static void AssertLegacyPayloadFieldsScrubbed(ToolCallModuleState state)
    {
        var approval = state.PendingApprovals.Values.Should().ContainSingle().Subject;
        approval.ArgumentsJson.Should().BeEmpty();
        approval.Input.Should().BeEmpty();
        approval.InputFileRefs.Should().BeEmpty();
        approval.IdempotencyKey.Should().BeEmpty();
        approval.ExternalInvocation.Should().BeNull();
        approval.DisplayName.Should().BeEmpty();

        var execution = state.PendingExecutions.Values.Should().ContainSingle().Subject;
        execution.ArgumentsJson.Should().BeEmpty();
        execution.InputFileRefs.Should().BeEmpty();
        execution.IdempotencyKey.Should().BeEmpty();
        execution.ExternalInvocation.Should().BeNull();
        execution.DisplayName.Should().BeEmpty();
    }

    private static ToolCallModuleState CreatePendingApprovalToolState(string runId)
    {
        var callId = $"workflow:{runId}:tool-step:exec-1";
        var pending = new PendingToolCallApprovalState
        {
            RunId = runId,
            StepId = "tool-step",
            ExecutionId = "exec-1",
            ToolName = "counting_tool",
            ToolCallId = callId,
            ApprovalRequestId = "approval-1",
            TimeoutMs = 60_000,
            TimeoutDeadlineUnixMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            ContinuationId = "continuation-1",
            ExecutionPhase = WorkflowToolCallExecutionPhase.ApprovalPending,
        };
        var state = new ToolCallModuleState();
        state.PendingApprovals[$"{runId}:tool-step:exec-1:{callId}:approval-1"] = pending;
        return state;
    }

    private static ToolCallModuleState CreateTerminalToolState(
        string runId,
        params RuntimeSecretReference[] references)
    {
        var state = new ToolCallModuleState();
        for (var i = 0; i < references.Length; i++)
        {
            var index = i + 1;
            var pending = CreatePendingExecution(
                runId,
                index,
                WorkflowToolCallExecutionPhase.ExecutionPending);
            pending.ProtectedMaterialReference = references[i].Clone();
            pending.ProtectedMaterialDigestSha256 = $"digest-{index}";
            pending.TimeoutLease = new WorkflowRuntimeCallbackLeaseState
            {
                ActorId = runId,
                CallbackId = $"timeout-{index}",
                Generation = index,
                Backend = WorkflowRuntimeCallbackBackendState.InMemory,
            };
            pending.RetryLease = new WorkflowRuntimeCallbackLeaseState
            {
                ActorId = runId,
                CallbackId = $"retry-{index}",
                Generation = index,
                Backend = WorkflowRuntimeCallbackBackendState.InMemory,
            };
            state.PendingExecutions[$"{pending.CallId}|{pending.ExecutionId}"] = pending;
        }

        return state;
    }

    private static PendingToolCallExecutionState CreatePendingExecution(
        string runId,
        int index,
        WorkflowToolCallExecutionPhase phase) =>
        new()
        {
            RunId = runId,
            StepId = "tool-step",
            ExecutionId = $"exec-{index}",
            ToolName = "counting_tool",
            CallId = $"workflow:{runId}:tool-step:exec-{index}",
            TimeoutMs = 60_000,
            TimeoutDeadlineUnixMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            TimeoutCallbackId = $"timeout-recovery-{index}",
            Attempt = 1,
            ContinuationId = $"continuation-{index}",
            ExecutionPhase = phase,
        };

    private static RuntimeSecretReference CreateProtectedReference(string reference, string runId, int index) =>
        new()
        {
            Ref = reference,
            Purpose = CredentialSecretPurposes.WorkflowToolCallProtectedMaterial,
            OwnerRunId = runId,
            OwnerStepId = "tool-step",
            Fingerprint = $"sha256:{index}",
            ConsumeOnce = false,
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
        };

    private static BindWorkflowRunDefinitionEvent CreateBindEvent(string runId) =>
        new()
        {
            DefinitionActorId = "definition-tool-recovery",
            WorkflowName = "tool_recovery",
            WorkflowYaml = """
                           name: tool_recovery
                           roles: []
                           steps:
                             - id: tool-step
                               type: tool_call
                               parameters:
                                 tool: counting_tool
                           """,
            RunId = runId,
            ScopeId = "scope-1",
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            ReusePolicy = WorkflowRunActorReusePolicy.SingleRun,
        };

    private static EventEnvelope EnvelopeFrom(string publisherActorId, IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                publisherActorId,
                TopologyAudience.Self),
        };

    private static EventEnvelope CreatePublicationRetryEnvelope(string actorId, IMessage payload) =>
        CreatePublicationRetryEnvelope(EnvelopeFrom(actorId, payload));

    private static EventEnvelope CreatePublicationRetryEnvelope(EventEnvelope original)
    {
        var retry = original.Clone();
        retry.EnsureRuntime().Retry = new EnvelopeRetryContext
        {
            OriginEventId = original.Id,
            Attempt = 1,
            LastErrorType = nameof(CommittedStatePublicationException),
        };
        return retry;
    }

    private static WorkflowDefinition? GetCompiledWorkflow(WorkflowRunGAgent agent) =>
        (WorkflowDefinition?)typeof(WorkflowRunGAgent)
            .GetField("_compiledWorkflow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent);

    private static async Task PersistForTestAsync(WorkflowRunGAgent agent, IMessage evt)
    {
        var method = typeof(GAgentBase<WorkflowRunState>)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate =>
                candidate.Name == "PersistDomainEventAsync" &&
                candidate.IsGenericMethodDefinition &&
                candidate.GetParameters().Length == 2 &&
                candidate.GetParameters()[0].ParameterType.IsGenericParameter);
        var task = (Task)method.MakeGenericMethod(evt.GetType())
            .Invoke(agent, [evt, CancellationToken.None])!;
        await task;
    }

    private static WorkflowRunGAgent CreateAgent(
        string actorId,
        InMemoryEventStore store,
        RecordingCallbackScheduler scheduler,
        out RecordingWorkflowTool tool,
        out RecordingEventPublisher publisher,
        IRuntimeSecretStore? runtimeSecretStore = null,
        ICommittedStatePublicationHook? publicationHook = null)
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
            Services = new TestServiceProvider(scheduler, runtimeSecretStore, publicationHook),
            Logger = NullLogger.Instance,
        };
        SetAgentId(agent, actorId);
        return agent;
    }

    private static Task BindToolWorkflowAsync(WorkflowRunGAgent agent, string runId)
    {
        var bind = CreateBindEvent(runId);
        return agent.BindWorkflowRunDefinitionAsync(
            bind.DefinitionActorId,
            bind.WorkflowYaml,
            bind.WorkflowName,
            bind.InlineWorkflowYamls,
            bind.RunId,
            bind.ScopeId,
            bind.RunOrigin,
            bind.ScheduleId,
            bind.WorkflowId,
            bind.RevisionId,
            bind.DefinitionVersion,
            bind.CapabilityAdmissionPlan,
            bind.ExpectedExecutionMode,
            bind.InitialLineage,
            bind.ReusePolicy,
            bind.BindingGeneration,
            bind.ReuseAuthorityActorId);
    }

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

        public global::System.Type? FailNextPublishType { get; set; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (FailNextPublishType?.IsInstanceOfType(evt) == true)
            {
                FailNextPublishType = null;
                throw new InvalidOperationException("injected publication failure");
            }

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

    private sealed class RecordingCallbackScheduler(
        bool failSchedule = false,
        Func<RuntimeCallbackTimeoutRequest, CancellationToken, Task>? afterSchedule = null)
        : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public List<RuntimeCallbackLease> CancelledLeases { get; } = [];

        public int ScheduleAttempts { get; private set; }

        public async Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScheduleAttempts++;
            if (failSchedule)
                throw new InvalidOperationException("schedule failed");

            TimeoutRequests.Add(request);
            var lease = new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                TimeoutRequests.Count,
                RuntimeCallbackBackend.InMemory);
            if (afterSchedule != null)
                await afterSchedule(request, ct);
            return lease;
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CancelledLeases.Add(lease);
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class TestServiceProvider(
        IActorRuntimeCallbackScheduler scheduler,
        IRuntimeSecretStore? runtimeSecretStore,
        ICommittedStatePublicationHook? publicationHook) : IServiceProvider
    {
        public object? GetService(global::System.Type serviceType)
        {
            if (serviceType == typeof(IActorRuntimeCallbackScheduler))
                return scheduler;
            if (serviceType == typeof(IRuntimeSecretStore))
                return runtimeSecretStore;
            if (serviceType == typeof(IEnumerable<IGAgentExecutionHook>))
                return Array.Empty<IGAgentExecutionHook>();
            if (serviceType == typeof(IEnumerable<ICommittedStatePublicationHook>))
                return publicationHook == null
                    ? Array.Empty<ICommittedStatePublicationHook>()
                    : new[] { publicationHook };
            return null;
        }
    }

    private sealed class FailOnceCommittedPublicationHook : ICommittedStatePublicationHook
    {
        public bool FailNext { get; set; }

        public int FailureCount { get; private set; }

        public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!FailNext)
                return Task.CompletedTask;

            FailNext = false;
            FailureCount++;
            throw new InvalidOperationException("injected committed publication failure");
        }
    }

    private sealed class RecordingRuntimeSecretStore : IRuntimeSecretStore
    {
        public HashSet<string> FailReferences { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ThrowReferences { get; } = new(StringComparer.Ordinal);

        public HashSet<string> UnavailableReferences { get; } = new(StringComparer.Ordinal);

        public List<RevokeRuntimeSecretRequest> RevokeRequests { get; } = [];

        public List<ResolveRuntimeSecretRequest> ResolveRequests { get; } = [];

        public Task<StoreRuntimeSecretResult> PutAsync(
            StoreRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ResolveRuntimeSecretResult> ResolveAsync(
            ResolveRuntimeSecretRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ResolveRequests.Add(request);
            if (UnavailableReferences.Contains(request.Ref))
                return Task.FromResult(new ResolveRuntimeSecretResult(null, null));

            return Task.FromResult(new ResolveRuntimeSecretResult(
                new RuntimeSecretReference
                {
                    Ref = request.Ref,
                    Purpose = request.Purpose,
                    OwnerRunId = request.OwnerRunId,
                    OwnerStepId = request.OwnerStepId,
                },
                "present"));
        }

        public Task<ConsumeRuntimeSecretResult> ConsumeAsync(
            ConsumeRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RevokeRuntimeSecretResult> RevokeAsync(
            RevokeRuntimeSecretRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RevokeRequests.Add(request);
            if (ThrowReferences.Contains(request.Ref))
                throw new InvalidOperationException("injected transient revoke failure");
            return Task.FromResult(new RevokeRuntimeSecretResult(
                !FailReferences.Contains(request.Ref) &&
                !UnavailableReferences.Contains(request.Ref)));
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
#pragma warning restore CS0612
