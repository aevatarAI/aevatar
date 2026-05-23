using Aevatar.GAgents.Device;
using Aevatar.Testing;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class DeviceRegistrationCommittedStateProjectionActivationPlanProviderTests
    : ProjectionActivationPlanProviderTestBase
{
    [Fact]
    public void GetPlans_ShouldMapDeviceRegistrationActor()
    {
        var provider = new DeviceRegistrationCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildCommittedStateContext(
            typeof(DeviceRegistrationGAgent),
            new DeviceRegisteredEvent(),
            DeviceRegistrationGAgent.WellKnownId)).ToArray();

        plans.Should().ContainSingle();
        AssertDurablePlan(
            plans[0],
            typeof(DeviceRegistrationMaterializationRuntimeLease),
            DeviceRegistrationGAgent.WellKnownId,
            DeviceRegistrationProjectionBootstrapActivator.ProjectionKind);
    }

    [Fact]
    public void GetPlans_ShouldIgnoreUnrelatedActorOrMissingPayload()
    {
        var provider = new DeviceRegistrationCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildCommittedStateContext(
                typeof(string),
                new DeviceRegisteredEvent(),
                DeviceRegistrationGAgent.WellKnownId))
            .Should().BeEmpty();
        provider.GetPlans(new()
            {
                ActorId = DeviceRegistrationGAgent.WellKnownId,
                ActorType = typeof(DeviceRegistrationGAgent),
                Published = new(),
            })
            .Should().BeEmpty();
    }
}
