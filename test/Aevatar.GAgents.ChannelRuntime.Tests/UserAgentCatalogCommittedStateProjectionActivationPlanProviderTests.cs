using Aevatar.GAgents.Scheduled;
using Aevatar.Testing;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogCommittedStateProjectionActivationPlanProviderTests
    : ProjectionActivationPlanProviderTestBase
{
    [Fact]
    public void GetPlans_ShouldMapCatalogActor()
    {
        var provider = new UserAgentCatalogCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildCommittedStateContext(
            typeof(UserAgentCatalogGAgent),
            new UserAgentCatalogUpsertedEvent(),
            UserAgentCatalogGAgent.WellKnownId)).ToArray();

        plans.Should().ContainSingle();
        AssertDurablePlan(
            plans[0],
            typeof(UserAgentCatalogMaterializationRuntimeLease),
            UserAgentCatalogGAgent.WellKnownId,
            UserAgentCatalogProjectionBootstrapActivator.ProjectionKind);
    }

    [Fact]
    public void GetPlans_ShouldIgnoreUnrelatedActorOrMissingPayload()
    {
        var provider = new UserAgentCatalogCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildCommittedStateContext(
                typeof(string),
                new UserAgentCatalogUpsertedEvent(),
                UserAgentCatalogGAgent.WellKnownId))
            .Should().BeEmpty();
        provider.GetPlans(new()
            {
                ActorId = UserAgentCatalogGAgent.WellKnownId,
                ActorType = typeof(UserAgentCatalogGAgent),
                Published = new(),
            })
            .Should().BeEmpty();
    }
}
