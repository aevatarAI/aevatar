using Aevatar.AI.Abstractions;
using Aevatar.AI.Core;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
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
    public void GetPlans_ShouldMapSessionBearingCommittedEventsToSessionObservation(IMessage stateEvent)
    {
        var provider = new NyxIdChatCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext("conv-a", typeof(NyxIdChatGAgent), stateEvent))
            .ToArray();

        plans.Should().ContainSingle();
        plans[0].LeaseType.Should().Be(typeof(NyxIdChatSessionRuntimeLease));
        plans[0].StartRequest.RootActorId.Should().Be("conv-a");
        plans[0].StartRequest.ProjectionKind.Should().Be(NyxIdChatProjectionKinds.ChatSession);
        plans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.SessionObservation);
        plans[0].StartRequest.SessionId.Should().Be("session-1");
    }

    [Fact]
    public void GetPlans_ShouldTrimSessionIdFromCommittedEvent()
    {
        var provider = new NyxIdChatCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
                "conv-a",
                typeof(NyxIdChatGAgent),
                new RoleChatSessionStartedEvent { SessionId = "  session-9  " }))
            .ToArray();

        plans.Should().ContainSingle();
        plans[0].StartRequest.SessionId.Should().Be("session-9");
    }

    [Fact]
    public void GetPlans_ShouldSkipNonNyxIdChatActors()
    {
        var provider = new NyxIdChatCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            "role-a",
            typeof(RoleGAgent),
            new RoleChatSessionStartedEvent { SessionId = "session-1" }));

        plans.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(SessionlessStateEvents))]
    public void GetPlans_ShouldSkipCommittedEventsWithoutSessionId(IMessage stateEvent)
    {
        var provider = new NyxIdChatCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext("conv-a", typeof(NyxIdChatGAgent), stateEvent));

        plans.Should().BeEmpty();
    }

    [Fact]
    public void GetPlans_ShouldSkipPublicationsWithoutStateEventData()
    {
        var provider = new NyxIdChatCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(new CommittedStatePublicationContext
        {
            ActorId = "conv-a",
            ActorType = typeof(NyxIdChatGAgent),
            Published = new CommittedStateEventPublished
            {
                StateRoot = Any.Pack(new RoleGAgentState()),
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

    public static IEnumerable<object[]> SessionBearingStateEvents()
    {
        yield return
        [
            new RoleChatSessionStartedEvent
            {
                SessionId = "session-1",
                Prompt = "hello",
            },
        ];
        yield return
        [
            new RoleChatSessionCompletedEvent
            {
                SessionId = "session-1",
                Content = "done",
            },
        ];
        yield return
        [
            new PendingToolApprovalPersistedEvent
            {
                Pending = new PendingToolApprovalState
                {
                    RequestId = "req-1",
                    SessionId = "session-1",
                    ToolName = "lark_messages_send",
                },
            },
        ];
    }

    public static IEnumerable<object[]> SessionlessStateEvents()
    {
        yield return [new RoleChatSessionStartedEvent { Prompt = "hello" }];
        yield return [new ClearPendingApprovalEvent { RequestId = "req-1" }];
        yield return [new PendingToolApprovalPersistedEvent()];
        yield return
        [
            new NyxIdChatConversationCreationStartedEvent
            {
                ScopeId = "scope-a",
                ActorId = "conv-a",
            },
        ];
        yield return [new InitializeRoleAgentEvent { RoleName = "assistant" }];
    }

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
                StateRoot = Any.Pack(new RoleGAgentState()),
            },
        };
}
