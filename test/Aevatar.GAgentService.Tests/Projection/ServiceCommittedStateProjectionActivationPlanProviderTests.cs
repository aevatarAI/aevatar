using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Reflection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceCommittedStateProjectionActivationPlanProviderTests
{
    [Theory]
    [MemberData(nameof(ServiceDefinitionCatalogEvents))]
    public void GetPlans_ShouldMapServiceDefinitionEventsToCatalogScope(IMessage serviceDefinitionEvent)
    {
        var provider = new ServiceCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            typeof(ServiceDefinitionGAgent),
            serviceDefinitionEvent)).ToArray();

        plans.Should().ContainSingle();
        plans[0].LeaseType.Should().Be(typeof(ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>));
        plans[0].StartRequest.RootActorId.Should().Be("service-actor");
        plans[0].StartRequest.ProjectionKind.Should().Be("service-catalog");
        plans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    public static IEnumerable<object[]> ServiceDefinitionCatalogEvents()
    {
        yield return [new ServiceDefinitionCreatedEvent { Spec = new ServiceDefinitionSpec { Identity = Identity() } }];
        yield return [new ServiceDefinitionUpdatedEvent { Spec = new ServiceDefinitionSpec { Identity = Identity() } }];
        yield return [new ServiceRegistrationRequestedEvent { Identity = Identity(), DesiredSpecHash = "hash-1", Attempt = 1 }];
        yield return [new ServiceRegistrationAttemptStartedEvent { Identity = Identity(), DesiredSpecHash = "hash-1", Attempt = 1 }];
        yield return [new ServiceRegistrationSucceededEvent { Identity = Identity(), NyxidServiceId = "svc-1", NyxidSlug = "orders", DesiredSpecHash = "hash-1", RegisteredSpecHash = "hash-1", Attempt = 1 }];
        yield return [new ServiceRegistrationFailedEvent { Identity = Identity(), DesiredSpecHash = "hash-1", LastError = "Transient:timeout", Attempt = 1 }];
        yield return [new ServiceRegistrationRetiredEvent { Identity = Identity(), NyxidServiceId = "svc-1", NyxidSlug = "orders", Attempt = 1 }];
        yield return [new DefaultServingRevisionChangedEvent { Identity = Identity(), RevisionId = "r1" }];
    }

    [Fact]
    public void GetPlans_ShouldNotMapLegacyExternalExposureUpdatedEventToCatalogScope()
    {
        var provider = new ServiceCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            typeof(ServiceDefinitionGAgent),
            new ServiceExternalExposureUpdatedEvent
            {
                Identity = Identity(),
                ExternalExposure = new ExternalExposure { NyxidSlug = "legacy" },
            })).ToArray();

        plans.Should().BeEmpty();
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
    public void GetPlans_ShouldMapInvocationCatalogEventsToInvocationCatalogScope()
    {
        var provider = new ServiceCommittedStateProjectionActivationPlanProvider();

        var plan = provider.GetPlans(BuildContext(
                typeof(ServiceInvocationCatalogGAgent),
                new ServiceInvocationCatalogObservedEvent { Identity = Identity() }))
            .Should().ContainSingle().Subject;

        plan.LeaseType.Should().Be(typeof(ServiceProjectionRuntimeLease<ServiceInvocationCatalogProjectionContext>));
        plan.StartRequest.RootActorId.Should().Be("service-actor");
        plan.StartRequest.ProjectionKind.Should().Be("service-invocation-catalog");
        plan.StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
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

        var plans = new[]
        {
            "[[AEVATAR_LLM_ERROR]] approval_continuation_failed: missing reply",
            "[[AEVATAR_LLM_ERROR]] approval_denied: denied",
            "[[AEVATAR_LLM_ERROR]] approval_timeout: timed out",
        }.Select(content => provider.GetPlans(BuildContext(
                    typeof(TestRoleGAgent),
                    new RoleChatSessionCompletedEvent
                    {
                        SessionId = "session-1",
                        Content = content,
                    },
                    sourceCorrelationId: " corr-approval "))
                .Should().ContainSingle().Subject)
            .ToArray();

        plans.Should().OnlyContain(plan =>
            plan.LeaseType == typeof(ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>) &&
            plan.StartRequest.ProjectionKind == "gagent-run-terminal-approval" &&
            plan.StartRequest.SessionId == "corr-approval");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetPlans_ShouldIgnoreRoleChatSessionCompletedWithoutCorrelationId(string? correlationId)
    {
        var provider = new ServiceCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildContext(
                typeof(TestRoleGAgent),
                new RoleChatSessionCompletedEvent { SessionId = "session-1", Content = "done" },
                sourceCorrelationId: correlationId))
            .Should().BeEmpty();
    }

    [Fact]
    public void GAgentRunTerminalPlans_ShouldDefensivelyIgnoreNonCompletedPayload()
    {
        var method = typeof(ServiceCommittedStateProjectionActivationPlanProvider)
            .GetMethod("GAgentRunTerminalPlans", BindingFlags.NonPublic | BindingFlags.Static)
            .Should().NotBeNull().And.Subject!;

        var plans = method.Invoke(null, [BuildContext(
            typeof(TestRoleGAgent),
            new StringValue { Value = "not-completed" },
            sourceCorrelationId: "corr-1")]);

        plans.Should().BeAssignableTo<IEnumerable<ProjectionActivationPlan>>()
            .Which.Should().BeEmpty();
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
        string? sourceCorrelationId = "") =>
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
            SourceEnvelope = sourceCorrelationId == null
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
