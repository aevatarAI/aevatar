using System.Reflection;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Type = System.Type;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatConversationGAgentTests
{
    [Fact]
    public void PublicAndLegacyActors_ShouldHaveDistinctExplicitKinds()
    {
        typeof(NyxIdChatConversationGAgent).GetCustomAttribute<GAgentAttribute>()!.Kind
            .Should().Be(NyxIdChatServiceDefaults.GAgentKind);
        typeof(NyxIdChatGAgent).GetCustomAttribute<GAgentAttribute>()!.Kind
            .Should().Be(NyxIdChatServiceDefaults.LegacyGAgentKind);
    }

    [Fact]
    public void LegacyActor_ShouldNotOwnPublicConversationLifecycleHandlers()
    {
        var lifecyclePayloadTypes = new HashSet<Type>
        {
            typeof(NyxIdChatConversationCreateCommand),
            typeof(NyxIdChatConversationCreationCompensationRequested),
            typeof(NyxIdChatConversationDeleteCommand),
            typeof(NyxIdChatConversationDeletionCompensationRequested),
        };

        var subscribedLifecyclePayloads = typeof(NyxIdChatGAgent)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static method => method.GetCustomAttribute<EventHandlerAttribute>() is not null)
            .SelectMany(static method => method.GetParameters().Take(1))
            .Select(static parameter => parameter.ParameterType)
            .Where(lifecyclePayloadTypes.Contains)
            .ToArray();

        subscribedLifecyclePayloads.Should().BeEmpty(
            "the public conversation controller must be the only lifecycle authority");
    }

    [Fact]
    public void ResponsiveActorTypes_ShouldBeAvailable()
    {
        var assembly = typeof(NyxIdChatStartTurnCommand).Assembly;

        assembly.GetType("Aevatar.GAgents.NyxidChat.NyxIdChatConversationGAgent")
            .Should().NotBeNull();
        assembly.GetType("Aevatar.GAgents.NyxidChat.NyxIdChatTurnGAgent")
            .Should().NotBeNull();
        assembly.GetType("Aevatar.GAgents.NyxidChat.NyxIdChatTurnOperationExecutor")
            .Should().NotBeNull();
        assembly.GetType("Aevatar.GAgents.NyxidChat.NyxIdChatTurnActorIds")
            .Should().NotBeNull();
    }

    [Fact]
    public void ResponsiveActors_ShouldDependOnNarrowRuntimeNeutralPorts()
    {
        var assembly = typeof(NyxIdChatStartTurnCommand).Assembly;
        var executorPort = assembly.GetType(
            "Aevatar.GAgents.NyxidChat.INyxIdChatTurnOperationExecutor");

        executorPort.Should().NotBeNull();
        typeof(NyxIdChatConversationGAgent).GetConstructor(
        [
            typeof(IActorRuntime),
            typeof(IActorDispatchPort),
            typeof(TimeProvider),
        ]).Should().NotBeNull();
        typeof(NyxIdChatTurnGAgent).GetConstructor(
        [
            executorPort!,
            typeof(IActorDispatchPort),
            typeof(TimeProvider),
        ]).Should().NotBeNull();
    }

    [Fact]
    public void TurnActorAddress_ShouldBeStableOpaqueAndTurnScoped()
    {
        var method = typeof(NyxIdChatTurnActorIds).GetMethod(
            "ForTurn",
            [typeof(string), typeof(string)]);

        method.Should().NotBeNull();
        var first = (string)method!.Invoke(null, ["conversation-alpha", "turn-alpha"])!;
        var replay = (string)method.Invoke(null, ["conversation-alpha", "turn-alpha"])!;
        var otherTurn = (string)method.Invoke(null, ["conversation-alpha", "turn-beta"])!;

        first.Should().Be(replay);
        first.Should().StartWith("nyxid-chat-turn:");
        first.Should().NotContain("conversation-alpha").And.NotContain("turn-alpha");
        otherTurn.Should().NotBe(first);
    }

    [Fact]
    public async Task StartTurn_ShouldCommitRequestedWaterlineBeforeCreatingAndDispatchingTurnActor()
    {
        const string conversationActorId = "conversation-alpha";
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations);
        var eventStore = new InMemoryEventStoreForTests();
        NyxIdChatConversationGAgent? agent = null;
        NyxIdChatConversationGAgentState? stateObservedAtDispatch = null;
        IReadOnlyList<StateEvent>? eventsObservedAtDispatch = null;
        var dispatchPort = new RecordingActorDispatchPort(
            operations,
            async (actorId, envelope) =>
            {
                actorId.Should().Be(NyxIdChatTurnActorIds.ForTurn(conversationActorId, "turn-alpha"));
                stateObservedAtDispatch = agent!.State.Clone();
                eventsObservedAtDispatch = await eventStore.GetEventsAsync(conversationActorId);
                envelope.Payload.Is(NyxIdChatOperationDispatchCommand.Descriptor).Should().BeTrue();
            });
        using var services = BuildEventSourcingServices(eventStore);
        agent = new NyxIdChatConversationGAgent(
            runtime,
            dispatchPort,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();

        var command = CreateStartTurnCommand();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, command));

        operations.Should().Equal("create", "link", "dispatch");
        runtime.CreateCalls.Should().ContainSingle().Which.Should().Be(
            (typeof(NyxIdChatTurnGAgent), NyxIdChatTurnActorIds.ForTurn(conversationActorId, "turn-alpha")));
        runtime.LinkCalls.Should().ContainSingle().Which.Should().Be(
            (conversationActorId, NyxIdChatTurnActorIds.ForTurn(conversationActorId, "turn-alpha")));
        dispatchPort.Calls.Should().ContainSingle();

        stateObservedAtDispatch.Should().NotBeNull();
        stateObservedAtDispatch!.ConversationActorId.Should().Be(conversationActorId);
        stateObservedAtDispatch.ScopeId.Should().Be("scope-alpha");
        stateObservedAtDispatch.ActiveTurn.TurnId.Should().Be("turn-alpha");
        stateObservedAtDispatch.ActiveTurn.TaskId.Should().Be("task-alpha");
        stateObservedAtDispatch.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        stateObservedAtDispatch.ActiveTask.TaskId.Should().Be("task-alpha");
        stateObservedAtDispatch.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        var requestedStep = stateObservedAtDispatch.ActiveTask.Steps.Should().ContainSingle().Which;
        requestedStep.Kind.Should().Be(NyxIdChatStepKind.Llm);
        requestedStep.Status.Should().Be(NyxIdChatStepStatus.Running);
        requestedStep.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        requestedStep.Operation.Key.ConversationActorId.Should().Be(conversationActorId);
        requestedStep.Operation.Key.TurnId.Should().Be("turn-alpha");
        requestedStep.Operation.Key.TaskId.Should().Be("task-alpha");
        requestedStep.Operation.Key.OperationGeneration.Should().Be(1);

        eventsObservedAtDispatch.Should().ContainSingle();
        eventsObservedAtDispatch![0].EventData.Is(NyxIdChatTurnStartedEvent.Descriptor).Should().BeTrue();
        var committedEvents = await eventStore.GetEventsAsync(conversationActorId);
        committedEvents.Select(static item => item.EventData.TypeUrl).Should().Equal(
            Any.Pack(new NyxIdChatTurnStartedEvent()).TypeUrl,
            Any.Pack(new NyxIdChatOperationDispatchedEvent()).TypeUrl);
        agent.State.ActiveTask.Steps.Single().Operation.Phase.Should()
            .Be(NyxIdChatOperationPhase.Dispatched);
    }

    [Fact]
    public async Task ChildResult_ShouldBecomeProductFactOnlyAfterCompleteKeyReconciliationCommit()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var key = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        var mismatched = new NyxIdChatOperationResultSignal
        {
            Key = key.Clone(),
            Llm = new NyxIdChatLLMOperationResult { Content = "must be ignored" },
        };
        mismatched.Key.OperationId = "operation-wrong";

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, mismatched));

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(2);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        agent.State.ActiveTask.Steps.Single().Status.Should().Be(NyxIdChatStepStatus.Running);

        var accepted = new NyxIdChatOperationResultSignal
        {
            Key = key,
            Llm = new NyxIdChatLLMOperationResult { Content = "completed" },
        };
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, accepted));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(3);
        committed[^1].EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor).Should().BeTrue();
        var reconciliation = committed[^1].EventData.Unpack<NyxIdChatOperationReconciledEvent>();
        reconciliation.Result.Key.Should().BeEquivalentTo(key);
        reconciliation.Task.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        reconciliation.Turn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        agent.State.ActiveTask.Steps.Single().Status.Should().Be(NyxIdChatStepStatus.Done);
    }

    [Fact]
    public async Task LlmToolResult_ShouldCommitSuccessorRequestedWaterlineBeforeDispatch()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        NyxIdChatConversationGAgent? agent = null;
        NyxIdChatConversationGAgentState? stateObservedAtSuccessorDispatch = null;
        IReadOnlyList<StateEvent>? eventsObservedAtSuccessorDispatch = null;
        var dispatchCount = 0;
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            async (_, envelope) =>
            {
                dispatchCount++;
                if (dispatchCount != 2)
                    return;

                var command = envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>();
                command.InputCase.Should().Be(
                    NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
                stateObservedAtSuccessorDispatch = agent!.State.Clone();
                eventsObservedAtSuccessorDispatch = await eventStore.GetEventsAsync(
                    conversationActorId);
            });
        using var services = BuildEventSourcingServices(eventStore);
        agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var llmKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        var llmResult = new NyxIdChatOperationResultSignal
        {
            Key = llmKey,
            Llm = new NyxIdChatLLMOperationResult
            {
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "call-alpha",
                        ToolName = "repository_update",
                        ArgumentsJson = "{\"repositoryId\":\"repo-alpha\"}",
                        Safety = new NyxIdChatToolCallSafety
                        {
                            SideEffectKind = "repository.update",
                            MayChangeExternalState = true,
                        },
                    },
                },
            },
        };

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, llmResult));

        dispatch.Calls.Should().HaveCount(2);
        stateObservedAtSuccessorDispatch.Should().NotBeNull();
        var toolStep = stateObservedAtSuccessorDispatch!.ActiveTask.Steps.Last();
        toolStep.Kind.Should().Be(NyxIdChatStepKind.Tool);
        toolStep.Status.Should().Be(NyxIdChatStepStatus.Running);
        toolStep.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        eventsObservedAtSuccessorDispatch.Should().NotBeNull();
        eventsObservedAtSuccessorDispatch![^1].EventData.Is(
            NyxIdChatOperationReconciledEvent.Descriptor).Should().BeTrue();
        var committedReconciliation = eventsObservedAtSuccessorDispatch[^1].EventData
            .Unpack<NyxIdChatOperationReconciledEvent>();
        committedReconciliation.Result.Llm.ToolCalls.Should().ContainSingle()
            .Which.ArgumentsJson.Should().BeEmpty(
                "tool arguments are transient dispatch input, not durable product facts");
        dispatch.Calls[^1].Envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>()
            .Tool.ArgumentsJson.Should().Be("{\"repositoryId\":\"repo-alpha\"}");
        eventsObservedAtSuccessorDispatch.Should().HaveCount(3,
            "the successor dispatched waterline is committed only after dispatch admission");

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(4);
        committed[^1].EventData.Is(NyxIdChatOperationDispatchedEvent.Descriptor).Should().BeTrue();
        agent.State.ActiveTask.Steps.Last().Operation.Phase.Should().Be(
            NyxIdChatOperationPhase.Dispatched);
    }

    [Fact]
    public async Task ChildProgress_ShouldCommitMatchingMonotonicSequenceAndIgnoreDuplicateOrWrongKey()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var key = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        var progress = new NyxIdChatOperationProgressSignal
        {
            Key = key,
            Sequence = 1,
            Text = new NyxIdChatTextProgress { Delta = "hello" },
        };

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, progress));

        var afterFirst = await eventStore.GetEventsAsync(conversationActorId);
        afterFirst.Should().HaveCount(3);
        afterFirst[^1].EventData.TypeUrl.Should().EndWith("NyxIdChatOperationProgressedEvent");
        agent.State.ProgressSequence.Should().Be(2);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, progress.Clone()));
        var wrong = progress.Clone();
        wrong.Sequence = 2;
        wrong.Key.StepId = "step-wrong";
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, wrong));

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(3);
        agent.State.ProgressSequence.Should().Be(2);
    }

    [Fact]
    public async Task StopDuringDispatchedLlm_ShouldCommitDurableFenceAndStoppedTerminal()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatStopCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = conversationActorId,
            TurnId = "turn-alpha",
            StopRequestId = "stop-alpha",
            ClientRequestId = "client-stop-alpha",
            CommandId = "command-stop-alpha",
            CorrelationId = "correlation-stop-alpha",
            ExpectedStateVersion = 2,
        }));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(3);
        committed[^1].EventData.Is(NyxIdChatControlFenceCommittedEvent.Descriptor).Should().BeTrue();
        var fence = committed[^1].EventData.Unpack<NyxIdChatControlFenceCommittedEvent>().Fence;
        fence.Kind.Should().Be(NyxIdChatControlKind.Stop);
        fence.Outcome.Should().Be(NyxIdChatControlOutcome.Uncancellable);
        fence.RequestId.Should().Be("stop-alpha");
        agent.State.ControlFence.Should().BeEquivalentTo(fence);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
        var stoppedStep = agent.State.ActiveTask.Steps.Should().ContainSingle().Which;
        stoppedStep.Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        stoppedStep.Operation.Phase.Should().Be(
            NyxIdChatOperationPhase.Dispatched,
            "an unsupported physical cancellation must not pretend the child operation ended");
        dispatch.Calls.Should().ContainSingle("stop commits a fence and does not dispatch more work");
    }

    [Fact]
    public async Task LateToolReceiptAfterStop_ShouldOnlyRefineExactEffectEvidence()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));

        var llmKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = llmKey,
            Llm = new NyxIdChatLLMOperationResult
            {
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "call-alpha",
                        ToolName = "repository_update",
                        ArgumentsJson = "{\"repositoryId\":\"repo-alpha\"}",
                        Safety = new NyxIdChatToolCallSafety
                        {
                            SideEffectKind = "repository.update",
                            MayChangeExternalState = true,
                        },
                    },
                },
            },
        }));
        var toolKey = agent.State.ActiveTask.Steps[^1].Operation.Key.Clone();

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStopCommand(4)));

        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
        agent.State.ActiveTask.Steps[^1].Status.Should().Be(NyxIdChatStepStatus.Uncertain);
        agent.State.ActiveTask.Steps[^1].ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.MayHaveChanged);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = toolKey,
            Tool = new NyxIdChatToolOperationResult
            {
                ResultJson = "{\"secret\":\"must-not-be-committed\"}",
                ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
                Receipt = new Aevatar.AI.Abstractions.AgentToolReceipt
                {
                    CallId = "call-alpha",
                    ToolName = "repository_update",
                    Status = Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success,
                    SubjectKind = "repository",
                    SubjectId = "repo-alpha",
                },
            },
        }));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(6);
        committed[^1].EventData.TypeUrl.Should().EndWith(
            "NyxIdChatLateOperationEvidenceCommittedEvent");
        var evidence = committed[^1].EventData.Unpack<NyxIdChatLateOperationEvidenceCommittedEvent>();
        evidence.Key.Should().BeEquivalentTo(toolKey);
        evidence.OperationPhase.Should().Be(NyxIdChatOperationPhase.Succeeded);
        evidence.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.Confirmed);
        evidence.ToolReceipt.Status.Should().Be(
            Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success);
        evidence.ToString().Should().NotContain("must-not-be-committed");

        var stoppedTool = agent.State.ActiveTask.Steps[^1];
        stoppedTool.Status.Should().Be(NyxIdChatStepStatus.Uncertain,
            "late evidence cannot regress or advance the terminal stopped step");
        stoppedTool.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.Confirmed);
        stoppedTool.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Succeeded);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
        dispatch.Calls.Should().HaveCount(2,
            "late evidence cannot start an old-plan successor");

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = toolKey,
            Tool = new NyxIdChatToolOperationResult
            {
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
                Receipt = new Aevatar.AI.Abstractions.AgentToolReceipt
                {
                    CallId = "call-alpha",
                    ToolName = "repository_update",
                    Status = Aevatar.AI.Abstractions.AgentToolReceiptStatus.Error,
                },
            },
        }));

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(6,
            "terminal exact evidence is monotonic and conflicting duplicates fail closed");
        agent.State.ActiveTask.Steps[^1].ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.Confirmed);
    }

    [Fact]
    public async Task LateLlmOutputAfterStop_ShouldBeDiscardedWithoutSuccessor()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var key = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStopCommand(2)));

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = key,
            Llm = new NyxIdChatLLMOperationResult
            {
                Content = "late body must disappear",
                ReasoningContent = "late reasoning must disappear",
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "late-call",
                        ToolName = "late_tool",
                        ArgumentsJson = "{\"secret\":\"late\"}",
                        Safety = new NyxIdChatToolCallSafety { IsReadOnly = true },
                    },
                },
            },
        }));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(3);
        committed.Select(static evt => evt.EventData.ToString()).Should().NotContain(value =>
            value.Contains("late body", StringComparison.Ordinal) ||
            value.Contains("late reasoning", StringComparison.Ordinal) ||
            value.Contains("late-call", StringComparison.Ordinal));
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
        dispatch.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task StopReplay_ShouldUseDurableFenceAfterLaterControlResult()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var stop = CreateStopCommand(2);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, stop));

        var conflicting = stop.Clone();
        conflicting.ClientRequestId = "client-stop-conflict";
        conflicting.CommandId = "command-stop-conflict";
        conflicting.CorrelationId = "correlation-stop-conflict";
        conflicting.ExpectedStateVersion = 0;
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, conflicting));

        var afterConflict = await eventStore.GetEventsAsync(conversationActorId);
        afterConflict.Should().HaveCount(4);
        var rejected = afterConflict[^1].EventData
            .Unpack<NyxIdChatControlFenceCommittedEvent>();
        rejected.Fence.Outcome.Should().Be(NyxIdChatControlOutcome.Rejected);
        rejected.Fence.ReasonCode.Should().Be(NyxIdChatControlCommands.ControlConflict);
        agent.State.ControlFence.RequestId.Should().Be("stop-alpha");
        agent.State.LatestControlResult.Outcome.Should().Be(NyxIdChatControlOutcome.Rejected);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, stop.Clone()));

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(4,
            "the accepted durable fence owns exact stop replay even when latest_control_result changed");
        agent.State.ControlFence.RequestId.Should().Be("stop-alpha");
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
    }

    [Fact]
    public async Task OrdinaryStartDuringActiveTurn_ShouldCommitTypedSteeringRequiredRejection()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var concurrent = CreateStartTurnCommand();
        concurrent.TurnId = "turn-beta";
        concurrent.TaskId = "task-beta";
        concurrent.ClientRequestId = "client-beta";
        concurrent.CommandId = "command-beta";
        concurrent.CorrelationId = "correlation-beta";
        concurrent.Prompt = "change direction";

        var act = () => agent.HandleEventAsync(CreateEnvelope(conversationActorId, concurrent));

        await act.Should().NotThrowAsync();
        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(3);
        committed[^1].EventData.TypeUrl.Should().EndWith("NyxIdChatTurnAdmissionRejectedEvent");
        agent.State.ActiveTurn.TurnId.Should().Be("turn-alpha");
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
    }

    [Fact]
    public async Task TerminalTurn_ShouldAllowASeparateOrdinaryTurn()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var completedKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = completedKey,
            Llm = new NyxIdChatLLMOperationResult { Content = "done" },
        }));
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);

        var next = CreateStartTurnCommand();
        next.TurnId = "turn-beta";
        next.TaskId = "task-beta";
        next.ClientRequestId = "client-beta";
        next.CommandId = "command-beta";
        next.CorrelationId = "correlation-beta";
        next.Prompt = "next request";
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, next));

        agent.State.ActiveTurn.TurnId.Should().Be("turn-beta");
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        agent.State.ActiveTask.TaskId.Should().Be("task-beta");
        agent.State.RecentTerminalTurns.Should().ContainSingle(summary =>
            summary.TurnId == "turn-alpha" &&
            summary.Status == NyxIdChatTurnStatus.Succeeded);
        dispatch.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task SteeringDuringDispatchedOperation_ShouldFenceOldTurnAndAllocateOneContinuation()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));

        var steering = new NyxIdChatSteeringCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = conversationActorId,
            TurnId = "turn-alpha",
            SteeringId = "steering-alpha",
            ClientRequestId = "client-steering-alpha",
            CommandId = "command-steering-alpha",
            CorrelationId = "correlation-steering-alpha",
            Instruction = "Use the safer read-only approach.",
            ExpectedStateVersion = 2,
        };
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering));
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering.Clone()));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Count(evt => evt.EventData.Is(NyxIdChatControlFenceCommittedEvent.Descriptor))
            .Should().Be(1);
        committed.Count(evt => evt.EventData.Is(NyxIdChatContinuationAdmissionCommittedEvent.Descriptor))
            .Should().Be(1);
        var committedFence = committed.Single(evt =>
                evt.EventData.Is(NyxIdChatControlFenceCommittedEvent.Descriptor))
            .EventData.Unpack<NyxIdChatControlFenceCommittedEvent>();
        committedFence.State.ControlFence.Should().NotBeNull(
            "the control commit must carry the durable execution fence");
        var committedAdmission = committed.Single(evt =>
                evt.EventData.Is(NyxIdChatContinuationAdmissionCommittedEvent.Descriptor))
            .EventData.Unpack<NyxIdChatContinuationAdmissionCommittedEvent>();
        committedAdmission.State.ControlFence.Should().NotBeNull(
            "the continuation snapshot must preserve the old-plan fence");
        agent.State.ControlFence.Kind.Should().Be(NyxIdChatControlKind.Steering);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
        agent.State.ContinuationAdmission.RequestId.Should().Be("steering-alpha");
        agent.State.ContinuationAdmission.OriginTurnId.Should().Be("turn-alpha");
        agent.State.ContinuationAdmission.ContinuationTurnId.Should().NotBeNullOrWhiteSpace();
        agent.State.ContinuationAdmission.ContinuationTurnId.Should().NotBe("turn-alpha");
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.AcceptedForLater);
        dispatch.Calls.Should().ContainSingle(
            "the revised instruction cannot run concurrently with an uncancellable old operation");
    }

    [Fact]
    public async Task SteeringReplayAfterLateLlmCheckpoint_ShouldStartOneContinuationWithTransientCapability()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var oldKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        var steering = CreateSteeringCommand(2);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering));

        var continuationTurnId = agent.State.ContinuationAdmission.ContinuationTurnId;
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.AcceptedForLater);
        dispatch.Calls.Should().ContainSingle();

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = oldKey,
            Llm = new NyxIdChatLLMOperationResult
            {
                Content = "discard this old-plan answer",
                ReasoningContent = "discard this old-plan reasoning",
            },
        }));

        var checkpointEvents = await eventStore.GetEventsAsync(conversationActorId);
        checkpointEvents.Should().HaveCount(5);
        checkpointEvents[^1].EventData.Is(
            NyxIdChatLateOperationEvidenceCommittedEvent.Descriptor).Should().BeTrue();
        checkpointEvents[^1].EventData.ToString().Should()
            .NotContain("discard this old-plan answer")
            .And.NotContain("discard this old-plan reasoning");
        agent.State.ActiveTask.Steps.Single().Operation.Phase.Should().Be(
            NyxIdChatOperationPhase.Succeeded);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        dispatch.Calls.Should().ContainSingle(
            "the late old-plan result only establishes a checkpoint");

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering.Clone()));

        agent.State.ActiveTurn.TurnId.Should().Be("turn-alpha",
            "the continuation must not advance inline in the steering actor turn");
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.AcceptedForLater);
        dispatch.Calls.Should().HaveCount(2);
        var selfDispatch = dispatch.Calls[^1];
        selfDispatch.ActorId.Should().Be(conversationActorId);
        selfDispatch.Envelope.Route.Direct.TargetActorId.Should().Be(conversationActorId);
        selfDispatch.Envelope.Payload.Is(NyxIdChatStartTurnCommand.Descriptor).Should().BeTrue();
        var continuationStart = selfDispatch.Envelope.Payload
            .Unpack<NyxIdChatStartTurnCommand>();
        continuationStart.TurnId.Should().Be(continuationTurnId);
        continuationStart.CommandId.Should().Be(selfDispatch.Envelope.Id);
        continuationStart.CommandId.Should().NotBe(steering.CommandId,
            "the self continuation has its own stable inbox identity");
        continuationStart.LlmControl.NyxIdAccessToken.Should().Be(
            "steering-runtime-token-alpha");
        agent.State.ToString().Should().NotContain("steering-runtime-token-alpha");

        await agent.HandleEventAsync(selfDispatch.Envelope);

        agent.State.ActiveTurn.TurnId.Should().Be(continuationTurnId);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Started);
        dispatch.Calls.Should().HaveCount(3);
        var continuation = dispatch.Calls[^1].Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        continuation.Key.TurnId.Should().Be(continuationTurnId);
        continuation.Llm.Request.Prompt.Should().Be("Use the safer read-only approach.");
        continuation.Llm.Request.LlmControl.NyxIdAccessToken.Should().Be(
            "steering-runtime-token-alpha");
        continuation.Llm.Request.ToolContext.Credentials.NyxIdAccessToken.Should().Be(
            "steering-runtime-token-alpha");
        agent.State.ToString().Should().NotContain("steering-runtime-token-alpha");

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering.Clone()));

        dispatch.Calls.Should().HaveCount(3,
            "a started continuation is idempotent under exact steering replay");
    }

    [Fact]
    public async Task SteeringAcceptedCheckpointReplay_ShouldRedispatchSameSelfContinuationAfterDeliveryGap()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var initial = CreateController(services, conversationActorId);
        await initial.ActivateAsync();
        await initial.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            CreateStartTurnCommand()));
        var requestedCheckpoint = initial.State.Clone();
        requestedCheckpoint.ActiveTask.Steps.Single().Operation.Phase =
            NyxIdChatOperationPhase.Requested;
        await PersistTestStateAsync(
            eventStore,
            conversationActorId,
            version: 3,
            requestedCheckpoint);

        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var reactivated = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(reactivated, conversationActorId);
        await reactivated.ActivateAsync();
        var steering = CreateSteeringCommand(expectedStateVersion: 3);

        await reactivated.HandleEventAsync(CreateEnvelope(conversationActorId, steering));

        reactivated.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Accepted);
        dispatch.Calls.Should().ContainSingle();
        var firstSelfMessage = dispatch.Calls.Single().Envelope.Clone();
        firstSelfMessage.Payload.Is(NyxIdChatStartTurnCommand.Descriptor).Should().BeTrue();
        var beforeReplay = await eventStore.GetEventsAsync(conversationActorId);

        await reactivated.HandleEventAsync(CreateEnvelope(conversationActorId, steering.Clone()));

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(
            beforeReplay.Count,
            "an exact replay must not commit the admission twice");
        dispatch.Calls.Should().HaveCount(2,
            "an accepted but unhandled self continuation must be safely redeliverable");
        dispatch.Calls[^1].Envelope.Id.Should().Be(firstSelfMessage.Id);
        dispatch.Calls[^1].Envelope.Payload.ToByteString().Should().Equal(
            firstSelfMessage.Payload.ToByteString());
        reactivated.State.ToString().Should().NotContain("steering-runtime-token-alpha");
    }

    [Fact]
    public async Task SteeringSameIdentityWithDifferentInstruction_ShouldFailClosed()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var steering = CreateSteeringCommand(2);
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering));
        var continuationTurnId = agent.State.ContinuationAdmission.ContinuationTurnId;

        var conflicting = steering.Clone();
        conflicting.Instruction = "Delete everything instead.";
        conflicting.ExpectedStateVersion = 0;
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, conflicting));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        var conflict = committed[^1].EventData.Unpack<NyxIdChatControlFenceCommittedEvent>();
        conflict.Fence.Outcome.Should().Be(NyxIdChatControlOutcome.Rejected);
        conflict.Fence.ReasonCode.Should().Be(NyxIdChatControlCommands.ControlConflict);
        agent.State.ContinuationAdmission.ContinuationTurnId.Should().Be(continuationTurnId);
        agent.State.ContinuationAdmission.Instruction.Should().Be(
            "Use the safer read-only approach.");
    }

    [Fact]
    public async Task RetryFailedLlm_ShouldCommitGenerationBeforeTransientDispatch()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        IReadOnlyList<StateEvent>? eventsObservedAtRetryDispatch = null;
        NyxIdChatConversationGAgent? agent = null;
        var dispatchCount = 0;
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            async (_, envelope) =>
            {
                dispatchCount++;
                if (dispatchCount != 2)
                    return;

                eventsObservedAtRetryDispatch = await eventStore.GetEventsAsync(
                    conversationActorId);
                var retry = envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>();
                retry.Key.OperationGeneration.Should().Be(2);
                retry.Llm.Request.LlmControl.NyxIdAccessToken.Should().Be(
                    "retry-runtime-token-alpha");
                agent!.State.ActiveTask.Steps.Single().Operation.Key
                    .Should().BeEquivalentTo(retry.Key);
            });
        using var services = BuildEventSourcingServices(eventStore);
        agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var firstKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = firstKey,
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = "MODEL_FAILED",
                SafeMessage = "The model attempt failed.",
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            },
        }));
        agent.State.ActiveTask.Steps.Single().AvailableActions.Retry.Should().BeTrue();

        var retry = CreateRetryCommand(firstKey.StepId, expectedStateVersion: 3);
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, retry));

        eventsObservedAtRetryDispatch.Should().NotBeNull();
        eventsObservedAtRetryDispatch![^1].EventData.Is(
            NyxIdChatStepControlCommittedEvent.Descriptor).Should().BeTrue();
        var committed = eventsObservedAtRetryDispatch[^1].EventData
            .Unpack<NyxIdChatStepControlCommittedEvent>();
        committed.Result.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        committed.Result.OperationGeneration.Should().Be(2);
        committed.State.ToString().Should().NotContain("retry-runtime-token-alpha");
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        agent.State.RecentTerminalTurns.Should().BeEmpty();

        var all = await eventStore.GetEventsAsync(conversationActorId);
        all[^1].EventData.Is(NyxIdChatOperationDispatchedEvent.Descriptor).Should().BeTrue();
        agent.State.ActiveTask.Steps.Single().Operation.Phase.Should().Be(
            NyxIdChatOperationPhase.Dispatched);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, retry.Clone()));
        dispatch.Calls.Should().HaveCount(2,
            "an already dispatched retry is an idempotent replay");
        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(all.Count);
    }

    [Fact]
    public async Task LaterTurn_ShouldPreserveBoundedStepControlReplayFacts()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var firstKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = firstKey,
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = "MODEL_FAILED",
                SafeMessage = "The model attempt failed.",
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            },
        }));
        var retry = CreateRetryCommand(firstKey.StepId, expectedStateVersion: 3);
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, retry));
        var retryKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = retryKey,
            Llm = new NyxIdChatLLMOperationResult { Content = "Recovered." },
        }));
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);

        var nextTurn = CreateStartTurnCommand();
        nextTurn.TurnId = "turn-beta";
        nextTurn.TaskId = "task-beta";
        nextTurn.ClientRequestId = "client-beta";
        nextTurn.CommandId = "command-beta";
        nextTurn.CorrelationId = "correlation-beta";
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, nextTurn));

        agent.State.ActiveTurn.TurnId.Should().Be("turn-beta");
        agent.State.RecentStepControlResults.Should().ContainSingle(result =>
            result.RequestId == "retry-alpha" &&
            result.TurnId == "turn-alpha" &&
            result.Outcome == NyxIdChatTransitionOutcome.Accepted);
        agent.State.LatestStepControlResult.RequestId.Should().Be("retry-alpha");

        var beforeReplay = await eventStore.GetEventsAsync(conversationActorId);
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, retry.Clone()));
        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(
            beforeReplay.Count,
            "an old exact request identity remains an idempotent replay after a later turn starts");
    }

    [Fact]
    public async Task SkipOptionalFailedStep_ShouldCommitTypedSuccessWithoutDispatch()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var key = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = key,
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = "OPTIONAL_FAILED",
                SafeMessage = "The optional step failed.",
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            },
        }));
        var failed = agent.State.Clone();
        failed.ActiveTask.Steps.Single().Required = false;
        failed.ActiveTask.Steps.Single().AvailableActions =
            NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(
                failed.ActiveTask.Steps.Single());
        await PersistTestStateAsync(eventStore, conversationActorId, version: 4, failed);
        var reactivated = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(reactivated, conversationActorId);
        await reactivated.ActivateAsync();

        await reactivated.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatSkipStepCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = conversationActorId,
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = key.StepId,
            SkipRequestId = "skip-alpha",
            ClientRequestId = "client-skip-alpha",
            CommandId = "command-skip-alpha",
            CorrelationId = "correlation-skip-alpha",
            ExpectedOperationGeneration = 1,
            ExpectedStateVersion = 4,
        }));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed[^1].EventData.Is(NyxIdChatStepControlCommittedEvent.Descriptor).Should().BeTrue();
        reactivated.State.ActiveTask.Steps.Single().Status.Should().Be(
            NyxIdChatStepStatus.Skipped);
        reactivated.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        reactivated.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        dispatch.Calls.Should().ContainSingle("skip never starts provider or tool I/O");
    }

    [Fact]
    public void StreamingCommand_ShouldKeepClientRequestIdentityDistinctFromTurnIdentity()
    {
        typeof(NyxIdChatCommand).GetProperty("ClientRequestId").Should().NotBeNull();
    }

    [Fact]
    public void StreamingEnvelope_ShouldDispatchTypedStartTurnCommand()
    {
        var factory = new NyxIdChatCommandEnvelopeFactory();
        var command = new NyxIdChatCommand(
            "conversation-alpha",
            "scope-alpha",
            "hello",
            "turn-alpha",
            "runtime-token-alpha",
            null,
            null);

        var envelope = factory.CreateEnvelope(
            command,
            new CommandContext(
                "conversation-alpha",
                "command-alpha",
                "correlation-alpha",
                new Dictionary<string, string>()));

        envelope.Payload.Is(NyxIdChatStartTurnCommand.Descriptor).Should().BeTrue();
        var start = envelope.Payload.Unpack<NyxIdChatStartTurnCommand>();
        start.ScopeId.Should().Be("scope-alpha");
        start.ConversationActorId.Should().Be("conversation-alpha");
        start.TurnId.Should().Be("turn-alpha");
        start.TaskId.Should().NotBeNullOrWhiteSpace();
        start.TaskId.Should().NotBe(start.TurnId);
        start.CommandId.Should().Be("command-alpha");
        start.CorrelationId.Should().Be("correlation-alpha");
        start.Prompt.Should().Be("hello");
        start.LlmControl.NyxIdAccessToken.Should().Be("runtime-token-alpha");
        start.ToolContext.Credentials.NyxIdAccessToken.Should().Be("runtime-token-alpha");
    }

    [Fact]
    public void TaskContracts_ShouldDeclareAtomicControllerStartAndTurnActorWaterlines()
    {
        var messageNames = NyxidChatTaskReflection.Descriptor.MessageTypes
            .Select(static descriptor => descriptor.Name)
            .ToArray();

        messageNames.Should().Contain(
        [
            "NyxIdChatTurnStartedEvent",
            "NyxIdChatTurnGAgentState",
            "NyxIdChatTurnOperationAdmittedEvent",
            "NyxIdChatTurnOperationCompletedEvent",
            "NyxIdChatTurnOperationDeliveredEvent",
        ]);
    }

    private static NyxIdChatStartTurnCommand CreateStartTurnCommand() => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        ClientRequestId = "client-alpha",
        CommandId = "command-alpha",
        CorrelationId = "correlation-alpha",
        Prompt = "hello",
        LlmControl = new Aevatar.AI.Abstractions.LLMControlContextPayload
        {
            NyxIdAccessToken = "runtime-token-alpha",
        },
        ToolContext = new Aevatar.AI.Abstractions.AgentToolExecutionContextPayload
        {
            Credentials = new Aevatar.AI.Abstractions.AgentToolCredentialsPayload
            {
                NyxIdAccessToken = "runtime-token-alpha",
            },
        },
    };

    private static NyxIdChatStopCommand CreateStopCommand(long expectedStateVersion) => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        StopRequestId = "stop-alpha",
        ClientRequestId = "client-stop-alpha",
        CommandId = "command-stop-alpha",
        CorrelationId = "correlation-stop-alpha",
        ExpectedStateVersion = expectedStateVersion,
    };

    private static NyxIdChatSteeringCommand CreateSteeringCommand(long expectedStateVersion) => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        SteeringId = "steering-alpha",
        ClientRequestId = "client-steering-alpha",
        CommandId = "command-steering-alpha",
        CorrelationId = "correlation-steering-alpha",
        Instruction = "Use the safer read-only approach.",
        ExpectedStateVersion = expectedStateVersion,
        LlmControl = new Aevatar.AI.Abstractions.LLMControlContextPayload
        {
            NyxIdAccessToken = "steering-runtime-token-alpha",
        },
        ToolContext = new Aevatar.AI.Abstractions.AgentToolExecutionContextPayload
        {
            Credentials = new Aevatar.AI.Abstractions.AgentToolCredentialsPayload
            {
                NyxIdAccessToken = "steering-runtime-token-alpha",
            },
        },
    };

    private static NyxIdChatRetryStepCommand CreateRetryCommand(
        string stepId,
        long expectedStateVersion) => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = stepId,
        RetryRequestId = "retry-alpha",
        ClientRequestId = "client-retry-alpha",
        CommandId = "command-retry-alpha",
        CorrelationId = "correlation-retry-alpha",
        ExpectedOperationGeneration = 1,
        ExpectedStateVersion = expectedStateVersion,
        LlmControl = new Aevatar.AI.Abstractions.LLMControlContextPayload
        {
            NyxIdAccessToken = "retry-runtime-token-alpha",
        },
        ToolContext = new Aevatar.AI.Abstractions.AgentToolExecutionContextPayload
        {
            Credentials = new Aevatar.AI.Abstractions.AgentToolCredentialsPayload
            {
                NyxIdAccessToken = "retry-runtime-token-alpha",
            },
        },
    };

    private static Task PersistTestStateAsync(
        IEventStore eventStore,
        string actorId,
        long version,
        NyxIdChatConversationGAgentState state) =>
        eventStore.AppendAsync(
            actorId,
            [
                new StateEvent
                {
                    EventId = "test-state-replacement-alpha",
                    AgentId = actorId,
                    Version = version,
                    Timestamp = Timestamp.FromDateTimeOffset(
                        new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
                    EventType = NyxIdChatTurnStartedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new NyxIdChatTurnStartedEvent { State = state.Clone() }),
                },
            ],
            expectedVersion: version - 1);

    private static NyxIdChatConversationGAgent CreateController(
        ServiceProvider services,
        string actorId)
    {
        var operations = new List<string>();
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            new RecordingActorDispatchPort(operations, static (_, _) => Task.CompletedTask),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static ServiceProvider BuildEventSourcingServices(IEventStore eventStore) =>
        new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopRuntimeCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

    private static EventEnvelope CreateEnvelope(string actorId, IMessage payload) => new()
    {
        Id = "envelope-alpha",
        Timestamp = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
        Payload = Any.Pack(payload),
        Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = actorId } },
        Propagation = new EnvelopePropagation { CorrelationId = "correlation-alpha" },
    };

    private static void AssignActorId(GAgentBase agent, string actorId) =>
        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [actorId]);

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

    private sealed class RecordingActorRuntime(List<string> operations) : IActorRuntime
    {
        public List<(Type Type, string Id)> CreateCalls { get; } = [];
        public List<(string ParentId, string ChildId)> LinkCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            operations.Add("create");
            CreateCalls.Add((agentType, actorId));
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            operations.Add("link");
            LinkCalls.Add((parentId, childId));
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort(
        List<string> operations,
        Func<string, EventEnvelope, Task> onDispatch)
        : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public async Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            operations.Add("dispatch");
            Calls.Add((actorId, envelope.Clone()));
            await onDispatch(actorId, envelope);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new RecordingAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingAgent : IAgent
    {
        public string Id => "recording-agent";
        public Task<string> GetDescriptionAsync() => Task.FromResult(Id);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
