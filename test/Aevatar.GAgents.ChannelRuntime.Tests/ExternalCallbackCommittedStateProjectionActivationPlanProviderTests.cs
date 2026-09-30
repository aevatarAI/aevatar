using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Testing;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ExternalCallbackCommittedStateProjectionActivationPlanProviderTests : ProjectionActivationPlanProviderTestBase
{
    [Fact]
    public void CommittedOperationState_ActivatesDurableMaterializer()
    {
        var plans = new ExternalCallbackCommittedStateProjectionActivationPlanProvider().GetPlans(
            BuildCommittedStateContext(typeof(ExternalCallbackGAgent), new ExternalCallbackStateChanged(), "opaque-operation"));
        AssertDurablePlan(plans.Single(), typeof(ExternalCallbackMaterializationLease), "opaque-operation",
            ExternalCallbackCommittedStateProjectionActivationPlanProvider.ProjectionKind);
    }

    [Fact]
    public void OtherActorAndCommand_DoNotActivateProjection()
    {
        var provider = new ExternalCallbackCommittedStateProjectionActivationPlanProvider();
        provider.GetPlans(BuildCommittedStateContext(typeof(ConversationGAgent), new ExternalCallbackStateChanged(), "conversation"))
            .Should().BeEmpty();
        provider.GetPlans(BuildCommittedStateContext(typeof(ExternalCallbackGAgent), new RegisterExternalCallback(), "operation"))
            .Should().BeEmpty();
        provider.GetPlans(BuildCommittedStateContext(typeof(ExternalCallbackGAgent), new Empty(), "operation"))
            .Should().BeEmpty();
    }
}
