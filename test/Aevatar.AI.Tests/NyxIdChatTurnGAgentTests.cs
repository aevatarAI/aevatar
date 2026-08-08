using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatTurnGAgentTests
{
    [Fact]
    public void OperationExecutorPort_ShouldCarryOnlyTypedCommandAndTransientSession()
    {
        var assembly = typeof(NyxIdChatTurnGAgent).Assembly;
        var sessionType = assembly.GetType(
            "Aevatar.GAgents.NyxidChat.NyxIdChatTransientExecutionSession");
        var executionType = assembly.GetType(
            "Aevatar.GAgents.NyxidChat.NyxIdChatTurnOperationExecution");
        var execute = typeof(INyxIdChatTurnOperationExecutor).GetMethod("ExecuteAsync");

        sessionType.Should().NotBeNull();
        executionType.Should().NotBeNull();
        execute.Should().NotBeNull();
        execute!.GetParameters().Select(static parameter => parameter.ParameterType).Should().Equal(
            typeof(NyxIdChatOperationDispatchCommand),
            sessionType!,
            typeof(Func<NyxIdChatOperationProgressSignal, CancellationToken, Task>),
            typeof(CancellationToken));
        execute.ReturnType.Should().Be(typeof(Task<>).MakeGenericType(executionType!));
    }

    [Fact]
    public async Task LlmCommand_ShouldExecuteOneOperationAndCommitBeforeDeliveringResult()
    {
        var key = CreateKey();
        var executor = new RecordingOperationExecutor(command =>
            new NyxIdChatOperationResultSignal
            {
                Key = command.Key.Clone(),
                Llm = new NyxIdChatLLMOperationResult
                {
                    Content = "planning complete",
                    ToolCalls =
                    {
                        new NyxIdChatToolCall
                        {
                            CallId = "call-alpha",
                            ToolName = "tool-alpha",
                            ArgumentsJson = "{}",
                        },
                    },
                },
        });
        var eventStore = new InMemoryEventStoreForTests();
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        NyxIdChatTurnGAgent? agent = null;
        NyxIdChatTurnGAgentState? stateObservedAtDispatch = null;
        IReadOnlyList<StateEvent>? eventsObservedAtDispatch = null;
        var dispatch = new RecordingDispatchPort(async (actorId, envelope) =>
        {
            actorId.Should().Be("conversation-alpha");
            stateObservedAtDispatch = agent!.State.Clone();
            eventsObservedAtDispatch = await eventStore.GetEventsAsync("turn-actor-alpha");
            envelope.Payload.Is(NyxIdChatOperationResultSignal.Descriptor).Should().BeTrue();
        });
        using var services = BuildEventSourcingServices(eventStore);
        agent = CreateAgent(services, operationDispatch, dispatch);
        await agent.ActivateAsync();
        var command = new NyxIdChatOperationDispatchCommand
        {
            Key = key,
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = new Aevatar.AI.Abstractions.ChatRequestEvent
                {
                    Prompt = "plan this",
                    SessionId = "turn-alpha",
                },
            },
        };

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", command));

        executor.Commands.Should().ContainSingle();
        executor.Commands.Single().InputCase.Should()
            .Be(NyxIdChatOperationDispatchCommand.InputOneofCase.Llm);
        dispatch.Calls.Should().BeEmpty();
        agent.State.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        (await eventStore.GetEventsAsync("turn-actor-alpha"))
            .Select(static item => item.EventData.TypeUrl)
            .Should().Equal(Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl);

        await operationDispatch.DeliverPendingSignalsAsync(agent);

        dispatch.Calls.Should().ContainSingle();
        stateObservedAtDispatch.Should().NotBeNull();
        stateObservedAtDispatch!.AdmittedOperation.Should().BeEquivalentTo(key);
        stateObservedAtDispatch.Phase.Should().Be(NyxIdChatOperationPhase.Succeeded);
        stateObservedAtDispatch.ResultDelivered.Should().BeFalse();
        eventsObservedAtDispatch!.Select(static item => item.EventData.TypeUrl).Should().Equal(
            Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
            Any.Pack(new NyxIdChatTurnOperationCompletedEvent()).TypeUrl);
        agent.State.ResultDelivered.Should().BeTrue();
        (await eventStore.GetEventsAsync("turn-actor-alpha"))
            .Select(static item => item.EventData.TypeUrl)
            .Should().Equal(
                Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationCompletedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationDeliveredEvent()).TypeUrl);
    }

    [Fact]
    public async Task LlmToolCalls_ShouldWaitForExplicitToolCommandAndIgnoreDuplicateOrStaleCommands()
    {
        var executor = new RecordingOperationExecutor(command =>
            command.InputCase == NyxIdChatOperationDispatchCommand.InputOneofCase.Llm
                ? new NyxIdChatOperationResultSignal
                {
                    Key = command.Key.Clone(),
                    Llm = new NyxIdChatLLMOperationResult
                    {
                        ToolCalls =
                        {
                            new NyxIdChatToolCall
                            {
                                CallId = "call-alpha",
                                ToolName = "tool-alpha",
                                ArgumentsJson = "{}",
                            },
                        },
                    },
                }
                : new NyxIdChatOperationResultSignal
                {
                    Key = command.Key.Clone(),
                    Tool = new NyxIdChatToolOperationResult
                    {
                        ResultJson = "{\"ok\":true}",
                        Receipt = new Aevatar.AI.Abstractions.AgentToolReceipt
                        {
                            CallId = "call-alpha",
                            ToolName = "tool-alpha",
                            Status = Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success,
                        },
                        ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
                    },
                });
        var eventStore = new InMemoryEventStoreForTests();
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        var dispatch = new RecordingDispatchPort();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(services, operationDispatch, dispatch);
        await agent.ActivateAsync();
        var llm = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = new Aevatar.AI.Abstractions.ChatRequestEvent
                {
                    Prompt = "use a tool",
                    SessionId = "turn-alpha",
                },
            },
        };

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", llm));
        executor.Commands.Should().ContainSingle();
        await operationDispatch.DeliverPendingSignalsAsync(agent);

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", llm.Clone()));
        var stale = llm.Clone();
        stale.Key.OperationId = "operation-stale";
        stale.Key.OperationGeneration = 0;
        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", stale));
        executor.Commands.Should().ContainSingle();

        var tool = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(
                stepId: "step-tool-alpha",
                operationId: "operation-tool-alpha",
                generation: 1),
            Tool = new NyxIdChatToolOperationInput
            {
                ToolName = "tool-alpha",
                CallId = "call-alpha",
                ArgumentsJson = "{}",
            },
        };
        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", tool));
        await operationDispatch.DeliverPendingSignalsAsync(agent);

        executor.Commands.Should().HaveCount(2);
        executor.Commands.Select(static command => command.InputCase).Should().Equal(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Llm,
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
        dispatch.Calls.Should().HaveCount(2);

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", llm.Clone()));
        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", tool.Clone()));

        executor.Commands.Should().HaveCount(2);
        dispatch.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task EffectToolCommand_ShouldCommitDispatchWaterlineBeforeToolExecution()
    {
        var generationExecutor = new StreamingCapabilityReplyExecutor();
        var operationExecutor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var eventStore = new InMemoryEventStoreForTests();
        var operationDispatch = new RecordingOperationDispatchPort(operationExecutor);
        var dispatch = new RecordingDispatchPort();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(services, operationDispatch, dispatch);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(
            "turn-actor-alpha",
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "authorize the effect tool",
                        SessionId = "turn-alpha",
                    },
                },
            }));
        await operationDispatch.DeliverPendingSignalsAsync(agent);
        var tool = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
            Tool = new NyxIdChatToolOperationInput
            {
                CallId = "call-alpha",
                ToolName = "tool-alpha",
                ArgumentsJson = "{\"value\":1}",
                MayChangeExternalState = true,
                IdempotencyKey = "operation-tool-alpha",
            },
        };

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", tool));

        generationExecutor.ToolExecutions.Should().Be(1);
        agent.State.EffectDispatchWaterline.Should().Be(
            NyxIdChatEffectEvidence.MayHaveChanged);
        agent.State.EffectDispatchStartedAt.Should().NotBeNull();
        (await eventStore.GetEventsAsync("turn-actor-alpha"))
            .Select(static item => item.EventData.TypeUrl)
            .Should().Equal(
                Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationCompletedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationDeliveredEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnEffectDispatchStartedEvent()).TypeUrl);

        await operationDispatch.DeliverPendingSignalsAsync(agent);

        (await eventStore.GetEventsAsync("turn-actor-alpha"))
            .Select(static item => item.EventData.TypeUrl)
            .Should().Equal(
                Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationCompletedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationDeliveredEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnEffectDispatchStartedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationCompletedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationDeliveredEvent()).TypeUrl);
    }

    [Fact]
    public async Task PlanGateEffectCommand_ShouldPersistExactAdmissionBeforeDispatch()
    {
        var admission = ExactWriteAdmission();
        var executor = new RecordingOperationExecutor(command =>
            command.InputCase == NyxIdChatOperationDispatchCommand.InputOneofCase.Llm
                ? new NyxIdChatOperationResultSignal
                {
                    Key = command.Key.Clone(),
                    Llm = new NyxIdChatLLMOperationResult
                    {
                        Content = "The exact plan is ready for confirmation.",
                    },
                }
                : new NyxIdChatOperationResultSignal
                {
                    Key = command.Key.Clone(),
                    Tool = new NyxIdChatToolOperationResult
                    {
                        Receipt = new AgentToolReceipt
                        {
                            Status = AgentToolReceiptStatus.Success,
                            CallId = command.PlanGateContinuation.ToolCallId,
                            ToolName = command.PlanGateContinuation.ToolName,
                        },
                        ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
                    },
                });
        var eventStore = new InMemoryEventStoreForTests();
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(services, operationDispatch, new RecordingDispatchPort());
        await agent.ActivateAsync();
        var initial = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = new ChatRequestEvent
                {
                    Prompt = "prepare the exact plan",
                    SessionId = "turn-alpha",
                },
            },
        };
        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", initial));
        await operationDispatch.DeliverPendingSignalsAsync(agent);
        var command = PlanGateContinuation(
            NyxIdChatPlanGateDecisions.HashArguments("{\"value\":1}"));
        command.PlanGateContinuation.OperationAdmission = admission.Clone();
        var admittedAt = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 7, 24, 8, 0, 1, TimeSpan.Zero));
        var gateAdmission = new NyxIdChatTurnPlanGateAdmissionCommand
        {
            SourceOperationKey = initial.Key.Clone(),
            Admission = new NyxIdChatTurnPlanGateAdmissionState
            {
                Key = command.Key.Clone(),
                GateRequestId = command.PlanGateContinuation.GateRequestId,
                TaskId = command.PlanGateContinuation.TaskId,
                PlanId = command.PlanGateContinuation.PlanId,
                PlanRevision = command.PlanGateContinuation.PlanRevision,
                ToolCallId = command.PlanGateContinuation.ToolCallId,
                ToolName = command.PlanGateContinuation.ToolName,
                ArgumentsSha256 = command.PlanGateContinuation.ArgumentsSha256,
                MayChangeExternalState = command.PlanGateContinuation.MayChangeExternalState,
                OperationAdmission = admission.Clone(),
                AdmittedAt = admittedAt,
            },
        };
        var eventsBeforeAdmission = await eventStore.GetEventsAsync("turn-actor-alpha");

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", gateAdmission));
        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", command));

        executor.Commands.Where(candidate =>
                candidate.InputCase ==
                NyxIdChatOperationDispatchCommand.InputOneofCase.PlanGateContinuation)
            .Should().ContainSingle();
        agent.State.OperationAdmission.Should().BeEquivalentTo(admission);
        agent.State.IdempotencyKey.Should().Be(command.Key.OperationId);
        agent.State.PlanGateAdmission.Should().BeNull("the exact admission is one-use");
        agent.State.EffectDispatchWaterline.Should().Be(
            NyxIdChatEffectEvidence.MayHaveChanged);
        (await eventStore.GetEventsAsync("turn-actor-alpha"))
            .Skip(eventsBeforeAdmission.Count)
            .Select(static item => item.EventData.TypeUrl)
            .Should().Equal(
                Any.Pack(new NyxIdChatTurnPlanGateAdmissionCommittedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnEffectDispatchStartedEvent()).TypeUrl);
    }

    [Fact]
    public async Task PlanGateContinuation_WithoutDurableAdmission_ShouldFailClosedBeforeExecution()
    {
        var executor = new RecordingOperationExecutor(command =>
            throw new InvalidOperationException($"Unexpected execution: {command.InputCase}"));
        var eventStore = new InMemoryEventStoreForTests();
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(services, operationDispatch, new RecordingDispatchPort());
        await agent.ActivateAsync();

        await agent.HandleEventAsync(CreateEnvelope(
            "turn-actor-alpha",
            PlanGateContinuation(NyxIdChatPlanGateDecisions.HashArguments("{\"value\":1}"))));

        executor.Commands.Should().BeEmpty();
        (await eventStore.GetEventsAsync("turn-actor-alpha")).Should().BeEmpty();
    }

    [Fact]
    public async Task PlanGateAdmission_AfterReactivation_ShouldExpireAndSignalConversation()
    {
        var executor = new RecordingOperationExecutor(command => new NyxIdChatOperationResultSignal
        {
            Key = command.Key.Clone(),
            Llm = new NyxIdChatLLMOperationResult { Content = "Plan ready." },
        });
        var eventStore = new InMemoryEventStoreForTests();
        var originalOperationDispatch = new RecordingOperationDispatchPort(executor);
        using var services = BuildEventSourcingServices(eventStore);
        var original = CreateAgent(
            services,
            originalOperationDispatch,
            new RecordingDispatchPort());
        await original.ActivateAsync();
        var initial = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = new ChatRequestEvent
                {
                    Prompt = "prepare the exact plan",
                    SessionId = "turn-alpha",
                },
            },
        };
        await original.HandleEventAsync(CreateEnvelope("turn-actor-alpha", initial));
        await originalOperationDispatch.DeliverPendingSignalsAsync(original);
        var continuation = PlanGateContinuation(
            NyxIdChatPlanGateDecisions.HashArguments("{\"value\":1}"));
        var admission = CreatePlanGateAdmission(initial.Key, continuation);
        await original.HandleEventAsync(CreateEnvelope("turn-actor-alpha", admission));
        original.State.PlanGateAdmission.Should().NotBeNull();

        var reactivationDispatch = new RecordingDispatchPort();
        var reactivated = CreateAgent(
            services,
            new RecordingOperationDispatchPort(executor),
            reactivationDispatch);
        await reactivated.ActivateAsync();

        reactivated.State.PlanGateAdmission.Should().BeNull();
        var signal = reactivationDispatch.Calls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatPlanGateCapabilityExpiredSignal>();
        signal.FailureCode.Should().Be(NyxIdChatTurnGAgent.PlanGateCapabilityExpiredCode);
        signal.Admission.Should().BeEquivalentTo(admission.Admission);
        (await eventStore.GetEventsAsync("turn-actor-alpha"))[^1].EventData
            .Is(NyxIdChatTurnPlanGateAdmissionExpiredEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task ToolCommand_AfterActorRestart_ShouldFailNotStartedWithoutReauthorizingOrExecuting()
    {
        var generationExecutor = new CapabilityGeneratingReplyExecutor();
        var operationExecutor = Activator.CreateInstance(
            typeof(NyxIdChatTurnOperationExecutor),
            generationExecutor).Should().BeAssignableTo<INyxIdChatTurnOperationExecutor>().Subject;
        var eventStore = new InMemoryEventStoreForTests();
        var dispatch = new RecordingDispatchPort();
        var originalOperationDispatch = new RecordingOperationDispatchPort(operationExecutor);
        using var services = BuildEventSourcingServices(eventStore);
        var original = CreateAgent(services, originalOperationDispatch, dispatch);
        await original.ActivateAsync();
        var llm = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = new Aevatar.AI.Abstractions.ChatRequestEvent
                {
                    Prompt = "use the authorized tool",
                    SessionId = "turn-alpha",
                },
            },
        };

        await original.HandleEventAsync(CreateEnvelope("turn-actor-alpha", llm));
        await originalOperationDispatch.DeliverPendingSignalsAsync(original);

        generationExecutor.LlmExecutions.Should().Be(1);
        generationExecutor.ToolContinuations.Should().Be(0);
        generationExecutor.ToolExecutions.Should().Be(0);

        // Reconstructing the actor replays only its durable operation waterline. The
        // exact authorized-tool capability is intentionally transient and is gone.
        var restartedOperationDispatch = new RecordingOperationDispatchPort(operationExecutor);
        var restarted = CreateAgent(services, restartedOperationDispatch, dispatch);
        await restarted.ActivateAsync();
        var tool = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(
                stepId: "step-tool-alpha",
                operationId: "operation-tool-alpha",
                generation: 1),
            Tool = new NyxIdChatToolOperationInput
            {
                ToolName = CapabilityGeneratingReplyExecutor.ToolCall.Name,
                CallId = CapabilityGeneratingReplyExecutor.ToolCall.Id,
                ArgumentsJson = CapabilityGeneratingReplyExecutor.ToolCall.ArgumentsJson,
                MayChangeExternalState = true,
                IdempotencyKey = "operation-tool-alpha",
            },
        };

        await restarted.HandleEventAsync(CreateEnvelope("turn-actor-alpha", tool));
        await restartedOperationDispatch.DeliverPendingSignalsAsync(restarted);

        generationExecutor.ToolContinuations.Should().Be(0);
        generationExecutor.ToolExecutions.Should().Be(0);
        var result = dispatch.Calls.Last().Envelope.Payload
            .Unpack<NyxIdChatOperationResultSignal>();
        result.Key.Should().BeEquivalentTo(tool.Key);
        result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        result.Failure.FailureCode.Should().Be("NYXID_CHAT_TOOL_CAPABILITY_LOST");
        result.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
        restarted.State.Phase.Should().Be(NyxIdChatOperationPhase.Failed);
        restarted.State.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
    }

    [Fact]
    public async Task EffectExecution_ShouldReturnActorTurnBeforeExternalCompletion()
    {
        var executor = new BlockingOperationExecutor();
        var completionDispatched = new TaskCompletionSource<EventEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var actorDispatch = new RecordingDispatchPort((actorId, envelope) =>
        {
            if (actorId == "turn-actor-alpha" &&
                envelope.Payload.Is(
                    NyxIdChatTurnOperationExecutionCompletedSignal.Descriptor))
            {
                completionDispatched.TrySetResult(envelope.Clone());
            }

            return Task.CompletedTask;
        });
        var operationDispatch = new NyxIdChatTurnOperationDispatchPort(
            executor,
            new UnavailableNyxIdChatTurnOperationReconciliationPort(),
            actorDispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
            NullLogger<NyxIdChatTurnOperationDispatchPort>.Instance);
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(services, operationDispatch, actorDispatch);
        await agent.ActivateAsync();
        var command = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
            Tool = new NyxIdChatToolOperationInput
            {
                CallId = "call-alpha",
                ToolName = "tool-alpha",
                ArgumentsJson = "{}",
                MayChangeExternalState = true,
                IdempotencyKey = "operation-tool-alpha",
                OperationAdmission = ExactWriteAdmission(),
            },
        };

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", command));
        await executor.Started.Task;

        agent.State.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        agent.State.EffectDispatchWaterline.Should().Be(
            NyxIdChatEffectEvidence.MayHaveChanged);
        agent.State.ResultDelivered.Should().BeFalse();
        completionDispatched.Task.IsCompleted.Should().BeFalse();
        (await eventStore.GetEventsAsync("turn-actor-alpha"))
            .Select(static item => item.EventData.TypeUrl)
            .Should().Equal(
                Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnEffectDispatchStartedEvent()).TypeUrl);

        executor.Complete(command.Key);
        var completionEnvelope = await completionDispatched.Task;
        await agent.HandleEventAsync(completionEnvelope);

        agent.State.Phase.Should().Be(NyxIdChatOperationPhase.Succeeded);
        agent.State.ResultDelivered.Should().BeTrue();
    }

    [Fact]
    public async Task EffectExecution_LostCompletion_ShouldUseDurableWatchdogsAndLockRetry()
    {
        var executor = new BlockingOperationExecutor();
        var executionCompletionAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reconciliationCompletionAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var conversationDeliveryAttempts = 0;
        var actorDispatch = new RecordingDispatchPort((actorId, envelope) =>
        {
            if (actorId == "conversation-alpha" &&
                envelope.Payload.Is(NyxIdChatOperationResultSignal.Descriptor) &&
                Interlocked.Increment(ref conversationDeliveryAttempts) == 1)
            {
                throw new InvalidOperationException("result delivery unavailable");
            }

            if (actorId != "turn-actor-alpha" ||
                !envelope.Payload.Is(NyxIdChatTurnOperationExecutionCompletedSignal.Descriptor))
            {
                return Task.CompletedTask;
            }

            var completion = envelope.Payload.Unpack<NyxIdChatTurnOperationExecutionCompletedSignal>();
            if (completion.Source == NyxIdChatTurnOperationCompletionSource.Execution)
                executionCompletionAttempted.TrySetResult();
            else
                reconciliationCompletionAttempted.TrySetResult();
            throw new InvalidOperationException("completion dispatch unavailable");
        });
        var operationDispatch = new NyxIdChatTurnOperationDispatchPort(
            executor,
            new UnavailableNyxIdChatTurnOperationReconciliationPort(),
            actorDispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
            NullLogger<NyxIdChatTurnOperationDispatchPort>.Instance);
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore, callbacks);
        var options = new NyxIdToolOptions { MaxRequestDurationSeconds = 420 };
        var agent = CreateAgent(services, operationDispatch, actorDispatch, options);
        await agent.ActivateAsync();
        var command = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
            Tool = new NyxIdChatToolOperationInput
            {
                CallId = "call-alpha",
                ToolName = "tool-alpha",
                ArgumentsJson = "{}",
                MayChangeExternalState = true,
                IdempotencyKey = "operation-tool-alpha",
                OperationAdmission = ExactWriteAdmission(),
            },
        };

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", command));
        await executor.Started.Task;

        var firstWatchdog = callbacks.TimeoutRequests.Should().ContainSingle().Which;
        firstWatchdog.ActorId.Should().Be("turn-actor-alpha");
        firstWatchdog.DueTime.Should().Be(
            options.EffectiveMaxRequestDuration +
            NyxIdChatTurnGAgent.OperationCompletionWatchdogMargin);
        var firstSignal = firstWatchdog.TriggerEnvelope.Payload
            .Unpack<NyxIdChatRecoveryRequestedSignal>();
        firstSignal.Kind.Should().Be(NyxIdChatRecoveryKind.OperationCompletionWatchdog);
        firstSignal.Key.Should().BeEquivalentTo(command.Key);
        firstSignal.ExpectedStateVersion.Should().Be(2);

        executor.Complete(command.Key);
        await executionCompletionAttempted.Task;
        agent.State.ResultDelivered.Should().BeFalse();

        await agent.HandleEventAsync(firstWatchdog.TriggerEnvelope);
        await reconciliationCompletionAttempted.Task;

        callbacks.TimeoutRequests.Should().HaveCount(2);
        var secondWatchdog = callbacks.TimeoutRequests[1];
        var secondSignal = secondWatchdog.TriggerEnvelope.Payload
            .Unpack<NyxIdChatRecoveryRequestedSignal>();
        secondSignal.Kind.Should().Be(NyxIdChatRecoveryKind.OperationCompletionWatchdog);
        secondSignal.Key.Should().BeEquivalentTo(command.Key);
        secondSignal.ExpectedStateVersion.Should().Be(3);
        agent.State.ReconciliationStartedAt.Should().NotBeNull();
        agent.State.ResultDelivered.Should().BeFalse();

        var failedDelivery = () => agent.HandleEventAsync(secondWatchdog.TriggerEnvelope);
        await failedDelivery.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("result delivery unavailable");

        agent.State.Phase.Should().Be(NyxIdChatOperationPhase.Uncertain);
        agent.State.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);
        agent.State.ResultDelivered.Should().BeFalse();
        callbacks.TimeoutRequests.Should().HaveCount(3);
        var deliveryWatchdog = callbacks.TimeoutRequests[2];
        deliveryWatchdog.DueTime.Should().Be(
            NyxIdChatTurnGAgent.OperationResultDeliveryWatchdogDelay);
        var deliverySignal = deliveryWatchdog.TriggerEnvelope.Payload
            .Unpack<NyxIdChatRecoveryRequestedSignal>();
        deliverySignal.Kind.Should().Be(
            NyxIdChatRecoveryKind.OperationResultDeliveryWatchdog);
        deliverySignal.Key.Should().BeEquivalentTo(command.Key);
        deliverySignal.ExpectedStateVersion.Should().Be(4);

        await agent.HandleEventAsync(deliveryWatchdog.TriggerEnvelope);

        conversationDeliveryAttempts.Should().Be(2);
        agent.State.Phase.Should().Be(NyxIdChatOperationPhase.Uncertain);
        agent.State.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);
        agent.State.ResultDelivered.Should().BeTrue();
        var retry = command.Clone();
        retry.Key = CreateKey("step-tool-beta", "operation-tool-beta", 2);
        retry.Tool.IdempotencyKey = retry.Key.OperationId;
        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", retry));
        executor.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task AdmittedRead_LostCompletion_ShouldReachHonestTerminalThroughWatchdog()
    {
        var executor = new BlockingOperationExecutor();
        var completionAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var actorDispatch = new RecordingDispatchPort((actorId, envelope) =>
        {
            if (actorId == "turn-actor-alpha" &&
                envelope.Payload.Is(NyxIdChatTurnOperationExecutionCompletedSignal.Descriptor))
            {
                completionAttempted.TrySetResult();
                throw new InvalidOperationException("read completion dispatch unavailable");
            }

            return Task.CompletedTask;
        });
        var operationDispatch = new NyxIdChatTurnOperationDispatchPort(
            executor,
            new UnavailableNyxIdChatTurnOperationReconciliationPort(),
            actorDispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
            NullLogger<NyxIdChatTurnOperationDispatchPort>.Instance);
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore, callbacks);
        var agent = CreateAgent(services, operationDispatch, actorDispatch);
        await agent.ActivateAsync();
        var command = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey("step-read-alpha", "operation-read-alpha", 1),
            Tool = new NyxIdChatToolOperationInput
            {
                CallId = "call-read-alpha",
                ToolName = "tool-read-alpha",
                ArgumentsJson = "{}",
                OperationAdmission = ExactReadAdmission(),
            },
        };

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", command));
        await executor.Started.Task;

        var watchdog = callbacks.TimeoutRequests.Should().ContainSingle().Which;
        var signal = watchdog.TriggerEnvelope.Payload
            .Unpack<NyxIdChatRecoveryRequestedSignal>();
        signal.Kind.Should().Be(NyxIdChatRecoveryKind.OperationCompletionWatchdog);
        signal.Key.Should().BeEquivalentTo(command.Key);
        signal.ExpectedStateVersion.Should().Be(1);

        executor.Complete(command.Key);
        await completionAttempted.Task;
        agent.State.ResultDelivered.Should().BeFalse();

        await agent.HandleEventAsync(watchdog.TriggerEnvelope);

        agent.State.Phase.Should().Be(NyxIdChatOperationPhase.Failed);
        agent.State.TerminalCode.Should().Be("NYXID_CHAT_OPERATION_INTERRUPTED");
        agent.State.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        agent.State.ResultDelivered.Should().BeTrue();
        actorDispatch.Calls.Should().Contain(call =>
            call.ActorId == "conversation-alpha" &&
            call.Envelope.Payload.Is(NyxIdChatOperationResultSignal.Descriptor));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OperationExecutor_ExactAdmissionDrift_ShouldFailBeforeToolExecution(
        bool driftServiceInstance)
    {
        var admitted = ExactWriteAdmission();
        var generationExecutor = new StreamingCapabilityReplyExecutor(admitted);
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var session = new NyxIdChatTransientExecutionSession();
        var llm = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "use the exact admitted operation",
                        SessionId = "turn-alpha",
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var call = llm.Result.Llm.ToolCalls.Should().ContainSingle().Which;
        call.OperationAdmission.Should().BeEquivalentTo(admitted);
        var drifted = admitted.Clone();
        if (driftServiceInstance)
            drifted.ServiceInstanceId = "connected-service-beta";
        else
            drifted.CatalogDigest = $"sha256:{new string('c', 64)}";

        var tool = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = call.CallId,
                    ToolName = call.ToolName,
                    ArgumentsJson = call.ArgumentsJson,
                    MayChangeExternalState = true,
                    IdempotencyKey = "operation-tool-alpha",
                    OperationAdmission = drifted,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        tool.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        tool.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolAuthorizationMismatchCode);
        tool.Result.Failure.ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.NotStarted);
        generationExecutor.ToolExecutions.Should().Be(0);
    }

    [Fact]
    public async Task OperationExecutor_Llm_ShouldMapStreamingChunksToOrderedTypedProgress()
    {
        var generationExecutor = new StreamingCapabilityReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var session = new NyxIdChatTransientExecutionSession();
        var progress = new List<NyxIdChatOperationProgressSignal>();
        var command = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = new ChatRequestEvent
                {
                    Prompt = "stream and call the tool",
                    SessionId = "turn-alpha",
                },
            },
        };

        var execution = await executor.ExecuteAsync(
            command,
            session,
            (signal, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                progress.Add(signal.Clone());
                return Task.CompletedTask;
            },
            CancellationToken.None);

        progress.Select(static signal => signal.Sequence).Should().Equal(1, 2, 3);
        progress.Select(static signal => signal.ProgressCase).Should().Equal(
            NyxIdChatOperationProgressSignal.ProgressOneofCase.Text,
            NyxIdChatOperationProgressSignal.ProgressOneofCase.Reasoning,
            NyxIdChatOperationProgressSignal.ProgressOneofCase.ToolStarted);
        progress.Should().OnlyContain(signal => signal.Key.Equals(command.Key));
        progress[0].Text.Delta.Should().Be("visible text");
        progress[1].Reasoning.Delta.Should().Be("private reasoning");
        progress[2].ToolStarted.CallId.Should().Be("call-alpha");
        progress[2].ToolStarted.ToolName.Should().Be("tool-alpha");
        progress[2].ToolStarted.Presentation.DisplayName.Should().Be("Tool Alpha");
        progress[2].ToolStarted.Presentation.Kind.Should().Be(ToolPresentationKind.Generic);

        execution.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Llm);
        execution.Result.Llm.Content.Should().Be("visible text");
        execution.Result.Llm.ReasoningContent.Should().Be("private reasoning");
        generationExecutor.LlmStepRequests.Should().ContainSingle();
        generationExecutor.LlmStepRequests.Single().AllowMultipleToolCalls.Should().BeFalse();
        var toolCall = execution.Result.Llm.ToolCalls.Should().ContainSingle().Which;
        toolCall.CallId.Should().Be("call-alpha");
        toolCall.ToolName.Should().Be("tool-alpha");
        toolCall.ArgumentsJson.Should().Be("{\"value\":1}");
        toolCall.Safety.Should().NotBeNull();
        toolCall.Safety.IsReadOnly.Should().BeFalse();
        toolCall.Safety.IsDestructive.Should().BeFalse();
        toolCall.Safety.SideEffectKind.Should().Be("tool-alpha.update");
        toolCall.Safety.MayChangeExternalState.Should().BeTrue();
        toolCall.NyxIdProvenance.ConnectedServiceId.Should().Be("connected-service-alpha");
        toolCall.NyxIdProvenance.ServiceSlug.Should().Be("service-slug-alpha");
        toolCall.NyxIdProvenance.CatalogServiceSlug.Should().Be("catalog-slug-alpha");
        toolCall.NyxIdProvenance.ReadinessCapabilityId.Should()
            .Be("readiness-capability-alpha");
    }

    [Fact]
    public async Task OperationExecutor_ProfiledRetryWithFreshSession_ShouldNotRematerializeCapability()
    {
        var registry = new CountingProfileToolSetRegistry();
        var generationExecutor = new CapabilityGeneratingReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentProfileTurnCatalogMaterializer(
                registry,
                new NoMatchProfileClassifier()));
        var profile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-alpha",
            ProfileVersion = "profile-v1",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "tool-alpha" },
            },
            RecoveryToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "tool-alpha" },
            },
            ActivationMode = AgentProfileActivationMode.Enforced,
        });
        var authority = new AgentProfileTurnAuthorityState
        {
            ReconciliationKey = new AgentProfileTurnReconciliationKey
            {
                SessionId = "turn-alpha",
                Attempt = 1,
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.Recovery,
            AuthorityCeilingToolNames = { "tool-alpha" },
        };
        var command = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(generation: 2),
            Llm = new NyxIdChatLLMOperationInput
            {
                AgentProfile = profile,
                AgentProfileTurnAuthority = authority,
                Request = new ChatRequestEvent
                {
                    Prompt = "retry safely",
                    SessionId = "turn-alpha",
                },
            },
        };

        await executor.ExecuteAsync(
            command,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        registry.ResolveCount.Should().Be(0);
        generationExecutor.LastTurnCatalog.Should().NotBeNull();
        generationExecutor.LastTurnCatalog!.FinalAllowedToolNames.Should().BeEmpty();
        generationExecutor.LastTurnCatalog.RouteOwnedTools.Should().BeEmpty();
    }

    [Fact]
    public async Task OperationExecutor_ExactToolCapability_ShouldExecuteOnceAndMapTypedReceipt()
    {
        var generationExecutor = new StreamingCapabilityReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var session = new NyxIdChatTransientExecutionSession();
        var progress = new List<NyxIdChatOperationProgressSignal>();
        Task ReportProgressAsync(
            NyxIdChatOperationProgressSignal signal,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            progress.Add(signal.Clone());
            return Task.CompletedTask;
        }
        var llmCommand = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = new ChatRequestEvent
                {
                    Prompt = "authorize the tool",
                    SessionId = "turn-alpha",
                },
            },
        };
        await executor.ExecuteAsync(
            llmCommand,
            session,
            ReportProgressAsync,
            CancellationToken.None);
        var toolCommand = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
            Tool = new NyxIdChatToolOperationInput
            {
                CallId = "call-alpha",
                ToolName = "tool-alpha",
                ArgumentsJson = "{\"value\":1}",
                MayChangeExternalState = true,
            },
        };

        var first = await executor.ExecuteAsync(
            toolCommand,
            session,
            ReportProgressAsync,
            CancellationToken.None);
        var duplicate = await executor.ExecuteAsync(
            toolCommand.Clone(),
            session,
            ReportProgressAsync,
            CancellationToken.None);

        generationExecutor.ToolExecutions.Should().Be(1);
        var toolStarts = progress.Where(signal =>
            signal.ProgressCase ==
            NyxIdChatOperationProgressSignal.ProgressOneofCase.ToolStarted).ToArray();
        toolStarts.Should().ContainSingle();
        toolStarts[0].ToolStarted.CallId.Should().Be("call-alpha");
        toolStarts[0].ToolStarted.Presentation.DisplayName.Should().Be("Tool Alpha");
        progress.Where(static signal =>
                signal.ProgressCase == NyxIdChatOperationProgressSignal.ProgressOneofCase.Phase)
            .Select(static signal => (
                signal.Phase.SubstepId,
                signal.Phase.Title,
                signal.Phase.Status))
            .Should().Equal(
                ("prepare-operation", "Prepare operation", NyxIdChatSubstepStatus.Running),
                ("prepare-operation", "Prepare operation", NyxIdChatSubstepStatus.Done),
                ("execute-operation", "Execute operation", NyxIdChatSubstepStatus.Running),
                ("execute-operation", "Execute operation", NyxIdChatSubstepStatus.Done));
        first.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Tool);
        first.Result.Tool.ResultJson.Should().Be("{\"ok\":true}");
        first.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        first.Result.Tool.Receipt.CallId.Should().Be("call-alpha");
        first.Result.Tool.Receipt.ToolName.Should().Be("tool-alpha");
        first.Result.Tool.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);
        duplicate.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        duplicate.Result.Failure.FailureCode.Should().Be("NYXID_CHAT_TOOL_CAPABILITY_LOST");
        duplicate.Result.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
    }

    [Fact]
    public async Task OperationExecutor_ExactPlanGateContinuation_ShouldUseTransientCallOnce()
    {
        var generationExecutor = new StreamingCapabilityReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var session = new NyxIdChatTransientExecutionSession();
        await ExecutePlanGateInitialLlmAsync(executor, session);
        var continuation = PlanGateContinuation(
            NyxIdChatPlanGateDecisions.HashArguments("{\"value\":1}"));

        var execution = await executor.ExecuteAsync(
            continuation,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        generationExecutor.ToolExecutions.Should().Be(1);
        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Tool);
        execution.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        execution.Result.Tool.Receipt.CallId.Should().Be("call-alpha");
    }

    [Fact]
    public async Task OperationExecutor_PlanGateDigestMismatch_ShouldFailClosedBeforeEffect()
    {
        var generationExecutor = new StreamingCapabilityReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var session = new NyxIdChatTransientExecutionSession();
        await ExecutePlanGateInitialLlmAsync(executor, session);
        var continuation = PlanGateContinuation(ByteString.CopyFrom(new byte[32]));

        var execution = await executor.ExecuteAsync(
            continuation,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        generationExecutor.ToolExecutions.Should().Be(0);
        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolAuthorizationMismatchCode);
        execution.Result.Failure.ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.NotStarted);
    }

    [Fact]
    public async Task OperationExecutor_ContinuationLlm_ShouldReuseToolUpdatedTransientSession()
    {
        var generationExecutor = new StreamingCapabilityReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var session = new NyxIdChatTransientExecutionSession();
        var initial = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = new ChatRequestEvent
                {
                    Prompt = "authorize then continue",
                    SessionId = "turn-alpha",
                },
            },
        };
        await executor.ExecuteAsync(
            initial,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = "call-alpha",
                    ToolName = "tool-alpha",
                    ArgumentsJson = "{\"value\":1}",
                    MayChangeExternalState = true,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var continuation = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-llm-continuation", "operation-llm-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput { ContinueSession = true },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        generationExecutor.InitialStateBuilds.Should().Be(1);
        generationExecutor.LlmStepStates.Should().HaveCount(2);
        var continuedState = generationExecutor.LlmStepStates[1];
        continuedState.Round.Should().Be(1);
        continuedState.PendingToolCalls.Should().BeEmpty();
        continuedState.Messages.Should().ContainSingle(message =>
            message.Role == "tool" &&
            message.ToolCallId == "call-alpha" &&
            message.Content == "{\"ok\":true}");
        continuedState.ToolReceipts.Should().ContainSingle(receipt =>
            receipt.CallId == "call-alpha" &&
            receipt.Status == AgentToolReceiptStatus.Success);
        continuation.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Llm);
        continuation.Result.Llm.Content.Should().Be("final response");
        continuation.Result.Llm.ToolCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task OperationExecutor_Postcondition_ShouldDelegateTypedInputToReadModelPort()
    {
        var port = new RecordingActionPostconditionPort
        {
            Result = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = "action-alpha",
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = true,
                Resource = new NyxIdChatSafeResourceRef
                {
                    UserService = new NyxIdChatUserServiceRef
                    {
                        UserServiceId = "service-alpha",
                    },
                },
            },
        };
        var executor = new NyxIdChatTurnOperationExecutor(
            new CapabilityGeneratingReplyExecutor(),
            port);
        var command = PostconditionCommand(NyxIdChatActionDisposition.Completed);

        var execution = await executor.ExecuteAsync(
            command,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        port.Inputs.Should().ContainSingle().Which.Should().BeEquivalentTo(
            command.ActionPostcondition);
        execution.Result.Key.Should().BeEquivalentTo(command.Key);
        execution.Result.ActionPostcondition.Verified.Should().BeTrue();
        execution.Result.ActionPostcondition.Resource.UserService.UserServiceId.Should().Be(
            "service-alpha");
    }

    [Fact]
    public async Task OperationExecutor_StateChangeWake_ShouldQueryWithoutCompletionClaim()
    {
        var port = new RecordingActionPostconditionPort
        {
            Result = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = "action-alpha",
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = true,
                Resource = new NyxIdChatSafeResourceRef
                {
                    UserService = new NyxIdChatUserServiceRef
                    {
                        UserServiceId = "service-alpha",
                    },
                },
            },
        };
        var executor = new NyxIdChatTurnOperationExecutor(
            new CapabilityGeneratingReplyExecutor(),
            port);
        var command = PostconditionCommand(NyxIdChatActionDisposition.Unspecified);
        command.ActionPostcondition.ResourceHint = null;

        var execution = await executor.ExecuteAsync(
            command,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        port.Inputs.Should().ContainSingle().Which.Should().BeEquivalentTo(
            command.ActionPostcondition);
        execution.Result.ActionPostcondition.Verified.Should().BeTrue();
        execution.Result.ActionPostcondition.Disposition.Should().Be(
            NyxIdChatActionDisposition.Completed);
    }

    [Theory]
    [InlineData(NyxIdChatActionDisposition.Declined)]
    [InlineData(NyxIdChatActionDisposition.Failed)]
    [InlineData(NyxIdChatActionDisposition.Cancelled)]
    [InlineData(NyxIdChatActionDisposition.Expired)]
    public async Task OperationExecutor_NonCompletedActionReport_ShouldFailClosedWithoutRead(
        NyxIdChatActionDisposition disposition)
    {
        var port = new RecordingActionPostconditionPort();
        var executor = new NyxIdChatTurnOperationExecutor(
            new CapabilityGeneratingReplyExecutor(),
            port);

        var execution = await executor.ExecuteAsync(
            PostconditionCommand(disposition),
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        port.Inputs.Should().BeEmpty();
        execution.Result.ActionPostcondition.Verified.Should().BeFalse();
        execution.Result.ActionPostcondition.FailureCode.Should().Be(
            "NYXID_ACTION_POSTCONDITION_INPUT_INVALID");
    }

    private static NyxIdChatOperationDispatchCommand PostconditionCommand(
        NyxIdChatActionDisposition disposition) => new()
    {
        Key = CreateKey(),
        ActionPostcondition = new NyxIdChatActionPostconditionInput
        {
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
            OriginTurnId = "turn-origin-alpha",
            ActionRequestId = "action-alpha",
            Action = NyxIdAssistantActionKind.ServiceConnect,
            ReportedDisposition = disposition,
            ResourceHint = new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef
                {
                    UserServiceId = "service-alpha",
                },
            },
            Params = new NyxIdAssistantActionParams
            {
                CatalogServiceConnect = new NyxIdCatalogServiceConnectParams
                {
                    ServiceSlug = "api-github",
                },
            },
        },
    };

    private static NyxIdChatTurnGAgent CreateAgent(
        ServiceProvider services,
        INyxIdChatTurnOperationDispatchPort operationDispatch,
        IActorDispatchPort dispatch,
        NyxIdToolOptions? options = null)
    {
        var agent = new NyxIdChatTurnGAgent(
            operationDispatch,
            dispatch,
            options ?? new NyxIdToolOptions(),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatTurnGAgentState>>(),
        };
        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, ["turn-actor-alpha"]);
        return agent;
    }

    private static NyxIdChatOperationKey CreateKey(
        string stepId = "step-alpha",
        string operationId = "operation-alpha",
        long generation = 1) =>
        new()
        {
            ConversationActorId = "conversation-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = stepId,
            OperationId = operationId,
            OperationGeneration = generation,
        };

    private static async Task ExecutePlanGateInitialLlmAsync(
        NyxIdChatTurnOperationExecutor executor,
        NyxIdChatTransientExecutionSession session)
    {
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "plan then run",
                        SessionId = "turn-alpha",
                        ToolContext = FreshToolContext("initial-plan-token"),
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
    }

    private static NyxIdChatOperationDispatchCommand PlanGateContinuation(
        ByteString argumentsSha256) => new()
    {
        Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
        PlanGateContinuation = new NyxIdChatPlanGateContinuationInput
        {
            GateRequestId = "plan-gate-alpha",
            TaskId = "task-alpha",
            PlanId = "plan-alpha",
            PlanRevision = 2,
            ToolCallId = "call-alpha",
            ToolName = "tool-alpha",
            ArgumentsSha256 = argumentsSha256,
            ToolContext = FreshToolContext("fresh-plan-token"),
            MayChangeExternalState = true,
            IdempotencyKey = "operation-tool-alpha",
        },
    };

    private static NyxIdChatTurnPlanGateAdmissionCommand CreatePlanGateAdmission(
        NyxIdChatOperationKey sourceOperationKey,
        NyxIdChatOperationDispatchCommand continuation) => new()
    {
        SourceOperationKey = sourceOperationKey.Clone(),
        Admission = new NyxIdChatTurnPlanGateAdmissionState
        {
            Key = continuation.Key.Clone(),
            GateRequestId = continuation.PlanGateContinuation.GateRequestId,
            TaskId = continuation.PlanGateContinuation.TaskId,
            PlanId = continuation.PlanGateContinuation.PlanId,
            PlanRevision = continuation.PlanGateContinuation.PlanRevision,
            ToolCallId = continuation.PlanGateContinuation.ToolCallId,
            ToolName = continuation.PlanGateContinuation.ToolName,
            ArgumentsSha256 = continuation.PlanGateContinuation.ArgumentsSha256,
            MayChangeExternalState = continuation.PlanGateContinuation.MayChangeExternalState,
            OperationAdmission = continuation.PlanGateContinuation.OperationAdmission?.Clone(),
            AdmittedAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 7, 24, 8, 0, 1, TimeSpan.Zero)),
        },
    };

    private static AgentToolExecutionContextPayload FreshToolContext(string token) => new()
    {
        Credentials = new AgentToolCredentialsPayload
        {
            NyxIdAccessToken = token,
            NyxIdCredentialKind =
                AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
        },
    };

    private static ServiceProvider BuildEventSourcingServices(
        IEventStore eventStore,
        IActorRuntimeCallbackScheduler? callbackScheduler = null) =>
        new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(
                callbackScheduler ?? new NoopRuntimeCallbackScheduler())
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

    private static EventEnvelope CreateEnvelope(string actorId, IMessage payload) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
        Payload = Any.Pack(payload),
        Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = actorId } },
        Propagation = new EnvelopePropagation { CorrelationId = "correlation-alpha" },
    };

    private sealed class RecordingOperationExecutor(
        Func<NyxIdChatOperationDispatchCommand, NyxIdChatOperationResultSignal> resultFactory)
        : INyxIdChatTurnOperationExecutor
    {
        public List<NyxIdChatOperationDispatchCommand> Commands { get; } = [];

        public Task<NyxIdChatTurnOperationExecution> ExecuteAsync(
            NyxIdChatOperationDispatchCommand command,
            NyxIdChatTransientExecutionSession session,
            Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
            CancellationToken ct)
        {
            _ = session;
            _ = reportProgressAsync;
            ct.ThrowIfCancellationRequested();
            Commands.Add(command.Clone());
            return Task.FromResult(new NyxIdChatTurnOperationExecution(resultFactory(command)));
        }
    }

    private sealed class BlockingOperationExecutor : INyxIdChatTurnOperationExecutor
    {
        private readonly TaskCompletionSource<NyxIdChatTurnOperationExecution> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<NyxIdChatOperationDispatchCommand> Commands { get; } = [];

        public async Task<NyxIdChatTurnOperationExecution> ExecuteAsync(
            NyxIdChatOperationDispatchCommand command,
            NyxIdChatTransientExecutionSession session,
            Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
            CancellationToken ct)
        {
            _ = session;
            _ = reportProgressAsync;
            Commands.Add(command.Clone());
            Started.TrySetResult();
            return await _completion.Task.WaitAsync(ct);
        }

        public void Complete(NyxIdChatOperationKey key) =>
            _completion.TrySetResult(new NyxIdChatTurnOperationExecution(
                new NyxIdChatOperationResultSignal
                {
                    Key = key.Clone(),
                    Tool = new NyxIdChatToolOperationResult
                    {
                        Receipt = new AgentToolReceipt
                        {
                            CallId = "call-alpha",
                            ToolName = "tool-alpha",
                            Status = AgentToolReceiptStatus.Success,
                        },
                        ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
                    },
                }));
    }

    private sealed class RecordingOperationDispatchPort(
        INyxIdChatTurnOperationExecutor executor)
        : INyxIdChatTurnOperationDispatchPort,
            INyxIdChatTurnOperationDispatchSession
    {
        private readonly Queue<IMessage> _pendingSignals = new();

        public async Task DispatchExecutionAsync(
            string turnActorId,
            NyxIdChatOperationDispatchCommand command,
            string correlationId,
            CancellationToken ct)
        {
            _ = turnActorId;
            _ = correlationId;
            var execution = await executor.ExecuteAsync(
                command,
                _executionSession,
                (progress, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    _pendingSignals.Enqueue(new NyxIdChatTurnOperationExecutionProgressSignal
                    {
                        Progress = progress.Clone(),
                    });
                    return Task.CompletedTask;
                },
                ct);
            _pendingSignals.Enqueue(new NyxIdChatTurnOperationExecutionCompletedSignal
            {
                Result = execution.Result.Clone(),
                Source = NyxIdChatTurnOperationCompletionSource.Execution,
            });
        }

        public Task DispatchReconciliationAsync(
            string turnActorId,
            NyxIdChatTurnOperationReconciliationInput input,
            string correlationId,
            CancellationToken ct) => throw new NotSupportedException();

        public INyxIdChatTurnOperationDispatchSession OpenSession() => this;

        public async Task DeliverPendingSignalsAsync(NyxIdChatTurnGAgent agent)
        {
            while (_pendingSignals.TryDequeue(out var signal))
            {
                await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", signal));
            }
        }

        private readonly NyxIdChatTransientExecutionSession _executionSession = new();
    }

    private sealed class RecordingActionPostconditionPort : INyxIdActionPostconditionPort
    {
        public List<NyxIdChatActionPostconditionInput> Inputs { get; } = [];

        public NyxIdChatActionPostconditionResult Result { get; set; } = new()
        {
            ActionRequestId = "action-alpha",
            Disposition = NyxIdChatActionDisposition.Completed,
            Verified = false,
            FailureCode = "NYXID_ACTION_POSTCONDITION_UNAVAILABLE",
            SafeMessage = "The action postcondition read model is unavailable.",
        };

        public Task<NyxIdChatActionPostconditionResult> VerifyAsync(
            NyxIdChatActionPostconditionInput input,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Inputs.Add(input.Clone());
            return Task.FromResult(Result.Clone());
        }
    }

    private sealed class CapabilityGeneratingReplyExecutor
        : IAgentRunReplyGenerationExecutorPort
    {
        public static AgentRunToolCall ToolCall { get; } = new()
        {
            Id = "call-alpha",
            Name = "tool-alpha",
            ArgumentsJson = "{}",
        };

        public int LlmExecutions { get; private set; }

        public int ToolContinuations { get; private set; }

        public int ToolExecutions { get; private set; }

        public AgentProfileTurnCatalog? LastTurnCatalog { get; private set; }

        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastTurnCatalog = request.TurnCatalog;
            return Task.FromResult(new AgentRunReplyStepState
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                NextStepIndex = 1,
                MaxToolRounds = 4,
            });
        }

        public Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LlmExecutions++;
            var result = new AgentRunLlmStepResult
            {
                Content = "I need a tool.",
                AccumulatedText = "I need a tool.",
                HasStreamedTextContent = true,
            };
            result.ToolCalls.Add(ToolCall.Clone());
            var continuation = new AgentRunNextLlmStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                LlmStepResult = result,
            };
            var capability = new AgentRunAuthorizedToolStep(
                request.RunId,
                request.Request.CorrelationId,
                request.Attempt,
                continuation.StepIndex,
                [ToolCall],
                _ =>
                {
                    ToolExecutions++;
                    return Task.FromResult(new AgentRunToolStepResult { AdvanceRound = true });
                });
            return Task.FromResult(new AgentRunLlmStepExecution(continuation, capability));
        }

        public async Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
            CancellationToken ct)
        {
            ToolContinuations++;
            var result = authorizedToolStep?.Matches(request) == true
                ? await authorizedToolStep.ExecuteAsync(ct)
                : new AgentRunToolStepResult { AdvanceRound = true };
            return new AgentRunNextToolStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                ToolStepResult = result,
            };
        }
    }

    private sealed class CountingProfileToolSetRegistry : IToolSetRegistry
    {
        public int ResolveCount { get; private set; }

        public IReadOnlyList<string> GetRegisteredNames() => ["profile.route"];

        public ToolSetResolveResult Resolve(string? name)
        {
            ResolveCount++;
            return ToolSetResolveResult.Success(
                "profile.route",
                [new SingleToolSource(new ProfileTool())]);
        }
    }

    private sealed class SingleToolSource(IAgentTool tool) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
    }

    private sealed class ProfileTool : IAgentTool
    {
        public string Name => "tool-alpha";
        public string Description => Name;
        public string ParametersSchema => "{}";
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private sealed class NoMatchProfileClassifier : IAgentProfileTurnClassifier
    {
        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(AgentProfileTurnClassificationResult.NoMatch());
    }

    private sealed class StreamingCapabilityReplyExecutor(
        AgentToolOperationAdmissionPayload? operationAdmission = null)
        : IAgentRunReplyGenerationExecutorPort
    {
        private static AgentRunToolCall AuthorizedToolCall { get; } = new()
        {
            Id = "call-alpha",
            Name = "tool-alpha",
            ArgumentsJson = "{\"value\":1}",
        };

        public int ToolExecutions { get; private set; }

        public int InitialStateBuilds { get; private set; }

        public List<AgentRunReplyStepState> LlmStepStates { get; } = [];

        public List<AgentRunReplyStepExecutionRequest> LlmStepRequests { get; } = [];

        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            InitialStateBuilds++;
            return Task.FromResult(new AgentRunReplyStepState
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                NextStepIndex = 1,
                MaxToolRounds = 4,
            });
        }

        public async Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LlmStepRequests.Add(request);
            LlmStepStates.Add(request.StepState.Clone());
            if (request.StepState.Round > 0)
            {
                var finalResult = new AgentRunLlmStepResult
                {
                    Content = "final response",
                    AccumulatedText = "final response",
                    FinishReason = "stop",
                    HasStreamedTextContent = true,
                };
                return new AgentRunLlmStepExecution(
                    new AgentRunNextLlmStepRequestedEvent
                    {
                        RunId = request.RunId,
                        CorrelationId = request.Request.CorrelationId,
                        TargetActorId = request.Request.TargetActorId,
                        Attempt = request.Attempt,
                        StepIndex = request.StepIndex + 1,
                        Request = request.Request.Clone(),
                        LlmStepResult = finalResult,
                    },
                    AuthorizedToolStep: null);
            }

            request.ReportChunkAsync.Should().NotBeNull();
            await request.ReportChunkAsync!(new LLMStreamChunk
            {
                DeltaContent = "visible text",
            }, ct);
            await request.ReportChunkAsync(new LLMStreamChunk
            {
                DeltaReasoningContent = "private reasoning",
            }, ct);
            await request.ReportChunkAsync(new LLMStreamChunk
            {
                ToolCallStarted = new ToolCallStartedChunk
                {
                    ToolCall = new ToolCall
                    {
                        Id = AuthorizedToolCall.Id,
                        Name = AuthorizedToolCall.Name,
                        ArgumentsJson = AuthorizedToolCall.ArgumentsJson,
                    },
                    Presentation = new ToolPresentationDescriptor
                    {
                        InvocationName = AuthorizedToolCall.Name,
                        DisplayName = "Tool Alpha",
                        Kind = ToolPresentationKind.Generic,
                        Availability = ToolAvailability.Available,
                    },
                },
            }, ct);

            var result = new AgentRunLlmStepResult
            {
                Content = "visible text",
                ReasoningContent = "private reasoning",
                AccumulatedText = "visible text",
                FinishReason = "tool_calls",
                HasStreamedTextContent = true,
            };
            result.ToolCalls.Add(AuthorizedToolCall.Clone());
            var continuation = new AgentRunNextLlmStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                LlmStepResult = result,
            };
            var capability = new AgentRunAuthorizedToolStep(
                request.RunId,
                request.Request.CorrelationId,
                request.Attempt,
                continuation.StepIndex,
                [AuthorizedToolCall],
                _ =>
                {
                    ToolExecutions++;
                    return Task.FromResult(new AgentRunToolStepResult
                    {
                        AdvanceRound = true,
                        ResultMessages =
                        {
                            new AgentRunChatMessage
                            {
                                Role = "tool",
                                ToolCallId = AuthorizedToolCall.Id,
                                Content = "{\"ok\":true}",
                            },
                        },
                        ToolReceipts =
                        {
                            new AgentToolReceipt
                            {
                                CallId = AuthorizedToolCall.Id,
                                ToolName = AuthorizedToolCall.Name,
                                Status = AgentToolReceiptStatus.Success,
                                ResultJson = "{\"ok\":true}",
                            },
                        },
                    });
                });
            return new AgentRunLlmStepExecution(
                continuation,
                capability,
                [
                    new AgentRunAuthorizedToolCallSafety(
                        AuthorizedToolCall.Id,
                        AuthorizedToolCall.Name,
                        AuthorizedToolCall.ArgumentsJson,
                        new AgentToolCallSafety(
                            RequiresApproval: false,
                            IsReadOnly: false,
                            IsDestructive: false),
                        SideEffectKind: "tool-alpha.update",
                        Presentation: new ToolPresentationDescriptor
                        {
                            InvocationName = AuthorizedToolCall.Name,
                            DisplayName = "Tool Alpha",
                            Kind = ToolPresentationKind.NyxIdOperation,
                            Availability = ToolAvailability.Available,
                            NyxIdOperation = new NyxIdOperationRef
                            {
                                ConnectedServiceId = operationAdmission?.ServiceInstanceId ??
                                                     "connected-service-alpha",
                                ServiceSlug = operationAdmission?.ServiceSlug ??
                                              "service-slug-alpha",
                                CatalogServiceSlug = "catalog-slug-alpha",
                                OperationId = operationAdmission?.PublishedEndpoint?.EndpointId ??
                                              string.Empty,
                                ReadinessCapabilityId = "readiness-capability-alpha",
                            },
                        },
                        OperationAdmission: operationAdmission?.Clone()),
                ]);
        }

        public async Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
            CancellationToken ct)
        {
            var result = authorizedToolStep?.Matches(request) == true
                ? await authorizedToolStep.ExecuteAsync(ct)
                : new AgentRunToolStepResult { AdvanceRound = true };
            return new AgentRunNextToolStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                ToolStepResult = result,
            };
        }
    }

    private sealed class RecordingDispatchPort(
        Func<string, EventEnvelope, Task>? onDispatch = null)
        : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public async Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((actorId, envelope.Clone()));
            if (onDispatch is not null)
                await onDispatch(actorId, envelope);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    private static AgentToolOperationAdmissionPayload ExactWriteAdmission() => new()
    {
        ServiceInstanceId = "connected-service-alpha",
        ServiceSlug = "service-slug-alpha",
        PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
        {
            EndpointId = "endpoint-alpha",
        },
        AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
        HttpMethod = "PATCH",
        PathTemplate = "/repositories/{repositoryId}",
        ContractDigest = new string('b', 64),
        CatalogDigest = $"sha256:{new string('a', 64)}",
        ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
        {
            Risk = AgentToolOperationRiskPayload.Write,
            Approval = AgentToolOperationApprovalPayload.Required,
            EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
            AllowedExecutionModes =
            {
                AgentToolOperationExecutionModePayload.Interactive,
            },
        },
    };

    private static AgentToolOperationAdmissionPayload ExactReadAdmission() => new()
    {
        ServiceInstanceId = "connected-service-alpha",
        ServiceSlug = "service-slug-alpha",
        PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
        {
            EndpointId = "endpoint-read-alpha",
        },
        AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
        HttpMethod = "GET",
        PathTemplate = "/repositories/{repositoryId}",
        ContractDigest = new string('d', 64),
        CatalogDigest = $"sha256:{new string('c', 64)}",
        ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
        {
            Risk = AgentToolOperationRiskPayload.ReadOnly,
            Approval = AgentToolOperationApprovalPayload.None,
            EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
            AllowedExecutionModes =
            {
                AgentToolOperationExecutionModePayload.Interactive,
                AgentToolOperationExecutionModePayload.Durable,
            },
        },
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NoopRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TimeoutRequests.Add(new RuntimeCallbackTimeoutRequest
            {
                ActorId = request.ActorId,
                CallbackId = request.CallbackId,
                TriggerEnvelope = request.TriggerEnvelope.Clone(),
                DueTime = request.DueTime,
                DeliveryMode = request.DeliveryMode,
            });
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                TimeoutRequests.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
