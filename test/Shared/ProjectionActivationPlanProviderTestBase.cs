using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aevatar.Testing;

// Refactor (iter52/issue-895-provider-coverage-contract):
//   Old pattern: New current-state readmodels added ad-hoc without enforced activation provider coverage; provider creation was a convention only.
//   New principle: CI guard requires every new current-state readmodel to have an associated IProjectionActivationPlanProvider implementation + DI + test, or an explicit [ProjectionExempt] classification.
public abstract class ProjectionActivationPlanProviderTestBase
{
    protected static CommittedStatePublicationContext BuildCommittedStateContext(
        System.Type actorType,
        IMessage payload,
        string actorId,
        IMessage? stateRoot = null,
        long version = 1)
    {
        ArgumentNullException.ThrowIfNull(actorType);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new CommittedStatePublicationContext
        {
            ActorId = actorId,
            ActorType = actorType,
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = actorId,
                    EventId = "evt-1",
                    Version = version,
                    EventData = Any.Pack(payload),
                },
                StateRoot = Any.Pack(stateRoot ?? new StringValue { Value = "state-root" }),
            },
        };
    }

    protected static void AssertDurablePlan(
        ProjectionActivationPlan plan,
        System.Type leaseType,
        string rootActorId,
        string projectionKind,
        string sessionId = "")
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(leaseType);

        Assert.Equal(leaseType, plan.LeaseType);
        Assert.Equal(rootActorId, plan.StartRequest.RootActorId);
        Assert.Equal(projectionKind, plan.StartRequest.ProjectionKind);
        Assert.Equal(ProjectionRuntimeMode.DurableMaterialization, plan.StartRequest.Mode);
        Assert.Equal(sessionId, plan.StartRequest.SessionId);
    }
}
