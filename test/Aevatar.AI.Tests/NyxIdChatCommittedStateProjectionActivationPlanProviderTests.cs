using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatCommittedStateProjectionActivationPlanProviderTests
{
    [Theory]
    [MemberData(nameof(SessionBearingStateEvents))]
    public void GetPlans_ShouldMapTurnBearingControllerEventsToSessionObservation(IMessage stateEvent)
    {
        var provider = new NyxIdChatCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
                "conv-a",
                typeof(NyxIdChatConversationGAgent),
                stateEvent))
            .ToArray();

        plans.Should().ContainSingle();
        plans[0].LeaseType.Should().Be(typeof(NyxIdChatSessionRuntimeLease));
        plans[0].StartRequest.RootActorId.Should().Be("conv-a");
        plans[0].StartRequest.ProjectionKind.Should().Be(NyxIdChatProjectionKinds.ChatSession);
        plans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.SessionObservation);
        plans[0].StartRequest.SessionId.Should().Be("turn-1");
    }

    [Fact]
    public void GetPlans_ShouldTrimSessionIdFromCommittedEvent()
    {
        var provider = new NyxIdChatCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
                "conv-a",
                typeof(NyxIdChatConversationGAgent),
                new NyxIdChatOperationDispatchedEvent
                {
                    Key = OperationKey("  turn-9  "),
                }))
            .ToArray();

        plans.Should().ContainSingle();
        plans[0].StartRequest.SessionId.Should().Be("turn-9");
    }

    [Fact]
    public void GetPlans_ShouldSkipLegacyNyxIdChatActorEvents()
    {
        var provider = new NyxIdChatCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            "legacy-conv-a",
            typeof(NyxIdChatGAgent),
            new RoleChatSessionStartedEvent { SessionId = "legacy-session-1" }));

        plans.Should().BeEmpty();
    }

    [Fact]
    public void GetPlans_ShouldSkipNonNyxIdChatActors()
    {
        var provider = new NyxIdChatCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            "role-a",
            typeof(NyxIdChatGAgent),
            new NyxIdChatOperationDispatchedEvent { Key = OperationKey("turn-1") }));

        plans.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(SessionlessStateEvents))]
    public void GetPlans_ShouldSkipCommittedEventsWithoutSessionId(IMessage stateEvent)
    {
        var provider = new NyxIdChatCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            "conv-a",
            typeof(NyxIdChatConversationGAgent),
            stateEvent));

        plans.Should().BeEmpty();
    }

    [Fact]
    public void GetPlans_ShouldSkipPublicationsWithoutStateEventData()
    {
        var provider = new NyxIdChatCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(new CommittedStatePublicationContext
        {
            ActorId = "conv-a",
            ActorType = typeof(NyxIdChatConversationGAgent),
            Published = new CommittedStateEventPublished
            {
                StateRoot = Any.Pack(new NyxIdChatConversationGAgentState()),
            },
        });

        plans.Should().BeEmpty();
    }

    [Fact]
    public void AddNyxIdChat_ShouldRegisterCommittedStateActivationProviderInDispatcherChain()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddAevatarRuntime()
            .AddNyxIdChat(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        provider.GetService<ProjectionActivationPlanDispatcher>()
            .Should().NotBeNull("the committed-state hook dispatches provider plans through the shared dispatcher");
        provider.GetServices<ICommittedStatePublicationHook>()
            .Should().ContainSingle(hook => hook is CommittedStateProjectionActivationHook);
        provider.GetServices<IProjectionActivationPlanProvider>()
            .Should().ContainSingle(planProvider =>
                planProvider is NyxIdChatCommittedStateProjectionActivationPlanProvider);
        provider.GetService<IProjectionScopeActivationService<NyxIdChatSessionRuntimeLease>>()
            .Should().NotBeNull("the dispatcher must be able to activate the chat-session observation scope");
    }

    [Fact]
    public void AddNyxIdChat_ShouldRegisterAndConstructResponsiveActorKinds()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddAevatarRuntime()
            .AddSingleton<ILLMProviderFactory>(new StubChatProviderFactory(
                static (_, _) => Task.FromResult(new LLMResponse())))
            .AddSingleton<INyxIdChatTurnOperationExecutor, NoopTurnOperationExecutor>()
            .AddNyxIdChat(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentKindRegistry>();

        registry.TryGetKindForAgentType(typeof(NyxIdChatConversationGAgent), out var conversationKind)
            .Should().BeTrue();
        conversationKind.Should().Be(NyxIdChatServiceDefaults.GAgentKind);
        registry.TryGetKindForAgentType(typeof(NyxIdChatTurnGAgent), out var turnKind)
            .Should().BeTrue();
        turnKind.Should().Be(NyxIdChatServiceDefaults.TurnGAgentKind);
        registry.TryGetKindForAgentType(typeof(NyxIdChatGAgent), out var legacyKind)
            .Should().BeTrue();
        legacyKind.Should().Be(NyxIdChatServiceDefaults.LegacyGAgentKind);

        registry.Resolve(conversationKind).Factory(provider)
            .Should().BeOfType<NyxIdChatConversationGAgent>();
        registry.Resolve(turnKind).Factory(provider)
            .Should().BeOfType<NyxIdChatTurnGAgent>();
    }

    public static IEnumerable<object[]> SessionBearingStateEvents()
    {
        yield return
        [
            new NyxIdChatTurnStartedEvent
            {
                State = new NyxIdChatConversationGAgentState
                {
                    ActiveTurn = new NyxIdChatTurnState { TurnId = "turn-1" },
                },
            },
        ];
        yield return
        [
            new NyxIdChatOperationDispatchedEvent
            {
                Key = OperationKey("turn-1"),
            },
        ];
        yield return
        [
            new NyxIdChatOperationProgressedEvent
            {
                Progress = new NyxIdChatOperationProgressSignal
                {
                    Key = OperationKey("turn-1"),
                    Sequence = 1,
                    Text = new NyxIdChatTextProgress { Delta = "hello" },
                },
            },
        ];
        yield return
        [
            new NyxIdChatOperationReconciledEvent
            {
                Result = new NyxIdChatOperationResultSignal
                {
                    Key = OperationKey("turn-1"),
                    Llm = new NyxIdChatLLMOperationResult { Content = "done" },
                },
            },
        ];
        yield return
        [
            new NyxIdChatLateOperationEvidenceCommittedEvent
            {
                Key = OperationKey("turn-1"),
            },
        ];
        yield return
        [
            new NyxIdChatControlFenceCommittedEvent
            {
                Fence = new NyxIdChatControlFenceState { TurnId = "turn-1" },
            },
        ];
        yield return
        [
            new NyxIdChatActionRequestedEvent
            {
                Request = new NyxIdChatActionRequestState
                {
                    ConversationActorId = "conv-a",
                    OriginTurnId = "turn-1",
                },
            },
        ];
        yield return
        [
            new NyxIdChatContinuationAdmissionCommittedEvent
            {
                Admission = new NyxIdChatContinuationAdmissionState
                {
                    OriginTurnId = "turn-1",
                },
            },
        ];
        yield return
        [
            new NyxIdChatStepControlCommittedEvent
            {
                Result = new NyxIdChatStepControlResultState
                {
                    ConversationActorId = "conv-a",
                    TurnId = "turn-1",
                },
            },
        ];
        yield return
        [
            new NyxIdChatTurnAdmissionRejectedEvent
            {
                ConversationActorId = "conv-a",
                RequestedTurnId = "turn-1",
                ActiveTurnId = "turn-active",
                ReasonCode = NyxIdChatControlCommands.ActiveTurnRequiresSteering,
            },
        ];
    }

    public static IEnumerable<object[]> SessionlessStateEvents()
    {
        yield return [new NyxIdChatTurnStartedEvent()];
        yield return [new NyxIdChatOperationDispatchedEvent()];
        yield return [new NyxIdChatOperationProgressedEvent()];
        yield return [new NyxIdChatOperationReconciledEvent()];
        yield return [new NyxIdChatLateOperationEvidenceCommittedEvent()];
        yield return [new NyxIdChatControlFenceCommittedEvent()];
        yield return [new NyxIdChatActionRequestedEvent()];
        yield return [new NyxIdChatContinuationAdmissionCommittedEvent()];
        yield return [new NyxIdChatStepControlCommittedEvent()];
        yield return [new NyxIdChatTurnAdmissionRejectedEvent()];
        yield return
        [
            new NyxIdChatConversationCreationStartedEvent
            {
                ScopeId = "scope-a",
                ActorId = "conv-a",
            },
        ];
    }

    private static NyxIdChatOperationKey OperationKey(string turnId) => new()
    {
        ConversationActorId = "conv-a",
        TurnId = turnId,
        TaskId = "task-1",
        StepId = "step-1",
        OperationId = "operation-1",
        OperationGeneration = 1,
    };

    private static CommittedStatePublicationContext BuildContext(
        string actorId,
        System.Type actorType,
        IMessage stateEvent) =>
        new()
        {
            ActorId = actorId,
            ActorType = actorType,
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = actorId,
                    EventId = "evt-1",
                    Version = 1,
                    EventType = stateEvent.Descriptor.FullName,
                    EventData = Any.Pack(stateEvent),
                },
                StateRoot = Any.Pack(new NyxIdChatConversationGAgentState()),
            },
        };

    private sealed class NoopTurnOperationExecutor : INyxIdChatTurnOperationExecutor
    {
        public Task<NyxIdChatTurnOperationExecution> ExecuteAsync(
            NyxIdChatOperationDispatchCommand command,
            NyxIdChatTransientExecutionSession session,
            Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
