using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Core.Runtime;
using Aevatar.Foundation.Projection.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Tests.Runtime;

public sealed class RuntimeFleetCapabilityCommittedStateProjectionActivationPlanProviderTests
{
    [Fact]
    public void GetPlans_ShouldMapAuthorityCommitToCurrentStateMaterializationScope()
    {
        var provider = new RuntimeFleetCapabilityCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            typeof(RuntimeFleetCapabilityAuthorityGAgent),
            new RuntimeFleetCapabilityGateOpenedEvent())).ToArray();

        plans.Should().ContainSingle();
        plans[0].LeaseType.Should().Be(typeof(RuntimeFleetCapabilityProjectionRuntimeLease));
        plans[0].StartRequest.RootActorId.Should().Be(RuntimeFleetCapabilityAuthorityIdentity.ActorId);
        plans[0].StartRequest.ProjectionKind.Should().Be(RuntimeFleetCapabilityProjectionKinds.AuthorityCurrentState);
        plans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
        plans[0].StartRequest.SessionId.Should().BeEmpty();
    }

    [Fact]
    public void GetPlans_ShouldIgnoreOtherActorTypes()
    {
        var provider = new RuntimeFleetCapabilityCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildContext(typeof(string), new RuntimeFleetCapabilityGateOpenedEvent()))
            .Should().BeEmpty();
    }

    private static CommittedStatePublicationContext BuildContext(System.Type actorType, IMessage evt) =>
        new()
        {
            ActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            ActorType = actorType,
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                    EventId = "evt-1",
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new StringValue { Value = "state" }),
            },
        };
}
