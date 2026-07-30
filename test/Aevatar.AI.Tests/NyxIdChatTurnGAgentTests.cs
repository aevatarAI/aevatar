using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
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
        agent = CreateAgent(services, executor, dispatch);
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
        var dispatch = new RecordingDispatchPort();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(services, executor, dispatch);
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
    public async Task ToolCommand_AfterActorRestart_ShouldFailNotStartedWithoutReauthorizingOrExecuting()
    {
        var generationExecutor = new CapabilityGeneratingReplyExecutor();
        var operationExecutor = Activator.CreateInstance(
            typeof(NyxIdChatTurnOperationExecutor),
            generationExecutor).Should().BeAssignableTo<INyxIdChatTurnOperationExecutor>().Subject;
        var eventStore = new InMemoryEventStoreForTests();
        var dispatch = new RecordingDispatchPort();
        using var services = BuildEventSourcingServices(eventStore);
        var original = CreateAgent(services, operationExecutor, dispatch);
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

        generationExecutor.LlmExecutions.Should().Be(1);
        generationExecutor.ToolContinuations.Should().Be(0);
        generationExecutor.ToolExecutions.Should().Be(0);

        // Reconstructing the actor replays only its durable operation waterline. The
        // exact authorized-tool capability is intentionally transient and is gone.
        var restarted = CreateAgent(services, operationExecutor, dispatch);
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
            },
        };

        await restarted.HandleEventAsync(CreateEnvelope("turn-actor-alpha", tool));

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

        execution.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Llm);
        execution.Result.Llm.Content.Should().Be("visible text");
        execution.Result.Llm.ReasoningContent.Should().Be("private reasoning");
        var toolCall = execution.Result.Llm.ToolCalls.Should().ContainSingle().Which;
        toolCall.CallId.Should().Be("call-alpha");
        toolCall.ToolName.Should().Be("tool-alpha");
        toolCall.ArgumentsJson.Should().Be("{\"value\":1}");
        toolCall.Safety.Should().NotBeNull();
        toolCall.Safety.IsReadOnly.Should().BeFalse();
        toolCall.Safety.IsDestructive.Should().BeFalse();
        toolCall.Safety.SideEffectKind.Should().Be("tool-alpha.update");
        toolCall.Safety.MayChangeExternalState.Should().BeTrue();
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
            static (_, _) => Task.CompletedTask,
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
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var duplicate = await executor.ExecuteAsync(
            toolCommand.Clone(),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        generationExecutor.ToolExecutions.Should().Be(1);
        first.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Tool);
        first.Result.Tool.ResultJson.Should().Be("{\"ok\":true}");
        first.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        first.Result.Tool.Receipt.CallId.Should().Be("call-alpha");
        first.Result.Tool.Receipt.ToolName.Should().Be("tool-alpha");
        first.Result.Tool.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.Confirmed);
        duplicate.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        duplicate.Result.Failure.FailureCode.Should().Be("NYXID_CHAT_TOOL_CAPABILITY_LOST");
        duplicate.Result.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
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
        INyxIdChatTurnOperationExecutor executor,
        IActorDispatchPort dispatch)
    {
        var agent = new NyxIdChatTurnGAgent(
            executor,
            dispatch,
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

    private static ServiceProvider BuildEventSourcingServices(IEventStore eventStore) =>
        new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopRuntimeCallbackScheduler>()
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

    private sealed class StreamingCapabilityReplyExecutor
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
                        SideEffectKind: "tool-alpha.update"),
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
}
