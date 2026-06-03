using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Core.GAgents;
using Aevatar.GAgentService.Governance.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceGovernanceCommittedStateProjectionActivationPlanProviderTests
{
    [Fact]
    public void GetPlans_ShouldMapConfigurationEventsToConfigurationScope()
    {
        var provider = new ServiceGovernanceCommittedStateProjectionActivationPlanProvider();

        var plan = provider.GetPlans(BuildContext(
            typeof(ServiceConfigurationGAgent),
            new ServiceBindingCreatedEvent
            {
                Spec = new ServiceBindingSpec
                {
                    Identity = Identity(),
                    BindingId = "binding-1",
                },
            })).Should().ContainSingle().Subject;

        plan.LeaseType.Should().Be(typeof(ServiceConfigurationRuntimeLease));
        plan.StartRequest.RootActorId.Should().Be("configuration-actor");
        plan.StartRequest.ProjectionKind.Should().Be("service-configuration");
        plan.StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public void GetPlans_ShouldNotMatchUnrelatedActorOrStateEvent()
    {
        var provider = new ServiceGovernanceCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildContext(typeof(ServiceConfigurationGAgent), new StringValue { Value = "not-governance" }))
            .Should().BeEmpty();
        provider.GetPlans(BuildContext(typeof(string), new ServiceBindingCreatedEvent
        {
            Spec = new ServiceBindingSpec
            {
                Identity = Identity(),
                BindingId = "binding-1",
            },
        })).Should().BeEmpty();
    }

    private static CommittedStatePublicationContext BuildContext(System.Type actorType, IMessage evt) =>
        new()
        {
            ActorId = "configuration-actor",
            ActorType = actorType,
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = "configuration-actor",
                    EventId = "evt-1",
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new StringValue { Value = "state" }),
            },
        };

    private static ServiceIdentity Identity() =>
        new()
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = "service",
        };
}
