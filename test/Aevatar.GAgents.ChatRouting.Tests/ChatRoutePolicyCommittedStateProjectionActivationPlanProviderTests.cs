using Aevatar.ChatRouting.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChatRouting.Tests;

// Refactor (iter32/cluster-034-chat-route-policy-request-path-projection-activation):
//   Old pattern: Chat route policy admin endpoints + voice demo bootstrap 在 request path 调 EnsureProjectionForActorAsync 同步 priming projection,违反 query-time priming forbidden + 命令骨架内聚
//   New principle: 加 ChatRoutePolicyCommittedStateProjectionActivationPlanProvider(committed-state hook 触发);删 ChatRoutePolicyProjectionPort + request-path activation;DI 注册 dispatcher + hook + provider;query_projection_priming_guard 加 chat route policy endpoint 扫描
public sealed class ChatRoutePolicyCommittedStateProjectionActivationPlanProviderTests
{
    [Fact]
    public void GetPlans_ShouldMapChatRoutePolicyUpdatedToDurableMaterializationScope()
    {
        var provider = new ChatRoutePolicyCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            typeof(ChatRoutePolicyGAgent),
            new ChatRoutePolicyUpdated
            {
                State = new ChatRoutePolicyState { PolicyId = "chat-route-policy:scope-1" },
            })).ToArray();

        plans.Should().ContainSingle();
        plans[0].LeaseType.Should().Be(typeof(ChatRoutePolicyMaterializationRuntimeLease));
        plans[0].StartRequest.RootActorId.Should().Be("chat-route-policy:scope-1");
        plans[0].StartRequest.ProjectionKind.Should().Be(ChatRoutePolicyGAgent.ProjectionKind);
        plans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public void GetPlans_ShouldNotMatchUnrelatedActorOrStateEvent()
    {
        var provider = new ChatRoutePolicyCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildContext(typeof(ChatRoutePolicyGAgent), new StringValue { Value = "not-policy" }))
            .Should().BeEmpty();
        provider.GetPlans(BuildContext(typeof(string), new ChatRoutePolicyUpdated { State = new ChatRoutePolicyState() }))
            .Should().BeEmpty();
    }

    private static CommittedStatePublicationContext BuildContext(System.Type actorType, IMessage evt) =>
        new()
        {
            ActorId = "chat-route-policy:scope-1",
            ActorType = actorType,
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = "chat-route-policy:scope-1",
                    EventId = "evt-1",
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new StringValue { Value = "state" }),
            },
        };
}
