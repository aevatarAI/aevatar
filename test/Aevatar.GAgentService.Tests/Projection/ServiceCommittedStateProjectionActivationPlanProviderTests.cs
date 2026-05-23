using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.AI.Abstractions;
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
    public void GetPlans_ShouldMapRoleChatSessionCompletedToGAgentRunTerminalScope()
    {
        var provider = new ServiceCommittedStateProjectionActivationPlanProvider();

        var plan = provider.GetPlans(BuildContext(
                typeof(TestRoleGAgent),
                new RoleChatSessionCompletedEvent { SessionId = "session-1", Content = "done" },
                sourceCorrelationId: "corr-1"))
            .Should().ContainSingle().Subject;

        plan.LeaseType.Should().Be(typeof(ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>));
        plan.StartRequest.RootActorId.Should().Be("service-actor");
        plan.StartRequest.ProjectionKind.Should().Be("gagent-run-terminal-draft-run");
        plan.StartRequest.SessionId.Should().Be("corr-1");
        plan.StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public void GetPlans_ShouldMapApprovalTerminalCompletionToApprovalTerminalScope()
    {
        var provider = new ServiceCommittedStateProjectionActivationPlanProvider();

        var plan = provider.GetPlans(BuildContext(
                typeof(TestRoleGAgent),
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "session-1",
                    Content = "[[AEVATAR_LLM_ERROR]] approval_denied: denied",
                },
                sourceCorrelationId: "corr-approval"))
            .Should().ContainSingle().Subject;

        plan.LeaseType.Should().Be(typeof(ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>));
        plan.StartRequest.ProjectionKind.Should().Be("gagent-run-terminal-approval");
        plan.StartRequest.SessionId.Should().Be("corr-approval");
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

    private static CommittedStatePublicationContext BuildContext(
        System.Type actorType,
        IMessage evt,
        string sourceCorrelationId = "") =>
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
            SourceEnvelope = string.IsNullOrWhiteSpace(sourceCorrelationId)
                ? null
                : new EventEnvelope
                {
                    Id = "source-evt-1",
                    Propagation = new EnvelopePropagation
                    {
                        CorrelationId = sourceCorrelationId,
                    },
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

    private sealed class TestRoleGAgent;
}
