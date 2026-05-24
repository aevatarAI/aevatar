using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.Testing;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class ChannelIdentityCommittedStateProjectionActivationPlanProviderTests
    : ProjectionActivationPlanProviderTestBase
{
    // Refactor (iter71/cluster-071-identity-projection-rebuild-events):
    //   Old pattern: emit no-op ProjectionRebuildRequested event in command handler to trigger projection materialization
    //   New principle: Identity actor only persists real identity facts; projection materialization owned by projection lifecycle/materializer/bootstrap
    [Fact]
    public void GetPlans_ShouldMapExternalIdentityBindingActor()
    {
        var provider = new ChannelIdentityCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildCommittedStateContext(
            typeof(ExternalIdentityBindingGAgent),
            new ExternalIdentityBoundEvent(),
            "external-identity-binding:lark:t:u")).ToArray();

        plans.Should().ContainSingle();
        AssertDurablePlan(
            plans[0],
            typeof(ExternalIdentityBindingMaterializationRuntimeLease),
            "external-identity-binding:lark:t:u",
            "external-identity-binding");
    }

    [Fact]
    public void GetPlans_ShouldMapOAuthClientActor()
    {
        var provider = new ChannelIdentityCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildCommittedStateContext(
            typeof(AevatarOAuthClientGAgent),
            new AevatarOAuthClientProvisionedEvent(),
            AevatarOAuthClientGAgent.WellKnownId)).ToArray();

        plans.Should().ContainSingle();
        AssertDurablePlan(
            plans[0],
            typeof(AevatarOAuthClientMaterializationRuntimeLease),
            AevatarOAuthClientGAgent.WellKnownId,
            "aevatar-oauth-client");
    }

    [Fact]
    public void GetPlans_ShouldIgnoreUnrelatedActorOrMissingPayload()
    {
        var provider = new ChannelIdentityCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildCommittedStateContext(
                typeof(string),
                new ExternalIdentityBoundEvent(),
                "external-identity-binding:lark:t:u"))
            .Should().BeEmpty();
        provider.GetPlans(new()
            {
                ActorId = "external-identity-binding:lark:t:u",
                ActorType = typeof(ExternalIdentityBindingGAgent),
                Published = new(),
            })
            .Should().BeEmpty();
    }
}
