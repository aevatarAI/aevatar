using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StatusDashboard.Tests;

// Refactor (iter47/cluster-005-status-dashboard-startup-projection-activation):
//   Old pattern: Startup service explicitly ensures projection scopes and uses Task.Delay retry before dispatching configure commands.
//   New principle: Startup path dispatches actor configuration only; projection activation owned by committed-state hooks; retry uses hosted-service scheduling.
public sealed class HealthProbeCommittedStateProjectionActivationPlanProviderTests
{
    [Theory]
    [MemberData(nameof(HealthProbeEvents))]
    public void GetPlans_ShouldMapHealthProbeCommittedStateEventsToDurableMaterializationScope(IMessage evt)
    {
        var provider = new HealthProbeCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(typeof(HealthProbeTargetGAgent), evt)).ToArray();

        plans.Should().ContainSingle();
        plans[0].LeaseType.Should().Be(typeof(HealthProbeMaterializationRuntimeLease));
        plans[0].StartRequest.RootActorId.Should().Be("health-probe::self-liveness");
        plans[0].StartRequest.ProjectionKind.Should().Be(HealthProbeTargetGAgent.ProjectionKind);
        plans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public void GetPlans_ShouldIgnoreUnrelatedActorsAndEvents()
    {
        var provider = new HealthProbeCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildContext(typeof(HealthProbeTargetGAgent), new StringValue { Value = "not-health" }))
            .Should().BeEmpty();
        provider.GetPlans(BuildContext(typeof(string), BuildConfiguredEvent()))
            .Should().BeEmpty();
    }

    [Fact]
    public void GetPlans_ShouldIgnoreMissingStateEventPayload()
    {
        var provider = new HealthProbeCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildContextWithoutStateEvent()).Should().BeEmpty();
        provider.GetPlans(BuildContextWithoutEventData()).Should().BeEmpty();
    }

    public static TheoryData<IMessage> HealthProbeEvents() =>
        new()
        {
            BuildConfiguredEvent(),
            new HealthProbeObserved
            {
                Outcome = new HealthProbeOutcome
                {
                    Status = HealthOutcomeStatus.Ok,
                    ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            },
        };

    private static HealthProbeConfigured BuildConfiguredEvent() =>
        new()
        {
            Spec = new HealthProbeTargetDescriptor
            {
                Slug = "self-liveness",
                DisplayName = "Self liveness",
                Category = "self",
                ProbeKind = "test",
                IntervalSeconds = 60,
                TimeoutMs = 1_000,
                Enabled = true,
            },
            ConfiguredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

    private static CommittedStatePublicationContext BuildContext(System.Type actorType, IMessage evt) =>
        new()
        {
            ActorId = "health-probe::self-liveness",
            ActorType = actorType,
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = "health-probe::self-liveness",
                    EventId = "evt-1",
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new StringValue { Value = "state" }),
            },
        };

    private static CommittedStatePublicationContext BuildContextWithoutStateEvent() =>
        new()
        {
            ActorId = "health-probe::self-liveness",
            ActorType = typeof(HealthProbeTargetGAgent),
            Published = new CommittedStateEventPublished
            {
                StateRoot = Any.Pack(new StringValue { Value = "state" }),
            },
        };

    private static CommittedStatePublicationContext BuildContextWithoutEventData() =>
        new()
        {
            ActorId = "health-probe::self-liveness",
            ActorType = typeof(HealthProbeTargetGAgent),
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = "health-probe::self-liveness",
                    EventId = "evt-1",
                },
                StateRoot = Any.Pack(new StringValue { Value = "state" }),
            },
        };
}
