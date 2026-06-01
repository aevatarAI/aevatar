using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Testing;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelBotRegistrationCommittedStateProjectionActivationPlanProviderTests
    : ProjectionActivationPlanProviderTestBase
{
    [Fact]
    public void GetPlans_ShouldMapChannelBotRegistrationActor()
    {
        var provider = new ChannelBotRegistrationCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildCommittedStateContext(
            typeof(ChannelBotRegistrationGAgent),
            new ChannelBotRegisteredEvent(),
            ChannelBotRegistrationGAgent.WellKnownId)).ToArray();

        plans.Should().ContainSingle();
        AssertDurablePlan(
            plans[0],
            typeof(ChannelBotRegistrationMaterializationRuntimeLease),
            ChannelBotRegistrationGAgent.WellKnownId,
            ChannelBotRegistrationProjectionBootstrapActivator.ProjectionKind);
    }

    [Fact]
    public void GetPlans_ShouldIgnoreUnrelatedActorOrMissingPayload()
    {
        var provider = new ChannelBotRegistrationCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildCommittedStateContext(
                typeof(string),
                new ChannelBotRegisteredEvent(),
                ChannelBotRegistrationGAgent.WellKnownId))
            .Should().BeEmpty();
        provider.GetPlans(new()
            {
                ActorId = ChannelBotRegistrationGAgent.WellKnownId,
                ActorType = typeof(ChannelBotRegistrationGAgent),
                Published = new(),
            })
            .Should().BeEmpty();
    }
}
