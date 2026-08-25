using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed partial class NyxIdChatTurnGAgentTests
{
    [Fact]
    public async Task DispatchSession_CancelReplacement_ShouldIsolateExecutionLeasesAndSessions()
    {
        var executor = new ReplacementAwareBlockingExecutor();
        var firstDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatch = new RecordingDispatchPort((_, envelope) =>
        {
            if (envelope.Payload?.Is(NyxIdChatTurnOperationExecutionCompletedSignal.Descriptor) == true)
            {
                var completed = envelope.Payload.Unpack<NyxIdChatTurnOperationExecutionCompletedSignal>();
                if (completed.Result.Key.OperationId == "operation-first")
                    firstDelivered.TrySetResult();
                if (completed.Result.Key.OperationId == "operation-second")
                    secondDelivered.TrySetResult();
            }
            return Task.CompletedTask;
        });
        var port = new NyxIdChatTurnOperationDispatchPort(
            executor,
            new UnavailableNyxIdChatTurnOperationReconciliationPort(),
            dispatch,
            TimeProvider.System,
            NullLogger<NyxIdChatTurnOperationDispatchPort>.Instance);
        var session = port.OpenSession();
        var first = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(operationId: "operation-first"),
            Llm = new NyxIdChatLLMOperationInput { Request = new ChatRequestEvent() },
        };
        var second = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(operationId: "operation-second", generation: 2),
            Llm = new NyxIdChatLLMOperationInput { Request = new ChatRequestEvent() },
        };

        await session.DispatchExecutionAsync("turn-actor-alpha", first, "correlation", CancellationToken.None);
        await executor.FirstStarted.Task;
        await session.DispatchExecutionAsync("turn-actor-alpha", second, "correlation", CancellationToken.None);
        await executor.SecondStarted.Task;

        await session.CancelExecutionAsync(second.Key, CancellationToken.None);
        await executor.SecondCancelled.Task;
        await secondDelivered.Task;
        executor.CompleteFirst(first.Key);
        await firstDelivered.Task;

        executor.Sessions.Should().HaveCount(2);
        executor.Sessions[0].Should().NotBeSameAs(executor.Sessions[1]);
    }

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
            if (envelope.Payload.Is(NyxIdChatTurnOperationDeliveryStatusSignal.Descriptor))
            {
                var delivery = envelope.Payload.Unpack<NyxIdChatTurnOperationDeliveryStatusSignal>();
                delivery.Key.Should().BeEquivalentTo(key);
                delivery.Admitted.Should().BeTrue();
                delivery.EffectDispatchWaterline.Should().Be(NyxIdChatEffectEvidence.NotApplied);
                return;
            }

            envelope.Payload.Is(NyxIdChatOperationResultSignal.Descriptor).Should().BeTrue();
            stateObservedAtDispatch = agent!.State.Clone();
            eventsObservedAtDispatch = await eventStore.GetEventsAsync("turn-actor-alpha");
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
        dispatch.Calls.Should().ContainSingle();
        dispatch.Calls[0].Envelope.Payload.Is(
            NyxIdChatTurnOperationDeliveryStatusSignal.Descriptor).Should().BeTrue();
        agent.State.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        (await eventStore.GetEventsAsync("turn-actor-alpha"))
            .Select(static item => item.EventData.TypeUrl)
            .Should().Equal(Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl);

        await operationDispatch.DeliverPendingSignalsAsync(agent);

        dispatch.Calls.Select(call => call.Envelope.Payload.TypeUrl).Should().Equal(
            Any.Pack(new NyxIdChatTurnOperationDeliveryStatusSignal()).TypeUrl,
            Any.Pack(new NyxIdChatOperationResultSignal()).TypeUrl);
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

    [Theory]
    [InlineData(NyxIdChatOperationResultSignal.ResultOneofCase.ActionPostcondition)]
    [InlineData(NyxIdChatOperationResultSignal.ResultOneofCase.ToolVerification)]
    [InlineData(NyxIdChatOperationResultSignal.ResultOneofCase.Failure)]
    public async Task PostconditionResult_ShouldRemainPendingUntilExactParentAcknowledgement(
        NyxIdChatOperationResultSignal.ResultOneofCase resultCase)
    {
        var command = PostconditionCommand(NyxIdChatActionDisposition.Completed);
        var result = CreatePostconditionResult(command.Key, resultCase);
        var executor = new RecordingOperationExecutor(_ => result.Clone());
        var eventStore = new InMemoryEventStoreForTests();
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        var dispatch = new RecordingDispatchPort();
        using var services = BuildEventSourcingServices(eventStore, callbacks);
        var agent = CreateAgent(services, operationDispatch, dispatch);
        await agent.ActivateAsync();

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", command));
        await operationDispatch.DeliverPendingSignalsAsync(agent);

        agent.State.ResultDelivered.Should().BeFalse();
        agent.State.PendingResult.Should().BeEquivalentTo(result);
        agent.State.ResultDeliveryAttempt.Should().Be(1);
        (await eventStore.GetEventsAsync("turn-actor-alpha"))
            .Select(static item => item.EventData.TypeUrl)
            .Should().Equal(
                Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationCompletedEvent()).TypeUrl);

        await agent.HandleOperationResultAcknowledgedAsync(
            new NyxIdChatTurnOperationResultAcknowledgedSignal
            {
                Key = command.Key.Clone(),
                ResultSha256 = ByteString.CopyFrom(new byte[SHA256.HashSizeInBytes]),
            });
        var staleGeneration = command.Key.Clone();
        staleGeneration.OperationGeneration++;
        await agent.HandleOperationResultAcknowledgedAsync(
            new NyxIdChatTurnOperationResultAcknowledgedSignal
            {
                Key = staleGeneration,
                ResultSha256 = ByteString.CopyFrom(SHA256.HashData(result.ToByteArray())),
            });

        agent.State.ResultDelivered.Should().BeFalse();
        agent.State.PendingResult.Should().NotBeNull();
        (await eventStore.GetEventsAsync("turn-actor-alpha")).Should().HaveCount(2);

        await agent.HandleOperationResultAcknowledgedAsync(
            new NyxIdChatTurnOperationResultAcknowledgedSignal
            {
                Key = command.Key.Clone(),
                ResultSha256 = ByteString.CopyFrom(SHA256.HashData(result.ToByteArray())),
            });

        agent.State.ResultDelivered.Should().BeTrue();
        agent.State.PendingResult.Should().BeNull();
        (await eventStore.GetEventsAsync("turn-actor-alpha"))
            .Select(static item => item.EventData.TypeUrl)
            .Should().Equal(
                Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationCompletedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnOperationDeliveredEvent()).TypeUrl);
    }

    [Theory]
    [InlineData(NyxIdChatOperationResultSignal.ResultOneofCase.ActionPostcondition)]
    [InlineData(NyxIdChatOperationResultSignal.ResultOneofCase.ToolVerification)]
    [InlineData(NyxIdChatOperationResultSignal.ResultOneofCase.Failure)]
    public async Task PostconditionDeliveryWatchdog_AfterRestart_ShouldRedispatchWithoutExecution(
        NyxIdChatOperationResultSignal.ResultOneofCase resultCase)
    {
        var command = PostconditionCommand(NyxIdChatActionDisposition.Completed);
        var result = CreatePostconditionResult(command.Key, resultCase);
        var executor = new RecordingOperationExecutor(_ => result.Clone());
        var eventStore = new InMemoryEventStoreForTests();
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        using var services = BuildEventSourcingServices(eventStore, callbacks);
        var original = CreateAgent(services, operationDispatch, new RecordingDispatchPort());
        await original.ActivateAsync();
        await original.HandleEventAsync(CreateEnvelope("turn-actor-alpha", command));
        await operationDispatch.DeliverPendingSignalsAsync(original);
        var deliveryWatchdog = callbacks.TimeoutRequests.Single(request =>
            request.TriggerEnvelope.Payload
                .Unpack<NyxIdChatRecoveryRequestedSignal>().Kind ==
            NyxIdChatRecoveryKind.OperationResultDeliveryWatchdog);

        var recoveredDispatch = new RecordingDispatchPort();
        var recovered = CreateAgent(
            services,
            new RecordingOperationDispatchPort(executor),
            recoveredDispatch);
        await recovered.ActivateAsync();
        await recovered.HandleEventAsync(deliveryWatchdog.TriggerEnvelope);

        executor.Commands.Should().ContainSingle();
        recoveredDispatch.Calls.Should().ContainSingle(call =>
            call.ActorId == command.Key.ConversationActorId &&
            call.Envelope.Payload.Is(NyxIdChatOperationResultSignal.Descriptor) &&
            call.Envelope.Payload.Unpack<NyxIdChatOperationResultSignal>().Equals(result));
        recovered.State.ResultDelivered.Should().BeFalse();
        recovered.State.PendingResult.Should().BeEquivalentTo(result);
        recovered.State.ResultDeliveryAttempt.Should().Be(2);
        (await eventStore.GetEventsAsync("turn-actor-alpha"))
            .Should().ContainSingle(item =>
                item.EventData.Is(
                    NyxIdChatTurnOperationResultRedeliveryAttemptedEvent.Descriptor));
    }

    [Theory]
    [InlineData(NyxIdChatOperationResultSignal.ResultOneofCase.ActionPostcondition)]
    [InlineData(NyxIdChatOperationResultSignal.ResultOneofCase.Failure)]
    public async Task FencedPostconditionResult_ShouldCommitReceiptAndDeliverChildWithoutRewritingParentTerminal(
        NyxIdChatOperationResultSignal.ResultOneofCase resultCase)
    {
        const string conversationActorId = "conversation-fenced-postcondition";
        const string turnId = "turn-fenced-postcondition";
        var turnActorId = NyxIdChatTurnActorIds.ForTurn(conversationActorId, turnId);
        var key = CreateKey("step-postcondition", "operation-postcondition", 1);
        key.ConversationActorId = conversationActorId;
        key.TurnId = turnId;
        key.TaskId = "task-fenced-postcondition";
        var result = CreatePostconditionResult(key, resultCase);
        var eventStore = new InMemoryEventStoreForTests();
        var parentState = CreateFencedPostconditionConversationState(key);
        await eventStore.AppendAsync(
            conversationActorId,
            [
                new StateEvent
                {
                    EventId = "fenced-postcondition-state",
                    AgentId = conversationActorId,
                    Version = 1,
                    Timestamp = parentState.UpdatedAt.Clone(),
                    EventType = NyxIdChatTurnStartedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new NyxIdChatTurnStartedEvent
                    {
                        State = parentState.Clone(),
                    }),
                },
            ],
            expectedVersion: 0);
        var actorDispatch = new DeterministicActorDispatchPort();
        using var services = BuildActorCompositionServices(eventStore);
        var conversation = new NyxIdChatConversationGAgent(
            new ImmediateActorRuntime(),
            actorDispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 10, 5, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(conversation, [conversationActorId]);
        await conversation.ActivateAsync();
        var beforeTask = conversation.State.ActiveTask.ToByteString();
        var beforeTurn = conversation.State.ActiveTurn.ToByteString();
        var beforeFence = conversation.State.ControlFence.ToByteString();
        var beforeOperation = conversation.State.ActiveTask.Steps.Single()
            .Operation.ToByteString();

        var executor = new RecordingOperationExecutor(_ => result.Clone());
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        var turn = CreateAgent(
            services,
            operationDispatch,
            actorDispatch,
            actorId: turnActorId);
        await turn.ActivateAsync();
        var command = PostconditionCommand(NyxIdChatActionDisposition.Completed);
        command.Key = key.Clone();
        await turn.HandleEventAsync(CreateEnvelope(turnActorId, command));
        await operationDispatch.DeliverPendingSignalsAsync(turn);
        turn.State.ResultDelivered.Should().BeFalse();
        turn.State.PendingResult.Should().BeEquivalentTo(result);

        var resultEnvelope = actorDispatch.TakeSingle(
            conversationActorId,
            static envelope => envelope.Payload.Is(NyxIdChatOperationResultSignal.Descriptor));
        await conversation.HandleEventAsync(resultEnvelope);

        var committed = (await eventStore.GetEventsAsync(conversationActorId))[^1]
            .EventData.Unpack<NyxIdChatLateOperationEvidenceCommittedEvent>();
        committed.ConsumedPostconditionFailure.Should().NotBeNull();
        committed.ConsumedPostconditionFailure.FailureCode.Should().Be(
            resultCase == NyxIdChatOperationResultSignal.ResultOneofCase.Failure
                ? "POSTCONDITION_FAILED"
                : "NYXID_CHAT_POSTCONDITION_RESULT_CONSUMED_AFTER_CONTROL_FENCE");
        committed.State.ActiveTask.ToByteString().Should().Equal(beforeTask);
        committed.State.ActiveTurn.ToByteString().Should().Equal(beforeTurn);
        committed.State.ControlFence.ToByteString().Should().Equal(beforeFence);
        committed.State.ActiveTask.Steps.Single().Operation.ToByteString().Should()
            .Equal(beforeOperation);
        committed.ProgressSequence.Should().Be(parentState.ProgressSequence + 1);
        committed.State.ResultAcknowledgementFences.Should().ContainSingle();
        var acknowledgementEnvelope = actorDispatch.TakeSingle(
            turnActorId,
            static envelope => envelope.Payload.Is(
                NyxIdChatTurnOperationResultAcknowledgedSignal.Descriptor));
        var acknowledgement = acknowledgementEnvelope.Payload
            .Unpack<NyxIdChatTurnOperationResultAcknowledgedSignal>();
        acknowledgement.ResultSha256.ToByteArray().Should()
            .Equal(SHA256.HashData(result.ToByteArray()));
        await turn.HandleEventAsync(acknowledgementEnvelope);
        turn.State.ResultDelivered.Should().BeTrue();
        turn.State.PendingResult.Should().BeNull();

        var recovered = new NyxIdChatConversationGAgent(
            new ImmediateActorRuntime(),
            new RecordingDispatchPort(),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 10, 5, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(recovered, [conversationActorId]);
        await recovered.ActivateAsync();
        recovered.State.ActiveTask.ToByteString().Should().Equal(beforeTask);
        recovered.State.ActiveTurn.ToByteString().Should().Equal(beforeTurn);
        recovered.State.ControlFence.ToByteString().Should().Equal(beforeFence);
        recovered.State.ActiveTask.Steps.Single().Operation.ToByteString().Should()
            .Equal(beforeOperation);
        recovered.State.ResultAcknowledgementFences.Should().ContainSingle();
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
        dispatch.Calls.Select(call => call.Envelope.Payload.TypeUrl).Should().Equal(
            Any.Pack(new NyxIdChatTurnOperationDeliveryStatusSignal()).TypeUrl,
            Any.Pack(new NyxIdChatOperationResultSignal()).TypeUrl,
            Any.Pack(new NyxIdChatTurnOperationDeliveryStatusSignal()).TypeUrl,
            Any.Pack(new NyxIdChatTurnOperationDeliveryStatusSignal()).TypeUrl,
            Any.Pack(new NyxIdChatOperationResultSignal()).TypeUrl);

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", llm.Clone()));
        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", tool.Clone()));

        executor.Commands.Should().HaveCount(2);
        dispatch.Calls.Select(call => call.Envelope.Payload.TypeUrl).Should().Equal(
            Any.Pack(new NyxIdChatTurnOperationDeliveryStatusSignal()).TypeUrl,
            Any.Pack(new NyxIdChatOperationResultSignal()).TypeUrl,
            Any.Pack(new NyxIdChatTurnOperationDeliveryStatusSignal()).TypeUrl,
            Any.Pack(new NyxIdChatTurnOperationDeliveryStatusSignal()).TypeUrl,
            Any.Pack(new NyxIdChatOperationResultSignal()).TypeUrl,
            Any.Pack(new NyxIdChatTurnOperationDeliveryStatusSignal()).TypeUrl);
        dispatch.Calls
            .Where(call => call.Envelope.Payload.Is(NyxIdChatOperationResultSignal.Descriptor))
            .Select(call => call.Envelope.Payload.Unpack<NyxIdChatOperationResultSignal>().Key)
            .Should().BeEquivalentTo([llm.Key, tool.Key], options => options.WithStrictOrdering(),
                "exact replays acknowledge delivery without repeating operation execution or results");
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
    public async Task EffectRecoveryCredential_ShouldSurvivePassivationUntilFrozenVerificationTerminal()
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 8, 4, 0, 0, TimeSpan.Zero));
        var vault = new InMemorySecretVault(clock);
        var eventStore = new InMemoryEventStoreForTests();
        var operationDispatch = new RecordingOperationDispatchPort(
            new RecordingOperationExecutor(command => new NyxIdChatOperationResultSignal
            {
                Key = command.Key.Clone(),
            }));
        var actorDispatch = new RecordingDispatchPort();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(
            services,
            operationDispatch,
            actorDispatch,
            timeProvider: clock,
            secretVault: vault);
        await agent.ActivateAsync();
        var admission = ExactWriteAdmission();
        admission.ReadBack = new AgentToolOperationReadBackPayload
        {
            ReadOperation = ExactReadAdmission(),
            Arguments = new Struct(),
            CheckName = "resource-visible",
            Assertion = new AgentToolReadBackAssertionPayload
            {
                Match = AgentToolReadBackMatchPayload.Exists,
                JsonPointer = "/data",
            },
        };
        var command = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(),
            Tool = new NyxIdChatToolOperationInput
            {
                CallId = "call-alpha",
                ToolName = "tool-alpha",
                ArgumentsJson = "{}",
                MayChangeExternalState = true,
                IdempotencyKey = "operation-alpha",
                OperationAdmission = admission,
                ToolContext = new AgentToolExecutionContextPayload
                {
                    Caller = new AgentToolCallerContextPayload
                    {
                        OwnerSubject = "owner-alpha",
                    },
                    Credentials = new AgentToolCredentialsPayload
                    {
                        NyxIdAccessToken = "delegation-token-alpha",
                        NyxIdCredentialKind =
                            AgentToolNyxIdCredentialKindPayload.ProxyDelegation,
                    },
                },
            },
        };

        await agent.HandleOperationAsync(command);

        var credential = agent.State.RecoveryCredential;
        credential.Should().NotBeNull();
        clock.Advance(TimeSpan.FromMinutes(6));
        (await vault.ResolveAsync(new ResolveSecretRequest(
            credential.Ref,
            credential.Purpose,
            credential.OwnerScopeKey,
            credential.SubjectId,
            "test recovery retention"))).Resolved.Should().BeTrue();

        await agent.HandleOperationExecutionCompletedAsync(
            new NyxIdChatTurnOperationExecutionCompletedSignal
            {
                Source = NyxIdChatTurnOperationCompletionSource.Execution,
                Result = new NyxIdChatOperationResultSignal
                {
                    Key = command.Key.Clone(),
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
                },
            });

        (await vault.ResolveAsync(new ResolveSecretRequest(
            credential.Ref,
            credential.Purpose,
            credential.OwnerScopeKey,
            credential.SubjectId,
            "test effect completion retention"))).Resolved.Should().BeTrue();

        var verificationExecutor = new RecordingOperationExecutor(command =>
            new NyxIdChatOperationResultSignal
            {
                Key = command.Key.Clone(),
                ToolVerification = new NyxIdChatToolVerificationResult
                {
                    EffectStepId = command.ToolVerification.EffectStepId,
                    Disposition = NyxIdChatToolVerificationDisposition.NotApplied,
                    ReadOperation = command.ToolVerification.ReadBack.ReadOperation.Clone(),
                    CheckName = command.ToolVerification.ReadBack.CheckName,
                },
            });
        var recoveredDispatch = new RecordingOperationDispatchPort(verificationExecutor);
        var recovered = CreateAgent(
            services,
            recoveredDispatch,
            actorDispatch,
            timeProvider: clock,
            secretVault: vault);
        await recovered.ActivateAsync();
        var verification = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey("step-verification", "operation-verification"),
            ToolVerification = new NyxIdChatToolVerificationInput
            {
                EffectStepId = command.Key.StepId,
                ReadBack = admission.ReadBack.Clone(),
            },
        };

        await recovered.HandleOperationAsync(verification);
        verificationExecutor.Commands.Should().ContainSingle();
        verificationExecutor.Commands[0].ToolVerification.ToolContext.Credentials
            .NyxIdAccessToken.Should().Be("delegation-token-alpha");
        await recoveredDispatch.DeliverPendingSignalsAsync(recovered);

        (await vault.ResolveAsync(new ResolveSecretRequest(
            credential.Ref,
            credential.Purpose,
            credential.OwnerScopeKey,
            credential.SubjectId,
            "test verification terminal revocation"))).FailureReason.Should()
            .Be(SecretResolutionFailureReason.Revoked);
    }

    [Fact]
    public async Task ToolEffectCommand_ShouldPersistExactAdmissionBeforeDispatch()
    {
        var admission = ExactWriteAdmission();
        var executor = new RecordingOperationExecutor(command =>
            new NyxIdChatOperationResultSignal
            {
                Key = command.Key.Clone(),
                Tool = new NyxIdChatToolOperationResult
                {
                    Receipt = new AgentToolReceipt
                    {
                        Status = AgentToolReceiptStatus.Success,
                        CallId = command.Tool.CallId,
                        ToolName = command.Tool.ToolName,
                    },
                    ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
                },
            });
        var eventStore = new InMemoryEventStoreForTests();
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(services, operationDispatch, new RecordingDispatchPort());
        await agent.ActivateAsync();
        var command = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
            Tool = new NyxIdChatToolOperationInput
            {
                CallId = "call-alpha",
                ToolName = "tool-alpha",
                ArgumentsJson = "{\"value\":1}",
                MayChangeExternalState = true,
                IdempotencyKey = "operation-tool-alpha",
                OperationAdmission = admission.Clone(),
            },
        };

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", command));

        executor.Commands.Should().ContainSingle(candidate =>
            candidate.InputCase == NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
        agent.State.OperationAdmission.Should().BeEquivalentTo(admission);
        agent.State.IdempotencyKey.Should().Be(command.Key.OperationId);
        agent.State.EffectDispatchWaterline.Should().Be(
            NyxIdChatEffectEvidence.MayHaveChanged);
        (await eventStore.GetEventsAsync("turn-actor-alpha"))
            .Select(static item => item.EventData.TypeUrl)
            .Should().Equal(
                Any.Pack(new NyxIdChatTurnOperationAdmittedEvent()).TypeUrl,
                Any.Pack(new NyxIdChatTurnEffectDispatchStartedEvent()).TypeUrl);
    }

    [Fact]
    public async Task DurableRetryTool_AfterReactivation_ShouldAcceptExactGenerationTwo()
    {
        var executor = new RecordingOperationExecutor(command =>
            command.InputCase == NyxIdChatOperationDispatchCommand.InputOneofCase.ToolVerification
                ? new NyxIdChatOperationResultSignal
                {
                    Key = command.Key.Clone(),
                    ToolVerification = new NyxIdChatToolVerificationResult
                    {
                        EffectStepId = "step-effect-alpha",
                        Disposition = NyxIdChatToolVerificationDisposition.NotApplied,
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
                            CallId = command.Tool.CallId,
                            ToolName = command.Tool.ToolName,
                        },
                        ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
                    },
                });
        var eventStore = new InMemoryEventStoreForTests();
        var originalOperationDispatch = new RecordingOperationDispatchPort(executor);
        using var services = BuildEventSourcingServices(eventStore);
        var original = CreateAgent(
            services,
            originalOperationDispatch,
            new RecordingDispatchPort());
        await original.ActivateAsync();
        var sourceKey = CreateKey("step-verification-alpha", "operation-verification-alpha", 1);
        await original.HandleEventAsync(CreateEnvelope(
            "turn-actor-alpha",
            new NyxIdChatOperationDispatchCommand
            {
                Key = sourceKey.Clone(),
                ToolVerification = new NyxIdChatToolVerificationInput
                {
                    EffectStepId = "step-effect-alpha",
                },
            }));
        await originalOperationDispatch.DeliverPendingSignalsAsync(original);
        await AcknowledgePendingResultAsync(original);
        original.State.ResultDelivered.Should().BeTrue();
        original.State.OperationKind.Should().Be(NyxIdChatStepKind.Postcondition);
        original.State.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);

        var retry = DurableGenerationTwoToolCommand(sourceKey);
        retry.Key = CreateKey("step-effect-alpha", "operation-effect-retry-alpha", 2);
        retry.Tool.IdempotencyKey = retry.Key.OperationId;

        var reactivationDispatch = new RecordingDispatchPort();
        var reactivated = CreateAgent(
            services,
            new RecordingOperationDispatchPort(executor),
            reactivationDispatch);
        await reactivated.ActivateAsync();
        reactivationDispatch.Calls.Should().BeEmpty(
            "the delivered source operation requires no activation-time recovery");

        await reactivated.HandleEventAsync(CreateEnvelope("turn-actor-alpha", retry));

        executor.Commands.Where(command =>
                command.InputCase == NyxIdChatOperationDispatchCommand.InputOneofCase.Tool)
            .Should().ContainSingle();
        reactivated.State.AdmittedOperation.Should().BeEquivalentTo(retry.Key);
    }

    [Fact]
    public async Task OperationDeliveryProbe_WhenItCommitsFirst_ShouldFenceDelayedExactCommand()
    {
        var executor = new RecordingOperationExecutor(command =>
            new NyxIdChatOperationResultSignal
            {
                Key = command.Key.Clone(),
                Llm = new NyxIdChatLLMOperationResult { Content = "Too late." },
            });
        var eventStore = new InMemoryEventStoreForTests();
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        var conversationDispatch = new RecordingDispatchPort();
        using var services = BuildEventSourcingServices(eventStore);
        var actorId = NyxIdChatTurnActorIds.ForTurn("conversation-alpha", "turn-alpha");
        var agent = CreateAgent(
            services,
            operationDispatch,
            conversationDispatch,
            actorId: actorId);
        await agent.ActivateAsync();
        var command = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = new ChatRequestEvent
                {
                    Prompt = "must stay fenced",
                    SessionId = "turn-alpha",
                },
            },
        };

        await agent.HandleEventAsync(CreateEnvelope(
            actorId,
            new NyxIdChatTurnOperationDeliveryProbeCommand
            {
                Key = command.Key.Clone(),
            }));
        await agent.HandleEventAsync(CreateEnvelope(actorId, command));

        agent.State.FencedOperationDeliveries.Should().ContainSingle(key =>
            key.Equals(command.Key));
        agent.State.AdmittedOperation.Should().BeNull();
        executor.Commands.Should().BeEmpty();
        conversationDispatch.Calls
            .Where(call => call.Envelope.Payload.Is(
                NyxIdChatTurnOperationDeliveryStatusSignal.Descriptor))
            .Select(call => call.Envelope.Payload.Unpack<
                NyxIdChatTurnOperationDeliveryStatusSignal>())
            .Should().HaveCount(2).And.OnlyContain(signal => !signal.Admitted);
    }

    [Fact]
    public async Task OperationDelivery_WhenExactCommandIsReplayed_ShouldReAckWithoutReexecution()
    {
        var executor = new RecordingOperationExecutor(command =>
            new NyxIdChatOperationResultSignal
            {
                Key = command.Key.Clone(),
                Llm = new NyxIdChatLLMOperationResult { Content = "Once." },
            });
        var eventStore = new InMemoryEventStoreForTests();
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        var conversationDispatch = new RecordingDispatchPort();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(services, operationDispatch, conversationDispatch);
        await agent.ActivateAsync();
        var command = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = new ChatRequestEvent
                {
                    Prompt = "execute once",
                    SessionId = "turn-alpha",
                },
            },
        };

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", command));
        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", command));

        executor.Commands.Should().ContainSingle();
        conversationDispatch.Calls.Count(call =>
            call.Envelope.Payload.Is(NyxIdChatTurnOperationDeliveryStatusSignal.Descriptor))
            .Should().Be(2);
    }

    [Fact]
    public async Task DirectDurableRetry_ShouldRequireExactDeliveredNotAppliedSource()
    {
        var executor = new RecordingOperationExecutor(command =>
            command.Key.OperationGeneration == 1
                ? new NyxIdChatOperationResultSignal
                {
                    Key = command.Key.Clone(),
                    Failure = new NyxIdChatOperationFailure
                    {
                        FailureCode = "NYXID_EFFECT_NOT_APPLIED",
                        SafeMessage = "The effect was not applied.",
                        ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
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
                            CallId = command.Tool.CallId,
                            ToolName = command.Tool.ToolName,
                        },
                        ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
                    },
                });
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        using var services = BuildEventSourcingServices(new InMemoryEventStoreForTests());
        var agent = CreateAgent(services, operationDispatch, new RecordingDispatchPort());
        await agent.ActivateAsync();
        var sourceKey = CreateKey("step-effect-alpha", "operation-effect-alpha", 1);
        await agent.HandleEventAsync(CreateEnvelope(
            "turn-actor-alpha",
            new NyxIdChatOperationDispatchCommand
            {
                Key = sourceKey.Clone(),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = "call-effect-alpha",
                    ToolName = "effect-tool",
                    MayChangeExternalState = true,
                    IdempotencyKey = sourceKey.OperationId,
                },
            }));
        await operationDispatch.DeliverPendingSignalsAsync(agent);
        agent.State.ResultDelivered.Should().BeTrue();
        agent.State.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);

        var retryKey = CreateKey("step-effect-alpha", "operation-effect-retry-alpha", 2);
        var retry = new NyxIdChatOperationDispatchCommand
        {
            Key = retryKey.Clone(),
            Tool = new NyxIdChatToolOperationInput
            {
                CallId = "call-effect-alpha",
                ToolName = "effect-tool",
                MayChangeExternalState = true,
                IdempotencyKey = retryKey.OperationId,
                RematerializeDurableAuthorization = true,
                RetryAuthorizationSourceKey = CreateKey(
                    "step-effect-alpha",
                    "operation-forged-alpha",
                    1),
            },
        };

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", retry));

        executor.Commands.Should().ContainSingle(
            "a caller flag without the exact committed source proof cannot authorize retry");
        agent.State.AdmittedOperation.Should().BeEquivalentTo(sourceKey);

        retry.Tool.RetryAuthorizationSourceKey = sourceKey.Clone();
        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", retry));

        executor.Commands.Should().HaveCount(2);
        agent.State.AdmittedOperation.Should().BeEquivalentTo(retryKey);
    }

    [Fact]
    public async Task ExactServiceApprovalRequired_AfterReactivation_ShouldAcceptTypedContinuation()
    {
        var continuation = GenerationTwoExactApprovalContinuation();
        var executor = new RecordingOperationExecutor(command =>
            command.Key.OperationGeneration == 1
                ? new NyxIdChatOperationResultSignal
                {
                    Key = command.Key.Clone(),
                    Tool = new NyxIdChatToolOperationResult
                    {
                        Receipt = new AgentToolReceipt
                        {
                            Status = AgentToolReceiptStatus.ApprovalRequired,
                            ApprovalRequestId = "approval-generation-one",
                            CallId = command.Tool.CallId,
                            ToolName = command.Tool.ToolName,
                        },
                        ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
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
                            CallId = command.ToolApprovalContinuation.ToolCallId,
                            ToolName = command.ToolApprovalContinuation.ToolName,
                        },
                        ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
                    },
                });
        var eventStore = new InMemoryEventStoreForTests();
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        var conversationDispatch = new RecordingDispatchPort();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(
            services,
            operationDispatch,
            conversationDispatch);
        await agent.ActivateAsync();
        var sourceKey = CreateKey(
            "step-tool-alpha",
            "operation-tool-source-alpha",
            1);
        await agent.HandleEventAsync(CreateEnvelope(
            "turn-actor-alpha",
            new NyxIdChatOperationDispatchCommand
            {
                Key = sourceKey.Clone(),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = "call-alpha",
                    ToolName = "tool-alpha",
                    ArgumentsJson = "{\"value\":1}",
                    MayChangeExternalState = true,
                    IdempotencyKey = sourceKey.OperationId,
                    OperationAdmission =
                        continuation.ToolApprovalContinuation.OperationAdmission.Clone(),
                },
            }));
        await operationDispatch.DeliverPendingSignalsAsync(agent);

        agent.State.ResultDelivered.Should().BeTrue();
        agent.State.Phase.Should().Be(NyxIdChatOperationPhase.Succeeded);
        agent.State.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
        agent.State.ExactServiceRecoveryStage.Should().Be(
            NyxIdChatExactServiceRecoveryStage.Create);
        agent.State.ToolReceiptStatus.Should().Be(
            AgentToolReceiptStatus.ApprovalRequired);

        operationDispatch = new RecordingOperationDispatchPort(executor);
        conversationDispatch = new RecordingDispatchPort();
        agent = CreateAgent(
            services,
            operationDispatch,
            conversationDispatch);
        await agent.ActivateAsync();
        agent.State.ToolReceiptStatus.Should().Be(
            AgentToolReceiptStatus.ApprovalRequired);

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", continuation));

        executor.Commands.Should().HaveCount(2);
        agent.State.AdmittedOperation.Should().BeEquivalentTo(continuation.Key);
        agent.State.ExactServiceRecoveryStage.Should().Be(
            NyxIdChatExactServiceRecoveryStage.DecideRedeem);
    }

    [Fact]
    public async Task LegacyExactServiceApprovalRequired_AfterReactivation_ShouldAcceptTypedContinuation()
    {
        var continuation = GenerationTwoExactApprovalContinuation();
        var sourceKey = CreateKey(
            "step-tool-alpha",
            "operation-tool-source-alpha",
            1);
        var eventStore = new InMemoryEventStoreForTests();
        await AppendDeliveredExactServiceTurnAsync(
            eventStore,
            sourceKey,
            continuation.ToolApprovalContinuation.OperationAdmission,
            AgentToolReceiptStatus.Unspecified);
        var executor = new RecordingOperationExecutor(command =>
            new NyxIdChatOperationResultSignal
            {
                Key = command.Key.Clone(),
                Tool = new NyxIdChatToolOperationResult
                {
                    Receipt = new AgentToolReceipt
                    {
                        Status = AgentToolReceiptStatus.Success,
                        CallId = command.ToolApprovalContinuation.ToolCallId,
                        ToolName = command.ToolApprovalContinuation.ToolName,
                    },
                    ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
                },
            });
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        var conversationDispatch = new RecordingDispatchPort();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(
            services,
            operationDispatch,
            conversationDispatch);
        await agent.ActivateAsync();

        agent.State.ToolReceiptStatus.Should().Be(AgentToolReceiptStatus.Unspecified);
        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", continuation));

        executor.Commands.Should().ContainSingle();
        agent.State.AdmittedOperation.Should().BeEquivalentTo(continuation.Key);
        agent.State.ExactServiceRecoveryStage.Should().Be(
            NyxIdChatExactServiceRecoveryStage.DecideRedeem);
    }

    [Theory]
    [InlineData(AgentToolReceiptStatus.Success, "")]
    [InlineData(AgentToolReceiptStatus.AuthorizationRequired, "")]
    [InlineData(AgentToolReceiptStatus.Unspecified, "NYXID_UNAUTHORIZED")]
    public async Task ExactServiceNonApprovalTerminal_ShouldFenceTypedContinuation(
        AgentToolReceiptStatus receiptStatus,
        string terminalCode)
    {
        var continuation = GenerationTwoExactApprovalContinuation();
        var sourceKey = CreateKey(
            "step-tool-alpha",
            "operation-tool-source-alpha",
            1);
        var eventStore = new InMemoryEventStoreForTests();
        await AppendDeliveredExactServiceTurnAsync(
            eventStore,
            sourceKey,
            continuation.ToolApprovalContinuation.OperationAdmission,
            receiptStatus,
            terminalCode);
        var executor = new RecordingOperationExecutor(command =>
            new NyxIdChatOperationResultSignal
            {
                Key = command.Key.Clone(),
                Tool = new NyxIdChatToolOperationResult
                {
                    Receipt = new AgentToolReceipt
                    {
                        Status = AgentToolReceiptStatus.Success,
                        CallId = command.ToolApprovalContinuation.ToolCallId,
                        ToolName = command.ToolApprovalContinuation.ToolName,
                    },
                    ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
                },
            });
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(
            services,
            new RecordingOperationDispatchPort(executor),
            new RecordingDispatchPort());
        await agent.ActivateAsync();

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", continuation));

        executor.Commands.Should().BeEmpty();
        agent.State.AdmittedOperation.Should().BeEquivalentTo(sourceKey);
        agent.State.ExactServiceRecoveryStage.Should().Be(
            NyxIdChatExactServiceRecoveryStage.Create);
    }

    [Fact]
    public async Task ExactServiceApprovalRequired_WithNonApprovalAdmission_ShouldFenceTypedContinuation()
    {
        var continuation = GenerationTwoExactApprovalContinuation();
        var sourceAdmission =
            continuation.ToolApprovalContinuation.OperationAdmission.Clone();
        continuation.ToolApprovalContinuation.OperationAdmission.ExecutionPolicy.Approval =
            AgentToolOperationApprovalPayload.None;
        var sourceKey = CreateKey(
            "step-tool-alpha",
            "operation-tool-source-alpha",
            1);
        var eventStore = new InMemoryEventStoreForTests();
        await AppendDeliveredExactServiceTurnAsync(
            eventStore,
            sourceKey,
            sourceAdmission,
            AgentToolReceiptStatus.ApprovalRequired);
        var executor = new RecordingOperationExecutor(command =>
            new NyxIdChatOperationResultSignal
            {
                Key = command.Key.Clone(),
                Tool = new NyxIdChatToolOperationResult
                {
                    Receipt = new AgentToolReceipt
                    {
                        Status = AgentToolReceiptStatus.Success,
                        CallId = command.ToolApprovalContinuation.ToolCallId,
                        ToolName = command.ToolApprovalContinuation.ToolName,
                    },
                    ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
                },
            });
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(
            services,
            new RecordingOperationDispatchPort(executor),
            new RecordingDispatchPort());
        await agent.ActivateAsync();

        agent.State.ToolReceiptStatus.Should().Be(AgentToolReceiptStatus.ApprovalRequired);
        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", continuation));

        executor.Commands.Should().BeEmpty();
        agent.State.AdmittedOperation.Should().BeEquivalentTo(sourceKey);
        agent.State.ExactServiceRecoveryStage.Should().Be(
            NyxIdChatExactServiceRecoveryStage.Create);
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
        call.OperationAdmission.DurableAuthorization.Should().NotBeNull();
        var credentialFreeAdmission = call.OperationAdmission.Clone();
        credentialFreeAdmission.DurableAuthorization = null;
        credentialFreeAdmission.Should().BeEquivalentTo(admitted);
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
            NyxIdChatOperationProgressSignal.ProgressOneofCase.StreamingBatch,
            NyxIdChatOperationProgressSignal.ProgressOneofCase.ToolStarted);
        progress.Should().OnlyContain(signal => signal.Key.Equals(command.Key));
        progress[0].Text.Delta.Should().Be("visible text");
        progress[1].StreamingBatch.Segments.Should().ContainSingle();
        progress[1].StreamingBatch.Segments[0].Reasoning.Delta.Should().Be("private reasoning");
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
    public async Task OperationExecutor_Llm_ShouldCoalesceTinyDeltasBeforeConversationActor()
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 8, 4, 0, 0, TimeSpan.Zero));
        var generationExecutor = new TinyDeltaReplyExecutor(clock);
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            null,
            new NyxIdChatDelegationCredentialLifecyclePort(clock),
            new NyxIdChatToolVerificationPort(),
            clock);
        var progress = new List<NyxIdChatOperationProgressSignal>();

        var execution = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "stream bounded progress",
                        SessionId = "turn-alpha",
                    },
                },
            },
            new NyxIdChatTransientExecutionSession(),
            (signal, _) =>
            {
                progress.Add(signal.Clone());
                return Task.CompletedTask;
            },
            CancellationToken.None);

        progress.Should().HaveCount(3,
            "the first delta is immediate, the one-second waterline flushes once, and terminal flushes the tail");
        progress.Select(static item => item.Sequence).Should().Equal(1, 2, 3);
        string.Concat(progress.SelectMany(static item => item.ProgressCase switch
            {
                NyxIdChatOperationProgressSignal.ProgressOneofCase.Text => [item.Text.Delta],
                NyxIdChatOperationProgressSignal.ProgressOneofCase.StreamingBatch =>
                    item.StreamingBatch.Segments
                        .Where(static segment => segment.ProgressCase ==
                            NyxIdChatStreamingProgressSegment.ProgressOneofCase.Text)
                        .Select(static segment => segment.Text.Delta),
                _ => [],
            })).Should()
            .Be(new string('x', 1_000));
        execution.Result.Llm.Content.Should().Be(new string('x', 1_000));
    }

    [Fact]
    public async Task TinyDeltaActorBacklog_ShouldBoundProgressAndNotStarveStopOrDelete()
    {
        const string conversationActorId = "conversation-alpha";
        const string turnId = "turn-alpha";
        var turnActorId = NyxIdChatTurnActorIds.ForTurn(conversationActorId, turnId);
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 8, 4, 0, 0, TimeSpan.Zero));
        var eventStore = new InMemoryEventStoreForTests();
        var actorDispatch = new DeterministicActorDispatchPort();
        using var services = BuildActorCompositionServices(eventStore);
        var conversation = new NyxIdChatConversationGAgent(
            new ImmediateActorRuntime(),
            actorDispatch,
            clock)
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(conversation, [conversationActorId]);
        await conversation.ActivateAsync();
        await conversation.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatStartTurnCommand
            {
                ScopeId = "scope-alpha",
                ConversationActorId = conversationActorId,
                TurnId = turnId,
                TaskId = "task-alpha",
                ClientRequestId = "client-alpha",
                CommandId = "command-alpha",
                CorrelationId = "correlation-alpha",
                Prompt = "stream bounded progress",
            }));

        var operationEnvelope = actorDispatch.TakeSingle(
            turnActorId,
            static envelope => envelope.Payload.Is(
                NyxIdChatOperationDispatchCommand.Descriptor));
        var operationExecutor = new NyxIdChatTurnOperationExecutor(
            new TinyDeltaReplyExecutor(clock),
            new UnavailableNyxIdActionPostconditionPort(),
            null,
            new NyxIdChatDelegationCredentialLifecyclePort(clock),
            new NyxIdChatToolVerificationPort(),
            clock);
        var operationDispatch = new NyxIdChatTurnOperationDispatchPort(
            operationExecutor,
            new UnavailableNyxIdChatTurnOperationReconciliationPort(),
            actorDispatch,
            clock,
            NullLogger<NyxIdChatTurnOperationDispatchPort>.Instance);
        var turn = CreateAgent(
            services,
            operationDispatch,
            actorDispatch,
            timeProvider: clock,
            actorId: turnActorId);
        await turn.ActivateAsync();

        await turn.HandleEventAsync(operationEnvelope);
        await actorDispatch.ExecutionCompleted.Task;

        var turnProgress = actorDispatch.TakeAll(
            turnActorId,
            static envelope => envelope.Payload.Is(
                NyxIdChatTurnOperationExecutionProgressSignal.Descriptor));
        turnProgress.Should().HaveCount(3,
            "1000 tiny deltas must cross the actor dispatch boundary as bounded batches");
        var batchedProgress = turnProgress
            .Select(envelope => envelope.Payload
                .Unpack<NyxIdChatTurnOperationExecutionProgressSignal>().Progress)
            .ToArray();
        batchedProgress.Select(static progress => progress.Sequence).Should().Equal(1, 2, 3);
        string.Concat(batchedProgress.SelectMany(static progress => progress.ProgressCase switch
            {
                NyxIdChatOperationProgressSignal.ProgressOneofCase.Text =>
                    [progress.Text.Delta],
                NyxIdChatOperationProgressSignal.ProgressOneofCase.StreamingBatch =>
                    progress.StreamingBatch.Segments.Select(static segment =>
                        segment.ProgressCase ==
                        NyxIdChatStreamingProgressSegment.ProgressOneofCase.Text
                            ? segment.Text.Delta
                            : segment.Reasoning.Delta),
                _ => [],
            }))
            .Should().Be(new string('x', 1_000));

        var deliveryStatus = actorDispatch.TakeSingle(
            conversationActorId,
            static envelope => envelope.Payload.Is(
                NyxIdChatTurnOperationDeliveryStatusSignal.Descriptor));
        await conversation.HandleEventAsync(deliveryStatus);
        var stopExpectedVersion = (await eventStore.GetEventsAsync(conversationActorId))[^1].Version;

        foreach (var progressEnvelope in turnProgress)
            await turn.HandleEventAsync(progressEnvelope);
        var conversationProgress = actorDispatch.TakeAll(
            conversationActorId,
            static envelope => envelope.Payload.Is(
                NyxIdChatOperationProgressSignal.Descriptor));
        conversationProgress.Should().HaveCount(3);

        await conversation.HandleEventAsync(conversationProgress[0]);
        await conversation.HandleEventAsync(conversationProgress[1]);
        await conversation.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatStopCommand
            {
                ScopeId = "scope-alpha",
                ConversationActorId = conversationActorId,
                TurnId = turnId,
                StopRequestId = "stop-alpha",
                ClientRequestId = "client-stop-alpha",
                CommandId = "command-stop-alpha",
                CorrelationId = "correlation-stop-alpha",
                ExpectedStateVersion = stopExpectedVersion,
            }));

        var afterFence = await eventStore.GetEventsAsync(conversationActorId);
        afterFence.Count(entry => entry.EventData.Is(
                NyxIdChatOperationProgressedEvent.Descriptor))
            .Should().Be(2, "the stop is the third bounded mailbox item after admission");
        afterFence[^1].EventData.Is(NyxIdChatControlFenceCommittedEvent.Descriptor)
            .Should().BeTrue();
        conversation.State.ControlFence.Kind.Should().Be(NyxIdChatControlKind.Stop);
        conversation.State.ControlFence.Outcome.Should().NotBe(
            NyxIdChatControlOutcome.Rejected,
            "same-turn progress commits must not invalidate an already admitted stop");
        var fencedVersion = afterFence[^1].Version;
        var fencedProgressSequence = conversation.State.ProgressSequence;

        await conversation.HandleEventAsync(conversationProgress[2]);

        (await eventStore.GetEventsAsync(conversationActorId))[^1].Version.Should()
            .Be(fencedVersion, "old progress cannot advance the conversation after its stop fence");
        conversation.State.ProgressSequence.Should().Be(fencedProgressSequence);

        await conversation.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatConversationDeleteCommand
            {
                ScopeId = "scope-alpha",
                ActorId = conversationActorId,
            }));

        var afterDelete = await eventStore.GetEventsAsync(conversationActorId);
        afterDelete[^1].EventData.Is(NyxIdChatConversationHistoryDeletedEvent.Descriptor)
            .Should().BeTrue(
                "cleanup is handled after only three bounded progress deliveries and one stop");
        conversation.State.Deleted.Should().BeTrue();
        conversation.State.DeletedAt.Should().NotBeNull();
        conversation.State.DeletedAt.Should().Be(
            afterDelete[^1].EventData.Unpack<NyxIdChatConversationHistoryDeletedEvent>()
                .DeletedAt);
    }

    [Fact]
    public async Task StreamingBatcher_ShouldSplitUnicodeAtUtf8BoundaryAndPreserveKindOrder()
    {
        var progress = new List<NyxIdChatOperationProgressSignal>();
        var session = new NyxIdChatTransientExecutionSession();
        var largeUnicode = string.Concat(Enumerable.Repeat("\U0001F642", 20_000));
        await using (var batcher = new NyxIdChatStreamingProgressBatcher(
                         CreateKey(),
                         session,
                         (signal, _) =>
                         {
                             progress.Add(signal.Clone());
                             return Task.CompletedTask;
                         },
                         TimeProvider.System))
        {
            await batcher.QueueAsync(
                NyxIdChatOperationProgressSignal.ProgressOneofCase.Text,
                largeUnicode,
                CancellationToken.None);
            await batcher.QueueAsync(
                NyxIdChatOperationProgressSignal.ProgressOneofCase.Reasoning,
                "reason",
                CancellationToken.None);
            await batcher.QueueAsync(
                NyxIdChatOperationProgressSignal.ProgressOneofCase.Text,
                "tail",
                CancellationToken.None);
        }

        progress.Should().HaveCount(2);
        Encoding.UTF8.GetByteCount(progress[0].Text.Delta).Should()
            .BeLessThanOrEqualTo(NyxIdChatTurnOperationExecutor.StreamingProgressBatchBytes);
        progress[1].StreamingBatch.Segments.Sum(segment => Encoding.UTF8.GetByteCount(
                segment.ProgressCase == NyxIdChatStreamingProgressSegment.ProgressOneofCase.Text
                    ? segment.Text.Delta
                    : segment.Reasoning.Delta))
            .Should().BeLessThanOrEqualTo(
                NyxIdChatTurnOperationExecutor.StreamingProgressBatchBytes);
        var segments = new List<(string Kind, string Delta)>
        {
            ("text", progress[0].Text.Delta),
        };
        segments.AddRange(progress[1].StreamingBatch.Segments.Select(segment =>
            segment.ProgressCase == NyxIdChatStreamingProgressSegment.ProgressOneofCase.Text
                ? ("text", segment.Text.Delta)
                : ("reasoning", segment.Reasoning.Delta)));
        string.Concat(segments.Select(static segment => segment.Delta)).Should()
            .Be(largeUnicode + "reason" + "tail");
        segments.Select(static segment => segment.Kind).Should()
            .Equal("text", "text", "reasoning", "text");
    }

    [Theory]
    [InlineData(NyxIdChatTurnIntent.ServiceConnect)]
    [InlineData(NyxIdChatTurnIntent.KeyCreate)]
    [InlineData(NyxIdChatTurnIntent.KeyRotate)]
    [InlineData(NyxIdChatTurnIntent.WorkflowAuthoring)]
    public async Task OperationExecutor_UnprofiledBuiltInIntent_ShouldMaterializeOnlyAdmissionTools(
        NyxIdChatTurnIntent intent)
    {
        IAgentTool[] tools =
        [
            new NamedProfileTool("nyxid_catalog"),
            new NamedProfileTool("nyxid_require_service"),
            new NamedProfileTool("nyxid_services"),
            new NamedProfileTool("nyxid_request_key_create"),
            new NamedProfileTool("nyxid_api_keys"),
            new NamedProfileTool("nyxid_request_key_rotate"),
            new NamedProfileTool("list_external_workflow_capabilities", isReadOnly: true),
            new NamedProfileTool("inspect_external_workflow_capability_readiness", isReadOnly: true),
            new NamedProfileTool("preview_workflow_explicit_requests", isReadOnly: true),
            new NamedProfileTool("github_get_current_user"),
        ];
        var registry = new BuiltInIntentToolSetRegistry(tools);
        var generationExecutor = new CapabilityGeneratingReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
                registry,
                new NoMatchProfileClassifier()));

        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Intent = intent,
                    Request = new ChatRequestEvent
                    {
                        Prompt = intent switch
                        {
                            NyxIdChatTurnIntent.ServiceConnect =>
                                "Connect GitHub and verify the connection",
                            NyxIdChatTurnIntent.KeyCreate =>
                                "Create a least-scope key for one exact service",
                            NyxIdChatTurnIntent.KeyRotate => "Rotate one exact key",
                            _ => "Draft a workflow that calls an external service",
                        },
                        SessionId = "turn-alpha",
                    },
                },
            },
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        registry.RequestedNames.Should().Equal(
            intent == NyxIdChatTurnIntent.WorkflowAuthoring
                ? ToolSetNames.WorkflowExternalCapabilityAuthoring
                : ToolSetNames.NyxIdAssistantAdmission);
        generationExecutor.LastTurnCatalog.Should().NotBeNull();
        string[] expected = intent switch
        {
            NyxIdChatTurnIntent.ServiceConnect =>
                ["nyxid_catalog", "nyxid_require_service"],
            NyxIdChatTurnIntent.KeyCreate =>
                ["nyxid_services", "nyxid_request_key_create"],
            NyxIdChatTurnIntent.KeyRotate => ["nyxid_api_keys", "nyxid_request_key_rotate"],
            _ =>
            [
                "list_external_workflow_capabilities",
                "inspect_external_workflow_capability_readiness",
                "preview_workflow_explicit_requests",
            ],
        };
        generationExecutor.LastTurnCatalog!.FinalAllowedToolNames.Should()
            .BeEquivalentTo(expected);
        generationExecutor.LastTurnCatalog.ExactTools.Keys.Should()
            .BeEquivalentTo(expected);
        generationExecutor.LastTurnCatalog.FinalAllowedToolNames.Should()
            .NotContain("github_get_current_user");
    }

    [Theory]
    [InlineData(NyxIdChatTurnIntent.ServiceConnect, "general_nyxid_assistant")]
    [InlineData(
        NyxIdChatTurnIntent.ServiceConnect,
        NyxIdChatTurnIntentClassifier.ServiceConnectIntentId)]
    [InlineData(
        NyxIdChatTurnIntent.KeyCreate,
        NyxIdChatTurnIntentClassifier.KeyCreateIntentId)]
    [InlineData(
        NyxIdChatTurnIntent.WorkflowAuthoring,
        NyxIdChatTurnIntentClassifier.WorkflowAuthoringIntentId)]
    public async Task OperationExecutor_ProfiledBuiltInIntent_ShouldNarrowToIntentTools(
        NyxIdChatTurnIntent intent,
        string candidateIntentId)
    {
        IAgentTool[] tools =
        [
            new NamedProfileTool("use_skill"),
            new NamedProfileTool("nyxid_services"),
            new NamedProfileTool("nyxid_request_key_create"),
            new NamedProfileTool("nyxid_catalog"),
            new NamedProfileTool("nyxid_require_service"),
            new NamedProfileTool("list_external_workflow_capabilities", isReadOnly: true),
            new NamedProfileTool("inspect_external_workflow_capability_readiness", isReadOnly: true),
            new NamedProfileTool("preview_workflow_explicit_requests", isReadOnly: true),
        ];
        var profile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-mainnet-general",
            ProfileVersion = "profile-v1",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
            MaximumToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames =
                {
                    "use_skill",
                    "nyxid_services",
                    "nyxid_request_key_create",
                    "nyxid_catalog",
                    "nyxid_require_service",
                    "list_external_workflow_capabilities",
                    "inspect_external_workflow_capability_readiness",
                    "preview_workflow_explicit_requests",
                },
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
            CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
            {
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.ProfileVersion,
                PolicyRevision = profile.PolicyRevision,
                IntentId = candidateIntentId,
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
        };
        authority.AuthorityCeilingToolNames.Add(tools.Select(static tool => tool.Name));
        var generationExecutor = new CapabilityGeneratingReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
                new BuiltInIntentToolSetRegistry(tools),
                new NoMatchProfileClassifier()));

        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Intent = intent,
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    Request = new ChatRequestEvent
                    {
                        Prompt = intent switch
                        {
                            NyxIdChatTurnIntent.ServiceConnect =>
                                "Connect GitHub and verify the connection",
                            NyxIdChatTurnIntent.WorkflowAuthoring =>
                                "Draft a workflow that calls an external service",
                            _ => "Create a least-scope key for one exact service",
                        },
                        SessionId = "turn-alpha",
                    },
                },
            },
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        generationExecutor.LastTurnCatalog.Should().NotBeNull();
        string[] expected = intent switch
        {
            NyxIdChatTurnIntent.ServiceConnect =>
                ["nyxid_catalog", "nyxid_require_service"],
            NyxIdChatTurnIntent.WorkflowAuthoring =>
            [
                "list_external_workflow_capabilities",
                "inspect_external_workflow_capability_readiness",
                "preview_workflow_explicit_requests",
            ],
            _ => ["nyxid_services", "nyxid_request_key_create"],
        };
        generationExecutor.LastTurnCatalog!.FinalAllowedToolNames.Should()
            .BeEquivalentTo(expected);
        generationExecutor.LastTurnCatalog.ExactTools.Keys.Should()
            .BeEquivalentTo(expected);
        generationExecutor.LastTurnCatalog.FinalAllowedToolNames.Should().NotContain(
            ["use_skill", "github_get_current_user"]);
    }

    [Theory]
    [InlineData(NyxIdChatTurnIntent.ServiceConnect)]
    [InlineData(NyxIdChatTurnIntent.WorkflowAuthoring)]
    public async Task OperationExecutor_ProfiledBuiltInOverrideWithIncompleteAuthority_ShouldFailClosed(
        NyxIdChatTurnIntent intent)
    {
        IAgentTool[] tools =
        [
            new NamedProfileTool("nyxid_catalog"),
            new NamedProfileTool("nyxid_require_service"),
            new NamedProfileTool("list_external_workflow_capabilities", isReadOnly: true),
            new NamedProfileTool("inspect_external_workflow_capability_readiness", isReadOnly: true),
            new NamedProfileTool("preview_workflow_explicit_requests", isReadOnly: true),
        ];
        var profile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-mainnet-general",
            ProfileVersion = "profile-v1",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
            ActivationMode = AgentProfileActivationMode.Enforced,
        });
        var authority = new AgentProfileTurnAuthorityState
        {
            CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
            {
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.ProfileVersion,
                PolicyRevision = profile.PolicyRevision,
                IntentId = "general_nyxid_assistant",
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
        };
        authority.AuthorityCeilingToolNames.Add(intent == NyxIdChatTurnIntent.ServiceConnect
            ? "nyxid_catalog"
            : "list_external_workflow_capabilities");
        var generationExecutor = new CapabilityGeneratingReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
                new BuiltInIntentToolSetRegistry(tools),
                new NoMatchProfileClassifier()));

        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Intent = intent,
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    Request = new ChatRequestEvent
                    {
                        Prompt = intent == NyxIdChatTurnIntent.ServiceConnect
                            ? "Connect GitHub and verify the connection"
                            : "Draft a workflow that calls an external service",
                        SessionId = "turn-alpha",
                    },
                },
            },
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        generationExecutor.LastTurnCatalog.Should().NotBeNull();
        generationExecutor.LastTurnCatalog!.FinalAllowedToolNames.Should().BeEmpty();
        generationExecutor.LastTurnCatalog.ExactTools.Should().BeEmpty();
    }

    [Fact]
    public async Task OperationExecutor_ServiceConnectIntentWithoutMaterializer_ShouldFailClosed()
    {
        var generationExecutor = new CapabilityGeneratingReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);

        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Intent = NyxIdChatTurnIntent.ServiceConnect,
                    Request = new ChatRequestEvent
                    {
                        Prompt = "Connect GitHub and verify the connection",
                        SessionId = "turn-alpha",
                    },
                },
            },
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        generationExecutor.LastTurnCatalog.Should().NotBeNull();
        generationExecutor.LastTurnCatalog!.FinalAllowedToolNames.Should().BeEmpty();
        generationExecutor.LastTurnCatalog.ExactTools.Should().BeEmpty();
    }

    [Fact]
    public async Task OperationExecutor_ProfiledRetryWithFreshSession_ShouldNotRematerializeCapability()
    {
        var registry = new CountingProfileToolSetRegistry();
        var generationExecutor = new CapabilityGeneratingReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
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
        generationExecutor.LastTurnCatalog.ExactTools.Should().BeEmpty();
    }

    [Fact]
    public async Task OperationExecutor_DurableGenerationTwoApprovalRequired_ShouldPreserveExactAuthority()
    {
        var registry = new CountingProfileToolSetRegistry();
        var generationExecutor = new ApprovalRequiredDurableReplyExecutor();
        var credentialLifecycle = new AcceptingDelegationCredentialLifecycle();
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
                registry,
                new NoMatchProfileClassifier()),
            credentialLifecycle);
        var command = DurableGenerationTwoToolCommand();
        var progress = new List<NyxIdChatOperationProgressSignal>();

        var execution = await executor.ExecuteAsync(
            command,
            new NyxIdChatTransientExecutionSession(),
            (signal, _) =>
            {
                progress.Add(signal.Clone());
                return Task.CompletedTask;
            },
            CancellationToken.None);

        registry.ResolveCount.Should().Be(1);
        generationExecutor.ToolExecutions.Should().Be(1);
        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Tool);
        execution.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.ApprovalRequired);
        execution.Result.Tool.Receipt.ApprovalRequestId.Should().Be("approval-generation-two");
        execution.Result.Tool.Receipt.CallId.Should().Be("call-alpha");
        execution.Result.Tool.Receipt.ToolName.Should().Be("tool-alpha");

        var context = AgentToolExecutionContextMapper.FromPayload(
            generationExecutor.ExecutedStepState!.ToolContext);
        context.Request.RequestId.Should().Be(command.Key.OperationId);
        context.Caller.ScopeId.Should().Be("scope-alpha");
        context.Caller.OwnerSubject.Should().Be("owner-alpha");
        context.Channel.Platform.Should().Be(NyxIdChatServiceDefaults.ServiceId);
        context.Channel.SenderId.Should().BeNull();
        context.Channel.RegistrationScopeId.Should().Be("scope-alpha");
        context.ExecutionOwner.Kind.Should().Be(AgentToolExecutionOwnerKind.Actor);
        context.ExecutionOwner.OwnerId.Should().Be("conversation-alpha");
        credentialLifecycle.DelegationTokens.Should().Equal("fresh-retry-token");
        var start = progress.Should().ContainSingle(signal =>
                signal.ProgressCase ==
                NyxIdChatOperationProgressSignal.ProgressOneofCase.ToolStarted)
            .Which.ToolStarted;
        start.CallId.Should().Be("call-alpha");
        start.Presentation.Kind.Should().Be(ToolPresentationKind.Skill);
        start.Presentation.Skill.SkillName.Should().Be("durable-skill-alpha");
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("channel")]
    [InlineData("execution-owner")]
    public async Task OperationExecutor_DurableGenerationTwoTamperedAuthority_ShouldFailBeforeCatalogOrTool(
        string tamper)
    {
        var registry = new CountingProfileToolSetRegistry();
        var generationExecutor = new ApprovalRequiredDurableReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
                registry,
                new NoMatchProfileClassifier()));
        var command = DurableGenerationTwoToolCommand();
        switch (tamper)
        {
            case "caller":
                command.Tool.ToolContext.Caller.OwnerSubject = "owner-foreign";
                break;
            case "channel":
                command.Tool.ToolContext.Channel.SenderId = "owner-foreign";
                break;
            case "execution-owner":
                command.Tool.ToolContext.ExecutionOwner.OwnerId = "conversation-foreign";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null);
        }

        var execution = await executor.ExecuteAsync(
            command,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        registry.ResolveCount.Should().Be(0);
        generationExecutor.ToolExecutions.Should().Be(0);
        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolAuthorizationMismatchCode);
        execution.Result.Failure.ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.NotStarted);
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
    public async Task OperationExecutor_ToolStartWithoutLlmChunk_ShouldUseFrozenSkillPresentation()
    {
        var generationExecutor = new StreamingCapabilityReplyExecutor(toolName: "use_skill");
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var primingSession = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "load the skill",
                        SessionId = "turn-alpha",
                    },
                },
            },
            primingSession,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var session = new NyxIdChatTransientExecutionSession
        {
            StepState = primingSession.StepState?.Clone(),
            Request = primingSession.Request?.Clone(),
            AuthorizedToolStep = primingSession.AuthorizedToolStep,
            AuthorizedToolCallSafeties = primingSession.AuthorizedToolCallSafeties
                .Select(snapshot => snapshot with
                {
                    Presentation = ToolPresentationDescriptors.Skill(
                        "use_skill",
                        "sample-workflow",
                        "Load the exact workflow skill.",
                        "sample-workflow",
                        "remote"),
                })
                .ToArray(),
            AuthorizationSourceKey = primingSession.AuthorizationSourceKey?.Clone(),
            TurnCatalog = primingSession.TurnCatalog,
        };
        var progress = new List<NyxIdChatOperationProgressSignal>();
        Task ReportProgressAsync(
            NyxIdChatOperationProgressSignal signal,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            progress.Add(signal.Clone());
            return Task.CompletedTask;
        }
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = "call-alpha",
                    ToolName = "use_skill",
                    ArgumentsJson = "{\"value\":1}",
                    MayChangeExternalState = true,
                },
            },
            session,
            ReportProgressAsync,
            CancellationToken.None);

        var start = progress.Should().ContainSingle(signal =>
                signal.ProgressCase ==
                NyxIdChatOperationProgressSignal.ProgressOneofCase.ToolStarted)
            .Which.ToolStarted;
        start.CallId.Should().Be("call-alpha");
        start.ToolName.Should().Be("use_skill");
        start.Presentation.Kind.Should().Be(ToolPresentationKind.Skill);
        start.Presentation.Skill.SkillName.Should().Be("sample-workflow");
        start.Presentation.Skill.Source.Should().Be("remote");
    }

    [Fact]
    public void DurableToolPresentation_ShouldBoundMultibyteFieldsAndRoundTrip()
    {
        var oversizedMultibyte = string.Concat(Enumerable.Repeat("界🙂", 50_000));
        var source = ToolPresentationDescriptors.Skill(
            oversizedMultibyte,
            oversizedMultibyte,
            oversizedMultibyte,
            oversizedMultibyte,
            oversizedMultibyte);
        source.IconUrl = oversizedMultibyte;
        source.UnavailableReason = oversizedMultibyte;

        var bounded = NyxIdChatDurableToolPresentation.Snapshot(
            source,
            oversizedMultibyte);

        bounded.Should().NotBeNull();
        bounded!.CalculateSize().Should().BeLessThanOrEqualTo(
            NyxIdChatDurableToolPresentation.MaxDescriptorBytes);
        Encoding.UTF8.GetByteCount(bounded.InvocationName).Should().BeLessThanOrEqualTo(
            NyxIdChatDurableToolPresentation.MaxNameBytes);
        Encoding.UTF8.GetByteCount(bounded.DisplayName).Should().BeLessThanOrEqualTo(
            NyxIdChatDurableToolPresentation.MaxNameBytes);
        Encoding.UTF8.GetByteCount(bounded.Description).Should().BeLessThanOrEqualTo(
            NyxIdChatDurableToolPresentation.MaxDescriptionBytes);
        Encoding.UTF8.GetByteCount(bounded.Skill.SkillName).Should().BeLessThanOrEqualTo(
            NyxIdChatDurableToolPresentation.MaxNameBytes);
        char.IsHighSurrogate(bounded.InvocationName.LastOrDefault()).Should().BeFalse();
        char.IsHighSurrogate(bounded.Skill.SkillName.LastOrDefault()).Should().BeFalse();

        var roundTripped = ToolPresentationDescriptor.Parser.ParseFrom(bounded.ToByteArray());
        roundTripped.Should().BeEquivalentTo(bounded);
    }

    [Fact]
    public async Task OperationExecutor_WebSearch_ShouldPublishHonestResearchSubsteps()
    {
        var generationExecutor = new StreamingCapabilityReplyExecutor(toolName: "web_search");
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var session = new NyxIdChatTransientExecutionSession();
        var progress = new List<NyxIdChatOperationProgressSignal>();
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "Research Greek dinner options in northern Singapore.",
                        SessionId = "turn-uc2-1",
                    },
                },
            },
            session,
            (signal, _) =>
            {
                progress.Add(signal.Clone());
                return Task.CompletedTask;
            },
            CancellationToken.None);

        var execution = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-uc2-search", "operation-uc2-search", 1),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = "call-alpha",
                    ToolName = "web_search",
                    ArgumentsJson = "{\"value\":1}",
                    MayChangeExternalState = false,
                },
            },
            session,
            (signal, _) =>
            {
                progress.Add(signal.Clone());
                return Task.CompletedTask;
            },
            CancellationToken.None);

        generationExecutor.ToolExecutions.Should().Be(1);
        execution.Result.Tool.Receipt.ToolName.Should().Be("web_search");
        execution.Result.Tool.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        progress.Where(static signal =>
                signal.ProgressCase == NyxIdChatOperationProgressSignal.ProgressOneofCase.Phase)
            .Select(static signal => (
                signal.Phase.SubstepId,
                signal.Phase.Title,
                signal.Phase.Status))
            .Should().Equal(
                ("prepare-operation", "Build search query", NyxIdChatSubstepStatus.Running),
                ("prepare-operation", "Build search query", NyxIdChatSubstepStatus.Done),
                ("execute-operation", "Search current web results", NyxIdChatSubstepStatus.Running),
                ("execute-operation", "Search current web results", NyxIdChatSubstepStatus.Done));
    }

    [Fact]
    public async Task OperationExecutor_ExactToolCommand_ShouldUseTransientCallOnce()
    {
        var generationExecutor = new StreamingCapabilityReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var session = new NyxIdChatTransientExecutionSession();
        await ExecuteInitialToolAuthorizationAsync(executor, session);
        var command = AuthorizedToolCommand("{\"value\":1}");

        var execution = await executor.ExecuteAsync(
            command,
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
    public async Task OperationExecutor_ToolArgumentsMismatch_ShouldFailClosedBeforeEffect()
    {
        var generationExecutor = new StreamingCapabilityReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generationExecutor);
        var session = new NyxIdChatTransientExecutionSession();
        await ExecuteInitialToolAuthorizationAsync(executor, session);
        var command = AuthorizedToolCommand("{\"value\":2}");

        var execution = await executor.ExecuteAsync(
            command,
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
    public async Task OperationExecutor_ActionContinuation_ShouldRematerializeTurnCatalog()
    {
        var registry = new MutableProfileToolSetRegistry("tool-alpha");
        var generationExecutor = new StreamingCapabilityReplyExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
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
                ToolNames = { "tool-alpha", "tool-beta" },
            },
            ActivationMode = AgentProfileActivationMode.Enforced,
        });
        var authority = new AgentProfileTurnAuthorityState
        {
            CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
            {
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.ProfileVersion,
                PolicyRevision = profile.PolicyRevision,
                IntentId = "general_nyxid_assistant",
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
            AuthorityCeilingToolNames = { "tool-alpha", "tool-beta" },
        };
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    Request = new ChatRequestEvent
                    {
                        Prompt = "connect GitHub, then retrieve one assigned issue",
                        SessionId = "turn-alpha",
                    },
                },
            },
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
                    MayChangeExternalState = false,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        registry.SetTools("tool-alpha", "tool-beta");

        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        registry.ResolveCount.Should().Be(2);
        generationExecutor.InitialStateBuilds.Should().Be(1);
        generationExecutor.LlmStepRequests.Should().HaveCount(2);
        generationExecutor.LlmStepRequests[0].TurnCatalog!.FinalAllowedToolNames.Should()
            .Equal("tool-alpha");
        generationExecutor.LlmStepRequests[1].TurnCatalog!.FinalAllowedToolNames.Should()
            .BeEquivalentTo("tool-alpha", "tool-beta");
        generationExecutor.LlmStepRequests[1].StepState.ToolCatalogProof.Should().Be(
            generationExecutor.LlmStepRequests[1].TurnCatalog!.Proof.ToPayload());
        generationExecutor.LlmStepRequests[1].StepState.ToolCatalogPolicyVersion.Should().Be(
            AgentRunReplyGenerationExecutor.ToolCatalogPolicyVersion);
    }

    [Fact]
    public async Task OperationExecutor_UnprofiledVerifiedAuthorizationContinuation_ShouldDiscoverExactCurrentServiceCatalog()
    {
        IAgentTool[] tools =
        [
            new NamedProfileTool("operation-source"),
            new AdmittedProfileTool(
                "operation-alpha-read",
                CreateReadAdmission("us-alpha", "service-alpha", "endpoint-alpha")),
            new AdmittedProfileTool(
                "operation-same-slug-other-instance",
                CreateReadAdmission("us-other", "service-alpha", "endpoint-same-slug")),
            new AdmittedProfileTool(
                "operation-same-instance-other-slug",
                CreateReadAdmission("us-alpha", "service-other", "endpoint-same-instance")),
            new NamedProfileTool("global-fallback"),
        ];
        var registry = new BuiltInIntentToolSetRegistry(tools);
        var generationExecutor = new StreamingCapabilityReplyExecutor(
            toolName: "operation-source");
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
                registry,
                new NoMatchProfileClassifier()));
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "retrieve one assigned item through my connected service",
                        SessionId = "turn-alpha",
                    },
                },
            },
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
                    ToolName = "operation-source",
                    ArgumentsJson = "{\"value\":1}",
                    MayChangeExternalState = false,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        registry.RequestedNames.Clear();

        var execution = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    VerifiedAuthorizationContinuation = VerifiedAuthorization(
                        "us-alpha",
                        "service-alpha"),
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Llm,
            $"failure code was {execution.Result.Failure?.FailureCode}");
        generationExecutor.LlmStepRequests.Should().HaveCount(2);
        var continuationCatalog = generationExecutor.LlmStepRequests[1].TurnCatalog;
        continuationCatalog.Should().NotBeNull();
        continuationCatalog!.FinalAllowedToolNames.Should().Equal("operation-alpha-read");
        continuationCatalog.ExactTools.Keys.Should().Equal("operation-alpha-read");
        registry.RequestedNames.Should().Equal(AgentProfilePolicies.NyxIdChatRouteToolSet);
    }

    [Fact]
    public async Task OperationExecutor_ProfiledVerifiedAuthorizationContinuation_ShouldIgnoreStaleTurnCeilingButHonorMaximumPolicy()
    {
        IAgentTool[] tools =
        [
            new NamedProfileTool("operation-source"),
            new AdmittedProfileTool(
                "operation-alpha-read",
                CreateReadAdmission("us-alpha", "service-alpha", "endpoint-alpha-read")),
            new AdmittedProfileTool(
                "operation-alpha-blocked",
                CreateReadAdmission("us-alpha", "service-alpha", "endpoint-alpha-blocked")),
            new AdmittedProfileTool(
                "operation-same-slug-other-instance",
                CreateReadAdmission("us-other", "service-alpha", "endpoint-same-slug")),
            new AdmittedProfileTool(
                "operation-same-instance-other-slug",
                CreateReadAdmission("us-alpha", "service-other", "endpoint-same-instance")),
            new NamedProfileTool("global-fallback"),
        ];
        var profile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-alpha",
            ProfileVersion = "profile-v1",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
            MaximumToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames =
                {
                    "operation-source",
                    "operation-alpha-read",
                    "operation-same-slug-other-instance",
                    "operation-same-instance-other-slug",
                    "global-fallback",
                },
            },
            ActivationMode = AgentProfileActivationMode.Enforced,
        });
        var authority = new AgentProfileTurnAuthorityState
        {
            CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
            {
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.ProfileVersion,
                PolicyRevision = profile.PolicyRevision,
                IntentId = "general_nyxid_assistant",
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
            AuthorityCeilingToolNames = { "operation-source" },
        };
        var registry = new BuiltInIntentToolSetRegistry(tools);
        var generationExecutor = new StreamingCapabilityReplyExecutor(
            toolName: "operation-source");
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
                registry,
                new NoMatchProfileClassifier()));
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    Request = new ChatRequestEvent
                    {
                        Prompt = "retrieve one assigned item through my connected service",
                        SessionId = "turn-alpha",
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        session.TurnCatalog!.FinalAllowedToolNames.Should().Equal("operation-source");
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = "call-alpha",
                    ToolName = "operation-source",
                    ArgumentsJson = "{\"value\":1}",
                    MayChangeExternalState = false,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        registry.RequestedNames.Clear();

        var execution = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    VerifiedAuthorizationContinuation = VerifiedAuthorization(
                        "us-alpha",
                        "service-alpha"),
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Llm,
            $"failure code was {execution.Result.Failure?.FailureCode}");
        generationExecutor.LlmStepRequests.Should().HaveCount(2);
        var continuationCatalog = generationExecutor.LlmStepRequests[1].TurnCatalog;
        continuationCatalog.Should().NotBeNull();
        continuationCatalog!.FinalAllowedToolNames.Should().Equal("operation-alpha-read");
        continuationCatalog.ExactTools.Keys.Should().Equal("operation-alpha-read");
        registry.RequestedNames.Should().Equal(AgentProfilePolicies.NyxIdChatRouteToolSet);
    }

    [Fact]
    public async Task OperationExecutor_AuthorizationCompletionCommunication_ShouldExposeNoOperationTools()
    {
        IAgentTool[] tools =
        [
            new NamedProfileTool("operation-source"),
            new AdmittedProfileTool(
                "operation-alpha-read",
                CreateReadAdmission("us-alpha", "service-alpha", "endpoint-alpha")),
        ];
        var harness = await PrepareProfiledAuthorizationContinuationAsync(tools);
        harness.Registry.RequestedNames.Clear();
        var continuation = VerifiedAuthorization("us-alpha", "service-alpha");
        continuation.ResumeRequirement =
            NyxIdChatAuthorizationResumeRequirement.CommunicateAuthorizationCompletion;

        var execution = await harness.Executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = harness.Profile,
                    AgentProfileTurnAuthority = harness.Authority,
                    VerifiedAuthorizationContinuation = continuation,
                },
            },
            harness.Session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Llm,
            $"failure code was {execution.Result.Failure?.FailureCode}");
        harness.GenerationExecutor.LlmStepRequests.Should().HaveCount(2);
        harness.GenerationExecutor.LlmStepRequests[1].TurnCatalog.Should().NotBeNull();
        harness.GenerationExecutor.LlmStepRequests[1].TurnCatalog!.FinalAllowedToolNames.Should()
            .BeEmpty();
        harness.GenerationExecutor.LlmStepRequests[1].TurnCatalog!.ExactTools.Should()
            .BeEmpty();
        harness.Registry.RequestedNames.Should().BeEmpty();
    }

    [Fact]
    public async Task OperationExecutor_OriginalRequestContinuationWithoutTypedUserService_ShouldFailBeforeLlm()
    {
        IAgentTool[] tools =
        [
            new NamedProfileTool("operation-source"),
            new AdmittedProfileTool(
                "operation-alpha-read",
                CreateReadAdmission("us-alpha", "service-alpha", "endpoint-alpha")),
        ];
        var harness = await PrepareProfiledAuthorizationContinuationAsync(tools);
        harness.Registry.RequestedNames.Clear();
        var continuation = VerifiedAuthorization("us-alpha", "service-alpha");
        continuation.VerifiedResource = new NyxIdChatSafeResourceRef();

        var execution = await harness.Executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = harness.Profile,
                    AgentProfileTurnAuthority = harness.Authority,
                    VerifiedAuthorizationContinuation = continuation,
                },
            },
            harness.Session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.AuthorizationContinuationCapabilityUnavailableCode);
        harness.GenerationExecutor.LlmStepRequests.Should().ContainSingle();
        harness.Registry.RequestedNames.Should().BeEmpty();
    }

    [Fact]
    public async Task OperationExecutor_ProfiledOriginalRequestContinuationWithoutCommittedIdentity_ShouldFailBeforeLlm()
    {
        IAgentTool[] tools =
        [
            new NamedProfileTool("operation-source"),
            new AdmittedProfileTool(
                "operation-alpha-read",
                CreateReadAdmission("us-alpha", "service-alpha", "endpoint-alpha")),
        ];
        var harness = await PrepareProfiledAuthorizationContinuationAsync(tools);
        harness.Registry.RequestedNames.Clear();
        var authorityWithoutIdentity = harness.Authority.Clone();
        authorityWithoutIdentity.CandidateRoute = null;

        var execution = await harness.Executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = harness.Profile,
                    AgentProfileTurnAuthority = authorityWithoutIdentity,
                    VerifiedAuthorizationContinuation = VerifiedAuthorization(
                        "us-alpha",
                        "service-alpha"),
                },
            },
            harness.Session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.AuthorizationContinuationCapabilityUnavailableCode);
        harness.GenerationExecutor.LlmStepRequests.Should().ContainSingle();
        harness.Registry.RequestedNames.Should().BeEmpty();
    }

    [Fact]
    public async Task OperationExecutor_VerifiedAuthorizationWithUnsupportedResumeRequirement_ShouldFailBeforeLlm()
    {
        IAgentTool[] tools =
        [
            new NamedProfileTool("operation-source"),
            new AdmittedProfileTool(
                "operation-alpha-read",
                CreateReadAdmission("us-alpha", "service-alpha", "endpoint-alpha")),
        ];
        var harness = await PrepareProfiledAuthorizationContinuationAsync(tools);
        harness.Registry.RequestedNames.Clear();
        var continuation = VerifiedAuthorization("us-alpha", "service-alpha");
        continuation.ResumeRequirement = NyxIdChatAuthorizationResumeRequirement.Unspecified;

        var execution = await harness.Executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = harness.Profile,
                    AgentProfileTurnAuthority = harness.Authority,
                    VerifiedAuthorizationContinuation = continuation,
                },
            },
            harness.Session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.AuthorizationContinuationCapabilityUnavailableCode);
        harness.GenerationExecutor.LlmStepRequests.Should().ContainSingle();
        harness.Registry.RequestedNames.Should().BeEmpty();
    }

    [Fact]
    public async Task OperationExecutor_VerifiedAuthorizationContinuation_ShouldUseExactCatalogAndTransientInstruction()
    {
        IAgentTool[] tools =
        [
            new NamedProfileTool("operation-source"),
            new AdmittedProfileTool(
                "operation-alpha-read",
                CreateReadAdmission("us-alpha", "service-alpha", "endpoint-alpha")),
            new AdmittedProfileTool(
                "operation-beta-read",
                CreateReadAdmission("us-beta", "service-beta", "endpoint-beta")),
            new NamedProfileTool("global-fallback"),
        ];
        var registry = new BuiltInIntentToolSetRegistry(tools);
        var generationExecutor = new StreamingCapabilityReplyExecutor(
            toolName: "operation-source");
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
                registry,
                new NoMatchProfileClassifier()));
        var (profile, authority) = ContinuationProfile(tools);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    Request = new ChatRequestEvent
                    {
                        Prompt = "retrieve one assigned item through my connected service",
                        SessionId = "turn-alpha",
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        session.TurnCatalog!.FinalAllowedToolNames.Should().Contain("operation-alpha-read");
        session.TurnCatalog.ExactTools["operation-alpha-read"].Should()
            .BeAssignableTo<IAgentToolOperationAdmissionOwner>();
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = "call-alpha",
                    ToolName = "operation-source",
                    ArgumentsJson = "{\"value\":1}",
                    MayChangeExternalState = false,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var execution = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    VerifiedAuthorizationContinuation = VerifiedAuthorization(
                        "us-alpha",
                        "service-alpha"),
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Llm,
            $"failure code was {execution.Result.Failure?.FailureCode}");
        generationExecutor.LlmStepRequests.Should().HaveCount(2);
        var continuationRequest = generationExecutor.LlmStepRequests[1];
        continuationRequest.TurnCatalog!.FinalAllowedToolNames.Should()
            .Equal("operation-alpha-read");
        continuationRequest.TurnCatalog.ExactTools.Keys.Should()
            .Equal("operation-alpha-read");
        continuationRequest.StepState.Messages.Should().ContainSingle(message =>
            message.Role == "system" &&
            message.Content.Contains("authorization has been verified", StringComparison.Ordinal) &&
            message.Content.Contains("service-alpha", StringComparison.Ordinal) &&
            message.Content.Contains("us-alpha", StringComparison.Ordinal) &&
            message.Content.Contains(
                "continue the original request",
                StringComparison.OrdinalIgnoreCase));
        continuationRequest.StepState.PendingHistoryMessages.Should().NotContain(message =>
            message.Content.Contains("authorization has been verified", StringComparison.Ordinal));
        continuationRequest.StepState.AppendedHistory.Should().NotContain(entry =>
            entry.Content.Contains("authorization has been verified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OperationExecutor_VerifiedAuthorizationWithoutExactOperation_ShouldFailClosedWithoutReadinessFallback()
    {
        IAgentTool[] tools =
        [
            new NamedProfileTool("nyxid_require_service"),
            new NamedProfileTool("global-fallback"),
        ];
        var registry = new BuiltInIntentToolSetRegistry(tools);
        var generationExecutor = new StreamingCapabilityReplyExecutor(
            toolName: "nyxid_require_service");
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
                registry,
                new NoMatchProfileClassifier()));
        var (profile, authority) = ContinuationProfile(tools);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    Request = new ChatRequestEvent
                    {
                        Prompt = "retrieve one assigned item through my connected service",
                        SessionId = "turn-alpha",
                    },
                },
            },
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
                    ToolName = "nyxid_require_service",
                    ArgumentsJson = "{\"value\":1}",
                    MayChangeExternalState = false,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var continuation = VerifiedAuthorization("us-alpha", "service-alpha");
        continuation.AuthorizationReadiness = new NyxIdChatAuthorizationReadinessInput
        {
            ToolName = "nyxid_require_service",
            Params = new NyxIdChatRequireServiceParams
            {
                ServiceSlug = "service-alpha",
                ServiceLabel = "Service Alpha",
                ResourceUri = "https://service.example",
                RequestedScopes = { "items:read" },
            },
        };

        var execution = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    VerifiedAuthorizationContinuation = continuation,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.AuthorizationContinuationCapabilityUnavailableCode);
        generationExecutor.LlmStepRequests.Should().ContainSingle(
            "an exact verified UserService miss must fail before another LLM or readiness call");
    }

    [Fact]
    public async Task OperationExecutor_VerifiedAuthorizationReadiness_ShouldNotReinvokeConnectionTool()
    {
        const string initialArguments =
            "{\"service_slug\":\"service-alpha\",\"requested_scopes\":[\"items:read\"]}";
        var readinessTool = new RecordingReadinessTool();
        var provider = new InitialToolThenTextProvider(
            readinessTool.Name,
            initialArguments);
        var generationExecutor = new AgentRunReplyGenerationExecutor(
            new RecordingDispatchPort(),
            new NyxIdConversationReplyGenerator(
                provider,
                new BuiltInPromptFloorProvider(),
                toolExecutionPort: TestAgentToolExecutionPort.Instance),
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        IAgentTool[] tools = [readinessTool];
        var registry = new BuiltInIntentToolSetRegistry(tools);
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
                registry,
                new NoMatchProfileClassifier()));
        var (profile, authority) = ContinuationProfile(tools);
        var session = new NyxIdChatTransientExecutionSession();

        var initial = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    Request = new ChatRequestEvent
                    {
                        Prompt = "retrieve one assigned item through my connected service",
                        SessionId = "turn-alpha",
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var initialCall = initial.Result.Llm.ToolCalls.Should().ContainSingle().Which;
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = initialCall.CallId,
                    ToolName = initialCall.ToolName,
                    ArgumentsJson = initialCall.ArgumentsJson,
                    MayChangeExternalState = false,
                    Idempotent = true,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var continuation = VerifiedAuthorization("us-alpha", "service-alpha");
        continuation.AuthorizationReadiness = new NyxIdChatAuthorizationReadinessInput
        {
            ToolName = readinessTool.Name,
            Params = new NyxIdChatRequireServiceParams
            {
                ServiceSlug = "service-alpha",
                ServiceLabel = "Service Alpha",
                ResourceUri = "https://service.example",
                RequestedScopes = { "items:read" },
            },
        };

        var resumed = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    VerifiedAuthorizationContinuation = continuation,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        resumed.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        resumed.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.AuthorizationContinuationCapabilityUnavailableCode);
        provider.Requests.Should().ContainSingle(
            "the verified continuation must fail before another model invocation");
        readinessTool.Arguments.Should().ContainSingle()
            .Which.Should().Be(initialArguments);
    }

    [Fact]
    public async Task OperationExecutor_VerifiedAuthorizationExactCatalogMiss_ShouldFailBeforeMiddlewareFiltering()
    {
        const string initialArguments =
            "{\"service_slug\":\"service-alpha\",\"requested_scopes\":[\"items:read\"]}";
        var readinessTool = new RecordingReadinessTool();
        var provider = new InitialToolThenTextProvider(
            readinessTool.Name,
            initialArguments);
        var middleware = new RemoveVerifiedAuthorizationToolsMiddleware();
        var generationExecutor = new AgentRunReplyGenerationExecutor(
            new RecordingDispatchPort(),
            new NyxIdConversationReplyGenerator(
                provider,
                new BuiltInPromptFloorProvider(),
                llmMiddlewares: [middleware],
                toolExecutionPort: TestAgentToolExecutionPort.Instance),
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        IAgentTool[] tools = [readinessTool];
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
                new BuiltInIntentToolSetRegistry(tools),
                new NoMatchProfileClassifier()));
        var (profile, authority) = ContinuationProfile(tools);
        var session = new NyxIdChatTransientExecutionSession();

        var initial = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    Request = new ChatRequestEvent
                    {
                        Prompt = "retrieve one assigned item through my connected service",
                        SessionId = "turn-alpha",
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var initialCall = initial.Result.Llm.ToolCalls.Should().ContainSingle().Which;
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = initialCall.CallId,
                    ToolName = initialCall.ToolName,
                    ArgumentsJson = initialCall.ArgumentsJson,
                    MayChangeExternalState = false,
                    Idempotent = true,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var continuation = VerifiedAuthorization("us-alpha", "service-alpha");
        continuation.AuthorizationReadiness = new NyxIdChatAuthorizationReadinessInput
        {
            ToolName = readinessTool.Name,
            Params = new NyxIdChatRequireServiceParams
            {
                ServiceSlug = "service-alpha",
                ServiceLabel = "Service Alpha",
                ResourceUri = "https://service.example",
                RequestedScopes = { "items:read" },
            },
        };

        var resumed = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    VerifiedAuthorizationContinuation = continuation,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        resumed.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        resumed.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.AuthorizationContinuationCapabilityUnavailableCode);
        provider.Requests.Should().ContainSingle(
            "required continuation capability loss must not fall back to another model choice");
        middleware.RemovedRequests.Should().Be(0,
            "an exact verified UserService miss must fail before middleware tool filtering");
        readinessTool.Arguments.Should().ContainSingle().Which.Should().Be(initialArguments);
    }

    [Fact]
    public async Task OperationExecutor_VerifiedAuthorizationWithoutExactOperation_ShouldFailBeforeLlm()
    {
        IAgentTool[] tools =
        [
            new NamedProfileTool("operation-source"),
            new AdmittedProfileTool(
                "operation-alpha-read",
                CreateReadAdmission("us-alpha", "service-alpha", "endpoint-alpha")),
            new NamedProfileTool("global-fallback"),
        ];
        var generationExecutor = new StreamingCapabilityReplyExecutor(
            toolName: "operation-source");
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
                new BuiltInIntentToolSetRegistry(tools),
                new NoMatchProfileClassifier()));
        var (profile, authority) = ContinuationProfile(tools);
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    Request = new ChatRequestEvent
                    {
                        Prompt = "retrieve one assigned item through my connected service",
                        SessionId = "turn-alpha",
                    },
                },
            },
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
                    ToolName = "operation-source",
                    ArgumentsJson = "{\"value\":1}",
                    MayChangeExternalState = false,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var execution = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    VerifiedAuthorizationContinuation = VerifiedAuthorization(
                        "us-other",
                        "service-other"),
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.AuthorizationContinuationCapabilityUnavailableCode);
        execution.Result.Failure.ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.NotStarted);
        generationExecutor.LlmStepRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task OperationExecutor_ActionPostcondition_ShouldRefreshToolCredentialsWithoutReplacingLlmAuthorization()
    {
        var registry = new CredentialAwareProfileToolSetRegistry();
        var generationExecutor = new StreamingCapabilityReplyExecutor();
        var postconditionPort = new RecordingActionPostconditionPort
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
                        UserServiceId = "service-github",
                    },
                },
            },
        };
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            postconditionPort,
            new AgentTurnToolCatalogMaterializer(
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
                ToolNames = { "tool-alpha", "tool-beta" },
            },
            ActivationMode = AgentProfileActivationMode.Enforced,
        });
        var authority = new AgentProfileTurnAuthorityState
        {
            CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
            {
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.ProfileVersion,
                PolicyRevision = profile.PolicyRevision,
                IntentId = "general_nyxid_assistant",
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
            AuthorityCeilingToolNames = { "tool-alpha", "tool-beta" },
        };
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    Request = new ChatRequestEvent
                    {
                        Prompt = "connect GitHub, then retrieve one assigned issue",
                        SessionId = "turn-alpha",
                        ToolContext = Credentials("old-token"),
                        LlmControl = new LLMControlContextPayload
                        {
                            NyxIdAccessToken = "llm-token",
                        },
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        session.StepState!.LlmControl = session.Request!.LlmControl.Clone();
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = "call-alpha",
                    ToolName = "tool-alpha",
                    ArgumentsJson = "{\"value\":1}",
                    MayChangeExternalState = false,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var postcondition = PostconditionCommand(NyxIdChatActionDisposition.Completed);
        postcondition.ActionPostcondition.ToolContext = Credentials("fresh-token");
        await executor.ExecuteAsync(
            postcondition,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        postconditionPort.TransientToolContexts.Should().ContainSingle()
            .Which.Credentials.NyxIdAccessToken.Should().Be("fresh-token");
        session.Request!.ToolContext.Credentials.NyxIdAccessToken.Should().Be("fresh-token");
        session.Request.ToolContext.Credentials.NyxIdCredentialAuthority.Should().Be(
            AgentToolNyxIdCredentialAuthorityPayload.ToolExecutionContext);
        session.StepState!.ToolContext.Credentials.NyxIdAccessToken.Should().Be("fresh-token");
        session.StepState.ToolContext.Credentials.NyxIdCredentialAuthority.Should().Be(
            AgentToolNyxIdCredentialAuthorityPayload.ToolExecutionContext);
        session.Request.LlmControl.NyxIdAccessToken.Should().Be("llm-token");
        session.StepState.LlmControl.NyxIdAccessToken.Should().Be("llm-token");
        generationExecutor.LlmStepRequests.Should().HaveCount(2);
        generationExecutor.LlmStepRequests[0].TurnCatalog!.FinalAllowedToolNames.Should()
            .Equal("tool-alpha");
        generationExecutor.LlmStepRequests[1].TurnCatalog!.FinalAllowedToolNames.Should()
            .BeEquivalentTo("tool-alpha", "tool-beta");
        generationExecutor.LlmStepRequests[1].Request.LlmControl.NyxIdAccessToken.Should()
            .Be("llm-token");
        generationExecutor.LlmStepRequests[1].StepState.LlmControl.NyxIdAccessToken.Should()
            .Be("llm-token");
    }

    [Fact]
    public async Task OperationExecutor_VerifiedAuthorizationContinuation_ShouldKeepRefreshedToolCredentialSeparateFromLlmAuthorization()
    {
        IAgentTool[] tools =
        [
            new NamedProfileTool("operation-source"),
            new AdmittedProfileTool(
                "operation-alpha-read",
                CreateReadAdmission("service-alpha", "service-alpha", "endpoint-alpha")),
        ];
        var provider = new SequentialToolCallProvider(
            ("operation-source", "{\"value\":1}"),
            ("operation-alpha-read", "{}"));
        var generationExecutor = new AgentRunReplyGenerationExecutor(
            new RecordingDispatchPort(),
            new NyxIdConversationReplyGenerator(
                provider,
                new BuiltInPromptFloorProvider(),
                toolExecutionPort: TestAgentToolExecutionPort.Instance),
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        var postconditionPort = new RecordingActionPostconditionPort
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
            generationExecutor,
            postconditionPort,
            new AgentTurnToolCatalogMaterializer(
                new BuiltInIntentToolSetRegistry(tools),
                new NoMatchProfileClassifier()));
        var (profile, authority) = ContinuationProfile(tools);
        var session = new NyxIdChatTransientExecutionSession();

        var initial = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    Request = new ChatRequestEvent
                    {
                        Prompt = "connect a service, then retrieve one assigned item",
                        SessionId = "turn-alpha",
                        ToolContext = Credentials("old-tool-token"),
                        LlmControl = new LLMControlContextPayload
                        {
                            NyxIdAccessToken = "llm-token",
                        },
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var initialCall = initial.Result.Llm.ToolCalls.Should().ContainSingle().Which;
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = initialCall.CallId,
                    ToolName = initialCall.ToolName,
                    ArgumentsJson = initialCall.ArgumentsJson,
                    MayChangeExternalState = false,
                    Idempotent = true,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var postcondition = PostconditionCommand(NyxIdChatActionDisposition.Completed);
        postcondition.ActionPostcondition.ToolContext = Credentials("fresh-tool-token");
        await executor.ExecuteAsync(
            postcondition,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var resumed = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    VerifiedAuthorizationContinuation = VerifiedAuthorization(
                        "service-alpha",
                        "service-alpha"),
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        resumed.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Llm,
            $"failure code was {resumed.Result.Failure?.FailureCode}");
        resumed.Result.Llm.ToolCalls.Should().ContainSingle()
            .Which.ToolName.Should().Be("operation-alpha-read");
        provider.Requests.Should().HaveCount(2);
        var continuationRequest = provider.Requests[1];
        continuationRequest.LlmControl!.NyxIdAccessToken.Should().Be("llm-token");
        continuationRequest.ToolContext!.Credentials.NyxIdAccessToken.Should()
            .Be("fresh-tool-token");
        continuationRequest.ToolContext.Credentials.NyxIdCredentialAuthority.Should().Be(
            AgentToolNyxIdCredentialAuthority.ToolExecutionContext);
    }

    [Fact]
    public async Task OperationExecutor_ActionPostcondition_ShouldPreserveRefreshedSourceReadableCredentialAcrossDelegationResolution()
    {
        var registry = new DelegatedCredentialAwareProfileToolSetRegistry();
        var generationExecutor = new StreamingCapabilityReplyExecutor();
        var postconditionPort = new RecordingActionPostconditionPort
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
        var credentialLifecycle = new AcceptingDelegationCredentialLifecycle();
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            postconditionPort,
            new AgentTurnToolCatalogMaterializer(
                registry,
                new NoMatchProfileClassifier()),
            credentialLifecycle);
        var profile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-alpha",
            ProfileVersion = "profile-v1",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "tool-alpha", "tool-beta" },
            },
            ActivationMode = AgentProfileActivationMode.Enforced,
        });
        var authority = new AgentProfileTurnAuthorityState
        {
            CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
            {
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.ProfileVersion,
                PolicyRevision = profile.PolicyRevision,
                IntentId = "general_nyxid_assistant",
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
            AuthorityCeilingToolNames = { "tool-alpha", "tool-beta" },
        };
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    Request = new ChatRequestEvent
                    {
                        Prompt = "connect a service, then complete the original request",
                        SessionId = "turn-alpha",
                        ToolContext = ProxyCredentials(
                            "delegation-token",
                            "old-source-token"),
                        LlmControl = new LLMControlContextPayload
                        {
                            NyxIdAccessToken = "delegation-token",
                        },
                    },
                },
            },
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
                    MayChangeExternalState = false,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var postcondition = PostconditionCommand(NyxIdChatActionDisposition.Completed);
        postcondition.ActionPostcondition.ToolContext = ProxyCredentials(
            "delegation-token",
            "fresh-source-token");
        await executor.ExecuteAsync(
            postcondition,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey("step-action-continuation", "operation-action-continuation", 1),
                Llm = new NyxIdChatLLMOperationInput
                {
                    ContinueSession = true,
                    RematerializeTurnCatalog = true,
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        credentialLifecycle.DelegationTokens.Should().Equal(
            "delegation-token",
            "delegation-token",
            "delegation-token");
        postconditionPort.TransientToolContexts.Should().ContainSingle()
            .Which.Credentials.SourceReadableNyxIdAccessToken.Should()
            .Be("fresh-source-token");
        session.Request!.ToolContext.Credentials.SourceReadableNyxIdAccessToken.Should()
            .Be("fresh-source-token");
        session.StepState!.ToolContext.Credentials.SourceReadableNyxIdAccessToken.Should()
            .Be("fresh-source-token");
        session.Request.LlmControl.NyxIdAccessToken.Should().Be("delegation-token");
        session.StepState.LlmControl.NyxIdAccessToken.Should().Be("delegation-token");
        generationExecutor.LlmStepRequests.Should().HaveCount(2);
        generationExecutor.LlmStepRequests[1].TurnCatalog!.FinalAllowedToolNames.Should()
            .BeEquivalentTo("tool-alpha", "tool-beta");
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
        var session = new NyxIdChatTransientExecutionSession
        {
            Request = new NeedsLlmReplyEvent
            {
                ToolContext = new AgentToolExecutionContextPayload
                {
                    Credentials = new AgentToolCredentialsPayload
                    {
                        NyxIdAccessToken = "transient-secret",
                        NyxIdCredentialKind =
                            AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
                    },
                },
            },
        };

        var execution = await executor.ExecuteAsync(
            command,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        port.Inputs.Should().ContainSingle().Which.Should().BeEquivalentTo(
            command.ActionPostcondition);
        port.TransientToolContexts.Should().ContainSingle()
            .Which.Credentials.NyxIdAccessToken.Should().Be("transient-secret");
        command.ToString().Should().NotContain("transient-secret");
        execution.Result.ToString().Should().NotContain("transient-secret");
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

    private static NyxIdChatOperationResultSignal CreatePostconditionResult(
        NyxIdChatOperationKey key,
        NyxIdChatOperationResultSignal.ResultOneofCase resultCase =
            NyxIdChatOperationResultSignal.ResultOneofCase.ActionPostcondition)
    {
        var result = new NyxIdChatOperationResultSignal { Key = key.Clone() };
        switch (resultCase)
        {
            case NyxIdChatOperationResultSignal.ResultOneofCase.ActionPostcondition:
                result.ActionPostcondition = new NyxIdChatActionPostconditionResult
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
                };
                break;
            case NyxIdChatOperationResultSignal.ResultOneofCase.ToolVerification:
                result.ToolVerification = new NyxIdChatToolVerificationResult
                {
                    EffectStepId = "step-effect-alpha",
                    Disposition = NyxIdChatToolVerificationDisposition.NotApplied,
                };
                break;
            case NyxIdChatOperationResultSignal.ResultOneofCase.Failure:
                result.Failure = new NyxIdChatOperationFailure
                {
                    FailureCode = "POSTCONDITION_FAILED",
                    SafeMessage = "The postcondition failed.",
                    ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(resultCase), resultCase, null);
        }

        return result;
    }

    private static NyxIdChatConversationGAgentState CreateFencedPostconditionConversationState(
        NyxIdChatOperationKey key)
    {
        var committedAt = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 8, 10, 4, 59, 0, TimeSpan.Zero));
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = key.ConversationActorId,
            ScopeId = "scope-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = key.TurnId,
                TaskId = key.TaskId,
                Status = NyxIdChatTurnStatus.Stopped,
                TerminalAt = committedAt.Clone(),
            },
            ActiveTask = new NyxIdChatTaskState
            {
                ActorId = key.ConversationActorId,
                TurnId = key.TurnId,
                TaskId = key.TaskId,
                PlanId = "plan-fenced-postcondition",
                Status = NyxIdChatTaskStatus.Stopped,
            },
            ControlFence = new NyxIdChatControlFenceState
            {
                Kind = NyxIdChatControlKind.Stop,
                RequestId = "stop-fenced-postcondition",
                ClientRequestId = "client-stop-fenced-postcondition",
                TurnId = key.TurnId,
                TaskId = key.TaskId,
                StepId = key.StepId,
                OperationGeneration = key.OperationGeneration,
                Outcome = NyxIdChatControlOutcome.Uncancellable,
                CommittedAt = committedAt.Clone(),
            },
            ProgressSequence = 7,
            UpdatedAt = committedAt.Clone(),
        };
        state.LatestTurn = state.ActiveTurn.Clone();
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = key.StepId,
            Order = 1,
            Kind = NyxIdChatStepKind.Postcondition,
            Status = NyxIdChatStepStatus.Cancelled,
            Required = true,
            Operation = new NyxIdChatOperationState
            {
                Key = key.Clone(),
                Kind = NyxIdChatStepKind.Postcondition,
                Phase = NyxIdChatOperationPhase.Dispatched,
                DispatchedAt = committedAt.Clone(),
            },
        });
        return state;
    }

    private static NyxIdChatTurnGAgent CreateAgent(
        ServiceProvider services,
        INyxIdChatTurnOperationDispatchPort operationDispatch,
        IActorDispatchPort dispatch,
        NyxIdToolOptions? options = null,
        TimeProvider? timeProvider = null,
        ISecretVault? secretVault = null,
        string actorId = "turn-actor-alpha")
    {
        var agent = new NyxIdChatTurnGAgent(
            operationDispatch,
            dispatch,
            options ?? new NyxIdToolOptions(),
            timeProvider ?? new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
            secretVault)
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatTurnGAgentState>>(),
        };
        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [actorId]);
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

    private static Task AcknowledgePendingResultAsync(NyxIdChatTurnGAgent agent)
    {
        var result = agent.State.PendingResult ?? throw new InvalidOperationException(
            "The turn must retain a pending postcondition result before acknowledgement.");
        return agent.HandleOperationResultAcknowledgedAsync(
            new NyxIdChatTurnOperationResultAcknowledgedSignal
            {
                Key = result.Key.Clone(),
                ResultSha256 = ByteString.CopyFrom(SHA256.HashData(result.ToByteArray())),
            });
    }

    private static async Task ExecuteInitialToolAuthorizationAsync(
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
                        Prompt = "authorize then run",
                        SessionId = "turn-alpha",
                        ToolContext = FreshToolContext("initial-tool-token"),
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
    }

    private static NyxIdChatOperationDispatchCommand AuthorizedToolCommand(
        string argumentsJson) => new()
    {
        Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
        Tool = new NyxIdChatToolOperationInput
        {
            CallId = "call-alpha",
            ToolName = "tool-alpha",
            ArgumentsJson = argumentsJson,
            ToolContext = FreshToolContext("fresh-tool-token"),
            MayChangeExternalState = true,
            IdempotencyKey = "operation-tool-alpha",
        },
    };

    private static NyxIdChatOperationDispatchCommand DurableGenerationTwoToolCommand(
        NyxIdChatOperationKey? retryAuthorizationSourceKey = null)
    {
        retryAuthorizationSourceKey ??= CreateKey(
            "step-tool-alpha",
            "operation-tool-source-alpha",
            1);
        var arguments = JsonParser.Default.Parse<Struct>("{\"value\":1}");
        var operationAdmission = ExactWriteAdmission();
        operationAdmission.DurableAuthorization = new AgentToolDurableAuthorizationSnapshotPayload
        {
            HasRequiresApproval = true,
            RequiresApproval = true,
            IsReadOnly = false,
            IsDestructive = false,
            SideEffectKind = "tool-alpha.update",
            ToolDefinitionFingerprint = "sha256:tool-alpha-v1",
        };
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
        return new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey("step-tool-alpha", "operation-tool-retry-alpha", 2),
            Tool = new NyxIdChatToolOperationInput
            {
                CallId = "call-alpha",
                ToolName = "tool-alpha",
                ArgumentsJson = JsonFormatter.Default.Format(arguments),
                ToolContext = DurableToolContext("retry-alpha", "fresh-retry-token"),
                MayChangeExternalState = true,
                IdempotencyKey = "operation-tool-retry-alpha",
                OperationAdmission = operationAdmission,
                AgentProfile = profile,
                AgentProfileTurnAuthority = new AgentProfileTurnAuthorityState
                {
                    ReconciliationKey = new AgentProfileTurnReconciliationKey
                    {
                        SessionId = "turn-alpha",
                        Attempt = 1,
                    },
                    AuthorityKind = AgentProfileTurnAuthorityKind.Recovery,
                    AuthorityCeilingToolNames = { "tool-alpha" },
                },
                RematerializeDurableAuthorization = true,
                RetryAuthorizationSourceKey = retryAuthorizationSourceKey?.Clone(),
                Presentation = ToolPresentationDescriptors.Skill(
                    "tool-alpha",
                    "Durable skill alpha",
                    "Retry the exact skill operation.",
                    "durable-skill-alpha",
                    "remote"),
            },
        };
    }

    private static NyxIdChatOperationDispatchCommand GenerationTwoExactApprovalContinuation()
    {
        var admission = ExactWriteAdmission();
        var authority = ExactApprovalAuthority().Clone();
        authority.RequestId = "approval-generation-one";
        authority.UserServiceId = admission.ServiceInstanceId;
        authority.EndpointId = admission.PublishedEndpoint.EndpointId;
        authority.CatalogDigest = admission.CatalogDigest;
        authority.EndpointContractDigest = admission.ContractDigest;
        authority.OperationId = "operation-tool-source-alpha";
        authority.OperationGeneration = 1;
        authority.IdempotencyKey = "operation-tool-source-alpha";
        return new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey("step-tool-alpha", "operation-tool-approval-alpha", 2),
            ToolApprovalContinuation = new NyxIdChatToolApprovalContinuationInput
            {
                ApprovalRequestId = authority.RequestId,
                Approved = true,
                ToolContext = DurableToolContext(authority.RequestId, "fresh-approval-token"),
                MayChangeExternalState = true,
                IdempotencyKey = "operation-tool-approval-alpha",
                OperationAdmission = admission,
                ExactServiceApproval = authority,
                ToolCallId = "call-alpha",
                ToolName = "tool-alpha",
            },
        };
    }

    private static Task AppendDeliveredExactServiceTurnAsync(
        InMemoryEventStoreForTests eventStore,
        NyxIdChatOperationKey sourceKey,
        AgentToolOperationAdmissionPayload operationAdmission,
        AgentToolReceiptStatus receiptStatus,
        string terminalCode = "")
    {
        var recordedAt = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 8, 12, 1, 0, 0, TimeSpan.Zero));
        return eventStore.AppendAsync(
            "turn-actor-alpha",
            [
                new StateEvent
                {
                    EventId = "legacy-exact-service-admitted",
                    AgentId = "turn-actor-alpha",
                    Version = 1,
                    Timestamp = recordedAt.Clone(),
                    EventType = NyxIdChatTurnOperationAdmittedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new NyxIdChatTurnOperationAdmittedEvent
                    {
                        Key = sourceKey.Clone(),
                        OperationKind = NyxIdChatStepKind.Tool,
                        AdmittedAt = recordedAt.Clone(),
                        MayChangeExternalState = true,
                        EffectDispatchWaterline = NyxIdChatEffectEvidence.NotStarted,
                        IdempotencyKey = sourceKey.OperationId,
                        OperationAdmission = operationAdmission.Clone(),
                        ExactServiceRecoveryStage =
                            NyxIdChatExactServiceRecoveryStage.Create,
                    }),
                },
                new StateEvent
                {
                    EventId = "legacy-exact-service-completed",
                    AgentId = "turn-actor-alpha",
                    Version = 2,
                    Timestamp = recordedAt.Clone(),
                    EventType = NyxIdChatTurnOperationCompletedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new NyxIdChatTurnOperationCompletedEvent
                    {
                        Key = sourceKey.Clone(),
                        Phase = NyxIdChatOperationPhase.Succeeded,
                        TerminalCode = terminalCode,
                        ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
                        CompletedAt = recordedAt.Clone(),
                        ToolReceiptStatus = receiptStatus,
                    }),
                },
                new StateEvent
                {
                    EventId = "legacy-exact-service-delivered",
                    AgentId = "turn-actor-alpha",
                    Version = 3,
                    Timestamp = recordedAt.Clone(),
                    EventType = NyxIdChatTurnOperationDeliveredEvent.Descriptor.FullName,
                    EventData = Any.Pack(new NyxIdChatTurnOperationDeliveredEvent
                    {
                        Key = sourceKey.Clone(),
                        DeliveredAt = recordedAt.Clone(),
                    }),
                },
            ],
            expectedVersion: 0);
    }

    private static AgentToolExecutionContextPayload FreshToolContext(string token) => new()
    {
        Credentials = new AgentToolCredentialsPayload
        {
            NyxIdAccessToken = token,
            NyxIdCredentialKind =
                AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
        },
    };

    private static AgentToolExecutionContextPayload DurableToolContext(string requestId, string token) =>
        (AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(requestId, null),
            Credentials = new AgentToolCredentials(
                token,
                null,
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation),
            Caller = new AgentToolCallerContext(
                "scope-alpha",
                "owner-alpha",
                requestId,
                "scope-alpha"),
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                null,
                "scope-alpha",
                null,
                null),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                string.Empty,
                "owner-alpha",
                "proxy"),
            ExecutionOwner = AgentToolExecutionOwners.Actor("conversation-alpha"),
        }).ToPayload();

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

    private static ServiceProvider BuildActorCompositionServices(IEventStore eventStore) =>
        new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(new NoopRuntimeCallbackScheduler())
            .AddSingleton<IChatHistoryCommandPort>(new ImmediateChatHistoryCommandPort())
            .AddSingleton<IGAgentActorRegistryCommandPort>(new ImmediateActorRegistryCommandPort())
            .AddTransient(
                typeof(IEventSourcingBehaviorFactory<>),
                typeof(DefaultEventSourcingBehaviorFactory<>))
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

    private sealed class ReplacementAwareBlockingExecutor : INyxIdChatTurnOperationExecutor
    {
        private readonly TaskCompletionSource<NyxIdChatTurnOperationExecution> _firstCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<NyxIdChatTurnOperationExecution> _secondCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<NyxIdChatTransientExecutionSession> Sessions { get; } = [];

        public async Task<NyxIdChatTurnOperationExecution> ExecuteAsync(
            NyxIdChatOperationDispatchCommand command,
            NyxIdChatTransientExecutionSession session,
            Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
            CancellationToken ct)
        {
            _ = reportProgressAsync;
            Sessions.Add(session);
            if (command.Key.OperationId == "operation-first")
            {
                FirstStarted.TrySetResult();
                return await _firstCompletion.Task.WaitAsync(ct);
            }

            SecondStarted.TrySetResult();
            try
            {
                return await _secondCompletion.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                SecondCancelled.TrySetResult();
                throw;
            }
        }

        public void CompleteFirst(NyxIdChatOperationKey key) =>
            _firstCompletion.TrySetResult(new NyxIdChatTurnOperationExecution(
                new NyxIdChatOperationResultSignal
                {
                    Key = key.Clone(),
                    Llm = new NyxIdChatLLMOperationResult { Content = "first" },
                }));
    }

    private sealed class RecordingOperationDispatchPort(
        INyxIdChatTurnOperationExecutor executor)
        : INyxIdChatTurnOperationDispatchPort,
            INyxIdChatTurnOperationDispatchSession
    {
        private readonly Queue<IMessage> _pendingSignals = new();

        public AgentToolExecutionContextPayload? CapturedToolContext { get; init; }

        public AgentToolExecutionContextPayload? CaptureToolContext() =>
            CapturedToolContext?.Clone();

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
            if (command.Tool?.CanaryEffectFault is { } directive &&
                NyxIdChatTurnOperationDispatchPort.IsCanaryEffectFaultBoundaryResult(
                    command,
                    execution.Result))
            {
                _pendingSignals.Enqueue(new NyxIdChatCanaryEffectFaultTriggeredSignal
                {
                    ArmId = directive.ArmId,
                    DeniedResult = execution.Result.Clone(),
                    TriggeredAt = Timestamp.FromDateTimeOffset(
                        new DateTimeOffset(2026, 7, 24, 8, 0, 2, TimeSpan.Zero)),
                });
                return;
            }

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

        public Task CancelExecutionAsync(NyxIdChatOperationKey key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

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
        public List<AgentToolExecutionContextPayload> TransientToolContexts { get; } = [];

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
            AgentToolExecutionContextPayload? transientToolContext = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Inputs.Add(input.Clone());
            if (transientToolContext is not null)
                TransientToolContexts.Add(transientToolContext.Clone());
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

        public AgentTurnToolCatalog? LastTurnCatalog { get; private set; }

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

    private sealed class ApprovalRequiredDurableReplyExecutor
        : IAgentRunReplyGenerationExecutorPort
    {
        public int ToolExecutions { get; private set; }

        public AgentRunReplyStepState? ExecutedStepState { get; private set; }

        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            request.AllowDurableToolAuthorization.Should().BeTrue();
            authorizedToolStep.Should().BeNull();
            ToolExecutions++;
            ExecutedStepState = request.StepState.Clone();
            var call = request.StepState.PendingToolCalls.Should().ContainSingle().Which;
            var result = new AgentRunToolStepResult
            {
                AdvanceRound = true,
                AuthorizationOutcome = AgentRunToolAuthorizationOutcome.DurableMatched,
            };
            result.ResultMessages.Add(new AgentRunChatMessage
            {
                Role = "tool",
                ToolCallId = call.Id,
                Content = "{\"error\":\"approval_required\"}",
            });
            result.ToolReceipts.Add(new AgentToolReceipt
            {
                CallId = call.Id,
                ToolName = call.Name,
                Status = AgentToolReceiptStatus.ApprovalRequired,
                ApprovalRequestId = "approval-generation-two",
                NyxIdApprovalDecisionMode = NyxIdApprovalDecisionMode.PerRequest,
                SubjectKind = "user",
                SubjectId = "owner-alpha",
            });
            return Task.FromResult(new AgentRunNextToolStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                ToolStepResult = result,
            });
        }
    }

    private sealed class TinyDeltaReplyExecutor(MutableTimeProvider clock)
        : IAgentRunReplyGenerationExecutorPort
    {
        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentRunReplyStepState
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                NextStepIndex = 1,
                MaxToolRounds = 1,
            });
        }

        public async Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct)
        {
            for (var index = 0; index < 1_000; index++)
            {
                if (index == 500)
                    clock.Advance(NyxIdChatTurnOperationExecutor.StreamingProgressBatchInterval);
                await request.ReportChunkAsync!(new LLMStreamChunk { DeltaContent = "x" }, ct);
            }

            var content = new string('x', 1_000);
            return new AgentRunLlmStepExecution(
                new AgentRunNextLlmStepRequestedEvent
                {
                    RunId = request.RunId,
                    CorrelationId = request.Request.CorrelationId,
                    TargetActorId = request.Request.TargetActorId,
                    Attempt = request.Attempt,
                    StepIndex = request.StepIndex + 1,
                    Request = request.Request.Clone(),
                    LlmStepResult = new AgentRunLlmStepResult
                    {
                        Content = content,
                        AccumulatedText = content,
                        FinishReason = "stop",
                        HasStreamedTextContent = true,
                    },
                },
                AuthorizedToolStep: null);
        }

        public Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
            CancellationToken ct) => throw new NotSupportedException();
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

    private sealed class MutableProfileToolSetRegistry(params string[] toolNames)
        : IToolSetRegistry
    {
        private IReadOnlyList<IAgentTool> _tools = toolNames
            .Select(static name => (IAgentTool)new NamedProfileTool(name))
            .ToArray();

        public int ResolveCount { get; private set; }

        public IReadOnlyList<string> GetRegisteredNames() => ["profile.route"];

        public ToolSetResolveResult Resolve(string? name)
        {
            ResolveCount++;
            return string.Equals(name, "profile.route", StringComparison.Ordinal)
                ? ToolSetResolveResult.Success(
                    "profile.route",
                    [new ToolListSource(_tools)])
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    name ?? string.Empty,
                    "missing",
                    GetRegisteredNames()));
        }

        public void SetTools(params string[] names)
        {
            _tools = names
                .Select(static name => (IAgentTool)new NamedProfileTool(name))
                .ToArray();
        }
    }

    private sealed class CredentialAwareProfileToolSetRegistry : IToolSetRegistry
    {
        public IReadOnlyList<string> GetRegisteredNames() => ["profile.route"];

        public ToolSetResolveResult Resolve(string? name) =>
            string.Equals(name, "profile.route", StringComparison.Ordinal)
                ? ToolSetResolveResult.Success(
                    "profile.route",
                    [new CredentialAwareToolSource()])
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    name ?? string.Empty,
                    "missing",
                    GetRegisteredNames()));
    }

    private sealed class DelegatedCredentialAwareProfileToolSetRegistry : IToolSetRegistry
    {
        public IReadOnlyList<string> GetRegisteredNames() => ["profile.route"];

        public ToolSetResolveResult Resolve(string? name) =>
            string.Equals(name, "profile.route", StringComparison.Ordinal)
                ? ToolSetResolveResult.Success(
                    "profile.route",
                    [new DelegatedCredentialAwareToolSource()])
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    name ?? string.Empty,
                    "missing",
                    GetRegisteredNames()));
    }

    private sealed class DelegatedCredentialAwareToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var sourceReadableToken = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
                AgentToolRequestContext.Current?.Credentials);
            IReadOnlyList<IAgentTool> tools = sourceReadableToken == "fresh-source-token"
                ? [new NamedProfileTool("tool-alpha"), new NamedProfileTool("tool-beta")]
                : [new NamedProfileTool("tool-alpha")];
            return Task.FromResult(tools);
        }
    }

    private sealed class CredentialAwareToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<IAgentTool> tools =
                AgentToolRequestContext.Current?.Credentials.NyxIdAccessToken == "fresh-token"
                    ? [new NamedProfileTool("tool-alpha"), new NamedProfileTool("tool-beta")]
                    : [new NamedProfileTool("tool-alpha")];
            return Task.FromResult(tools);
        }
    }

    private static AgentToolExecutionContextPayload Credentials(string accessToken) => new()
    {
        Credentials = new AgentToolCredentialsPayload
        {
            NyxIdAccessToken = accessToken,
            NyxIdCredentialKind =
                AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
        },
    };

    private static AgentToolExecutionContextPayload ProxyCredentials(
        string accessToken,
        string sourceReadableAccessToken) => new()
    {
        Credentials = new AgentToolCredentialsPayload
        {
            NyxIdAccessToken = accessToken,
            NyxIdCredentialKind = AgentToolNyxIdCredentialKindPayload.ProxyDelegation,
            SourceReadableNyxIdAccessToken = sourceReadableAccessToken,
        },
    };

    private sealed class BuiltInIntentToolSetRegistry(IReadOnlyList<IAgentTool> tools)
        : IToolSetRegistry
    {
        public List<string> RequestedNames { get; } = [];

        public IReadOnlyList<string> GetRegisteredNames() =>
        [
            AgentProfilePolicies.NyxIdChatRouteToolSet,
            ToolSetNames.NyxIdAssistantAdmission,
            ToolSetNames.WorkflowExternalCapabilityAuthoring,
        ];

        public ToolSetResolveResult Resolve(string? name)
        {
            RequestedNames.Add(name ?? string.Empty);
            return GetRegisteredNames().Contains(name, StringComparer.Ordinal)
                ? ToolSetResolveResult.Success(
                    name!,
                    [new ToolListSource(tools)])
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    name ?? string.Empty,
                    "missing",
                    GetRegisteredNames()));
        }
    }

    private sealed class SingleToolSource(IAgentTool tool) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
    }

    private sealed class ToolListSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(tools);
        }
    }

    private sealed class ProfileTool : IAgentTool
    {
        public string Name => "tool-alpha";
        public string Description => Name;
        public string ParametersSchema => "{}";
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private sealed class NamedProfileTool(string name, bool isReadOnly = false) : IAgentTool
    {
        public string Name => name;
        public string Description => Name;
        public string ParametersSchema => "{}";
        public bool IsReadOnly => isReadOnly;
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private sealed class RecordingReadinessTool : IAgentTool
    {
        public string Name => "nyxid_require_service";
        public string Description => Name;
        public string ParametersSchema =>
            "{\"type\":\"object\",\"required\":[\"service_slug\",\"requested_scopes\"]}";
        public bool IsReadOnly => true;
        public List<string> Arguments { get; } = [];

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Arguments.Add(argumentsJson);
            return Task.FromResult("{\"blocked\":false,\"readiness_status\":\"Ready\"}");
        }
    }

    private sealed class RemoveVerifiedAuthorizationToolsMiddleware : ILLMCallMiddleware
    {
        public int RemovedRequests { get; private set; }

        public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
        {
            if (context.Request.Messages.Any(message =>
                    string.Equals(message.Role, "system", StringComparison.Ordinal) &&
                    message.Content?.Contains(
                        "NyxID authorization has been verified",
                        StringComparison.Ordinal) == true))
            {
                RemovedRequests++;
                var request = context.Request;
                context.Request = new LLMRequest
                {
                    Messages = request.Messages,
                    RequestId = request.RequestId,
                    Metadata = request.Metadata,
                    CallerContext = request.CallerContext,
                    ToolContext = request.ToolContext,
                    RoutingContext = request.RoutingContext,
                    LlmControl = request.LlmControl,
                    Tools = [],
                    Model = request.Model,
                    Temperature = request.Temperature,
                    MaxTokens = request.MaxTokens,
                    AllowMultipleToolCalls = request.AllowMultipleToolCalls,
                    ResponseFormat = request.ResponseFormat,
                };
            }

            await next();
        }
    }

    private sealed class InitialToolThenTextProvider(
        string toolName,
        string initialArguments) : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "initial-tool-then-text";
        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call-initial-readiness",
                        Name = toolName,
                        ArgumentsJson = initialArguments,
                    },
                };
            }
            else
            {
                yield return new LLMStreamChunk
                {
                    DeltaContent = "Authorization is complete.",
                };
            }

            await Task.Yield();
        }
    }

    private sealed class SequentialToolCallProvider(
        params (string ToolName, string ArgumentsJson)[] calls) :
        ILLMProviderFactory,
        ILLMProvider
    {
        public string Name => "sequential-tool-calls";
        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            var index = Requests.Count - 1;
            if (index < calls.Length)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = $"call-{index + 1}",
                        Name = calls[index].ToolName,
                        ArgumentsJson = calls[index].ArgumentsJson,
                    },
                };
            }
            else
            {
                yield return new LLMStreamChunk
                {
                    DeltaContent = "complete",
                };
            }

            await Task.Yield();
        }
    }

    private sealed class AdmittedProfileTool(
        string name,
        AgentToolOperationAdmission operationAdmission) :
        IAgentTool,
        IAgentToolOperationAdmissionOwner
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public AgentToolOperationAdmission OperationAdmission { get; } = operationAdmission;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private static AgentToolOperationAdmission CreateReadAdmission(
        string userServiceId,
        string serviceSlug,
        string endpointId) =>
        new(
            userServiceId,
            serviceSlug,
            new AgentToolOperationIdentity.PublishedEndpoint(endpointId),
            AgentToolOperationAuthorizationBasis.PublishedContract,
            "GET",
            "/items",
            "contract-digest-alpha",
            [],
            null,
            AgentToolOperationResponsePolicy.TextOnly,
            new AgentToolOperationExecutionPolicy(
                AgentToolOperationRisk.ReadOnly,
                AgentToolOperationApproval.None,
                AgentToolOperationEnforcementOwner.Aevatar,
                [AgentToolOperationExecutionMode.Interactive]),
            "catalog-digest-alpha");

    private static NyxIdChatVerifiedAuthorizationContinuation VerifiedAuthorization(
        string userServiceId,
        string serviceSlug) =>
        new()
        {
            ActionRequestId = "action-alpha",
            OriginTurnId = "turn-alpha",
            SourceToolStepId = "step-tool-alpha",
            PostconditionStepId = "step-postcondition-alpha",
            VerifiedResource = new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef
                {
                    UserServiceId = userServiceId,
                },
            },
            ServiceSlug = serviceSlug,
            VerifiedAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.Zero)),
            ResumeRequirement =
                NyxIdChatAuthorizationResumeRequirement.CompleteOriginalServiceRequest,
        };

    private static async Task<(
        NyxIdChatTurnOperationExecutor Executor,
        StreamingCapabilityReplyExecutor GenerationExecutor,
        NyxIdChatTransientExecutionSession Session,
        AgentProfileSnapshot Profile,
        AgentProfileTurnAuthorityState Authority,
        BuiltInIntentToolSetRegistry Registry)> PrepareProfiledAuthorizationContinuationAsync(
        IReadOnlyList<IAgentTool> tools)
    {
        var (profile, authority) = ContinuationProfile(tools);
        var registry = new BuiltInIntentToolSetRegistry(tools);
        var generationExecutor = new StreamingCapabilityReplyExecutor(
            toolName: "operation-source");
        var executor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            new AgentTurnToolCatalogMaterializer(
                registry,
                new NoMatchProfileClassifier()));
        var session = new NyxIdChatTransientExecutionSession();
        await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    AgentProfile = profile,
                    AgentProfileTurnAuthority = authority,
                    Request = new ChatRequestEvent
                    {
                        Prompt = "retrieve one assigned item through my connected service",
                        SessionId = "turn-alpha",
                    },
                },
            },
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
                    ToolName = "operation-source",
                    ArgumentsJson = "{\"value\":1}",
                    MayChangeExternalState = false,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        return (executor, generationExecutor, session, profile, authority, registry);
    }

    private static (AgentProfileSnapshot Profile, AgentProfileTurnAuthorityState Authority)
        ContinuationProfile(IReadOnlyList<IAgentTool> tools)
    {
        var profile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-alpha",
            ProfileVersion = "profile-v1",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
            MaximumToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { tools.Select(static tool => tool.Name) },
            },
            ActivationMode = AgentProfileActivationMode.Enforced,
        });
        var authority = new AgentProfileTurnAuthorityState
        {
            CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
            {
                ProfileId = profile.ProfileId,
                ProfileVersion = profile.ProfileVersion,
                PolicyRevision = profile.PolicyRevision,
                IntentId = "general_nyxid_assistant",
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
        };
        authority.AuthorityCeilingToolNames.Add(tools.Select(static tool => tool.Name));
        return (profile, authority);
    }

    private sealed class NoMatchProfileClassifier : IAgentProfileTurnClassifier
    {
        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(AgentProfileTurnClassificationResult.NoMatch());
    }

    private sealed class AcceptingDelegationCredentialLifecycle
        : INyxIdChatDelegationCredentialLifecyclePort
    {
        public List<string> DelegationTokens { get; } = [];

        public Task<NyxIdChatDelegationCredentialResolution> ResolveAsync(
            string delegationToken,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            DelegationTokens.Add(delegationToken);
            return Task.FromResult(new NyxIdChatDelegationCredentialResolution(
                true,
                delegationToken));
        }
    }

    private sealed class StreamingCapabilityReplyExecutor(
        AgentToolOperationAdmissionPayload? operationAdmission = null,
        string toolName = "tool-alpha")
        : IAgentRunReplyGenerationExecutorPort
    {
        private readonly AgentRunToolCall _authorizedToolCall = new()
        {
            Id = "call-alpha",
            Name = toolName,
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
                        Id = _authorizedToolCall.Id,
                        Name = _authorizedToolCall.Name,
                        ArgumentsJson = _authorizedToolCall.ArgumentsJson,
                    },
                    Presentation = new ToolPresentationDescriptor
                    {
                        InvocationName = _authorizedToolCall.Name,
                        DisplayName = _authorizedToolCall.Name == "web_search"
                            ? "Web search"
                            : "Tool Alpha",
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
            result.ToolCalls.Add(_authorizedToolCall.Clone());
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
                [_authorizedToolCall],
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
                                ToolCallId = _authorizedToolCall.Id,
                                Content = "{\"ok\":true}",
                            },
                        },
                        ToolReceipts =
                        {
                            new AgentToolReceipt
                            {
                                CallId = _authorizedToolCall.Id,
                                ToolName = _authorizedToolCall.Name,
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
                        _authorizedToolCall.Id,
                        _authorizedToolCall.Name,
                        _authorizedToolCall.ArgumentsJson,
                        new AgentToolCallSafety(
                            RequiresApproval: false,
                            IsReadOnly: toolName == "web_search",
                            IsDestructive: false),
                        SideEffectKind: toolName == "web_search"
                            ? "web.search"
                            : "tool-alpha.update",
                        Presentation: new ToolPresentationDescriptor
                        {
                            InvocationName = _authorizedToolCall.Name,
                            DisplayName = _authorizedToolCall.Name == "web_search"
                                ? "Web search"
                                : "Tool Alpha",
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

    private sealed class DeterministicActorDispatchPort : IActorDispatchPort
    {
        private readonly object _sync = new();
        private readonly List<(string ActorId, EventEnvelope Envelope)> _pending = [];

        public TaskCompletionSource ExecutionCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync)
                _pending.Add((actorId, envelope.Clone()));
            if (envelope.Payload.Is(
                    NyxIdChatTurnOperationExecutionCompletedSignal.Descriptor))
            {
                ExecutionCompleted.TrySetResult();
            }

            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public EventEnvelope TakeSingle(
            string actorId,
            Func<EventEnvelope, bool> predicate)
        {
            lock (_sync)
            {
                var index = _pending.FindIndex(item =>
                    string.Equals(item.ActorId, actorId, StringComparison.Ordinal) &&
                    predicate(item.Envelope));
                index.Should().BeGreaterThanOrEqualTo(0);
                var envelope = _pending[index].Envelope;
                _pending.RemoveAt(index);
                return envelope;
            }
        }

        public IReadOnlyList<EventEnvelope> TakeAll(
            string actorId,
            Func<EventEnvelope, bool> predicate)
        {
            lock (_sync)
            {
                var envelopes = _pending
                    .Where(item =>
                        string.Equals(item.ActorId, actorId, StringComparison.Ordinal) &&
                        predicate(item.Envelope))
                    .Select(static item => item.Envelope)
                    .ToArray();
                _pending.RemoveAll(item =>
                    string.Equals(item.ActorId, actorId, StringComparison.Ordinal) &&
                    predicate(item.Envelope));
                return envelopes;
            }
        }
    }

    private sealed class ImmediateActorRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(
            string? id = null,
            CancellationToken ct = default)
            where TAgent : IAgent => CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(
            System.Type agentType,
            string? id = null,
            CancellationToken ct = default)
        {
            _ = agentType;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IActor>(new ImmediateActor(id ?? Guid.NewGuid().ToString("N")));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(
            string parentId,
            string childId,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ImmediateActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new ImmediateAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class ImmediateAgent : IAgent
    {
        public string Id => "immediate-agent";
        public Task<string> GetDescriptionAsync() => Task.FromResult(Id);
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ImmediateChatHistoryCommandPort : IChatHistoryCommandPort
    {
        public Task InitializeConversationAsync(
            ChatHistoryConversationInitialization request,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task ReserveTurnDeliveryAsync(
            ChatHistoryTurnDeliveryReservation request,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task NotifyTurnTerminalAsync(
            ChatHistoryTurnTerminalNotification notification,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveMessagesAsync(
            string scopeId,
            string conversationId,
            ConversationMeta meta,
            IReadOnlyList<StoredChatMessage> messages,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<ChatHistoryDeleteResult> DeleteConversationAsync(
            string scopeId,
            string conversationId,
            CancellationToken ct = default) =>
            Task.FromResult(ChatHistoryDeleteResult.Accepted());
    }

    private sealed class ImmediateActorRegistryCommandPort
        : IGAgentActorRegistryCommandPort
    {
        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionVisible));

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
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

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

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
