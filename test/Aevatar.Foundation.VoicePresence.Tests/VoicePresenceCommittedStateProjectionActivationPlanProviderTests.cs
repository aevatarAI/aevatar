using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence;
using Aevatar.Foundation.VoicePresence.Projection;
using Aevatar.Testing;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public sealed class VoicePresenceCommittedStateProjectionActivationPlanProviderTests
    : ProjectionActivationPlanProviderTestBase
{
    [Fact]
    public void GetPlans_ShouldPlanCapabilityMaterialization()
    {
        var provider = new VoicePresenceCommittedStateProjectionActivationPlanProvider();

        var plan = provider.GetPlans(BuildCommittedStateContext(
            typeof(object),
            new VoicePresenceRuntimeStateChangedEvent
            {
                ModuleName = "voice_presence",
                State = new VoicePresenceRuntimeState(),
            },
            "agent-1")).ShouldHaveSingleItem();

        AssertDurablePlan(
            plan,
            typeof(VoicePresenceCapabilityMaterializationRuntimeLease),
            "agent-1",
            VoicePresenceProjectionKinds.CapabilityMaterialization);
    }

    [Fact]
    public void GetPlans_ShouldIgnoreMissingPayload()
    {
        var provider = new VoicePresenceCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(new()
            {
                ActorId = "agent-1",
                ActorType = typeof(object),
                Published = new(),
            })
            .ShouldBeEmpty();
    }
}
