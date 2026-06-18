using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Testing;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ConversationDeliveryCommittedStateProjectionActivationPlanProviderTests
    : ProjectionActivationPlanProviderTestBase
{
    [Fact]
    public void GetPlans_ShouldMapConversationDeliveryProducedEvent()
    {
        var provider = new ConversationDeliveryCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildCommittedStateContext(
            typeof(ConversationGAgent),
            new DeliveryProducedEvent(),
            "conversation-actor-1")).ToArray();

        plans.Should().ContainSingle();
        AssertDurablePlan(
            plans[0],
            typeof(ConversationDeliveryMaterializationRuntimeLease),
            "conversation-actor-1",
            ConversationDeliveryCommittedStateProjectionActivationPlanProvider.ProjectionKind);
    }

    [Fact]
    public void GetPlans_ShouldIgnoreUnrelatedActorOrMissingPayload()
    {
        var provider = new ConversationDeliveryCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildCommittedStateContext(
                typeof(string),
                new DeliveryProducedEvent(),
                "conversation-actor-1"))
            .Should().BeEmpty();
        provider.GetPlans(new()
            {
                ActorId = "conversation-actor-1",
                ActorType = typeof(ConversationGAgent),
                Published = new(),
            })
            .Should().BeEmpty();
        provider.GetPlans(BuildCommittedStateContext(
                typeof(ConversationGAgent),
                new Empty(),
                "conversation-actor-1"))
            .Should().BeEmpty();
    }
}
