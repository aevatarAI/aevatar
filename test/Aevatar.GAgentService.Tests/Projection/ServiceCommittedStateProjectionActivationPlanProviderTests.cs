using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceCommittedStateProjectionActivationPlanProviderTests
{
    [Fact]
    public void GetPlans_ShouldMapServiceDefinitionEventsToCatalogScope()
    {
        var provider = new ServiceCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            typeof(ServiceDefinitionGAgent),
            new ServiceDefinitionCreatedEvent { Spec = new ServiceDefinitionSpec { Identity = Identity() } })).ToArray();

        plans.Should().ContainSingle();
        plans[0].LeaseType.Should().Be(typeof(ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>));
        plans[0].StartRequest.RootActorId.Should().Be("service-actor");
        plans[0].StartRequest.ProjectionKind.Should().Be("service-catalog");
        plans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public void GetPlans_ShouldMapDeploymentEventsToDeploymentAndCatalogScopes()
    {
        var provider = new ServiceCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            typeof(ServiceDeploymentManagerGAgent),
            new ServiceDeploymentActivatedEvent
            {
                Identity = Identity(),
                DeploymentId = "deployment-1",
            })).ToArray();

        plans.Should().HaveCount(2);
        plans.Select(x => x.LeaseType).Should().Equal(
            typeof(ServiceProjectionRuntimeLease<ServiceDeploymentCatalogProjectionContext>),
            typeof(ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>));
        plans.Select(x => x.StartRequest.ProjectionKind).Should().Equal("service-deployments", "service-catalog");
    }

    [Fact]
    public void GetPlans_ShouldMapCurrentStateActorsToTheirCurrentStateScopes()
    {
        var provider = new ServiceCommittedStateProjectionActivationPlanProvider();

        var sessionPlan = provider.GetPlans(BuildContext(
            typeof(LlmSessionGAgent),
            new LlmSessionRegisteredEvent { Record = new LlmSessionRecord { ResponseId = "resp-1" } }))
            .Should().ContainSingle().Subject;
        var toolPlan = provider.GetPlans(BuildContext(
            typeof(ResponsesAgentToolStateGAgent),
            new ResponsesAgentToolStateRegisteredEvent { Record = new ResponsesAgentToolStateRecord { ScopeId = "scope-1" } }))
            .Should().ContainSingle().Subject;

        sessionPlan.LeaseType.Should().Be(typeof(ServiceProjectionRuntimeLease<LlmSessionCurrentStateProjectionContext>));
        sessionPlan.StartRequest.ProjectionKind.Should().Be("response-sessions");
        toolPlan.LeaseType.Should().Be(typeof(ServiceProjectionRuntimeLease<ResponsesAgentToolStateCurrentStateProjectionContext>));
        toolPlan.StartRequest.ProjectionKind.Should().Be("responses-agent-tools");
    }

    [Fact]
    public void GetPlans_ShouldNotMatchUnrelatedActorOrStateEvent()
    {
        var provider = new ServiceCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildContext(typeof(ServiceDefinitionGAgent), new StringValue { Value = "not-service" }))
            .Should().BeEmpty();
        provider.GetPlans(BuildContext(typeof(string), new ServiceDefinitionCreatedEvent { Spec = new ServiceDefinitionSpec { Identity = Identity() } }))
            .Should().BeEmpty();
    }

    private static CommittedStatePublicationContext BuildContext(System.Type actorType, IMessage evt) =>
        new()
        {
            ActorId = "service-actor",
            ActorType = actorType,
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = "service-actor",
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
