using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Identity;

// Refactor (iter52/issue-895-provider-coverage-contract):
//   Old pattern: New current-state readmodels added ad-hoc without enforced activation provider coverage; provider creation was a convention only.
//   New principle: CI guard requires every new current-state readmodel to have an associated IProjectionActivationPlanProvider implementation + DI + test, or an explicit [ProjectionExempt] classification.
public sealed class ChannelIdentityCommittedStateProjectionActivationPlanProvider
    : IProjectionActivationPlanProvider
{
    private const string ExternalIdentityBindingProjectionKind = "external-identity-binding";
    private const string AevatarOAuthClientProjectionKind = "aevatar-oauth-client";

    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var payload = context.Published.StateEvent?.EventData;
        if (payload == null)
            return [];

        return context.ActorType switch
        {
            var type when type == typeof(ExternalIdentityBindingGAgent) &&
                          IsExternalIdentityBindingEvent(payload) =>
            [
                DurablePlan<ExternalIdentityBindingMaterializationRuntimeLease>(
                    context.ActorId,
                    ExternalIdentityBindingProjectionKind),
            ],
            var type when type == typeof(AevatarOAuthClientGAgent) &&
                          IsAevatarOAuthClientEvent(payload) =>
            [
                DurablePlan<AevatarOAuthClientMaterializationRuntimeLease>(
                    context.ActorId,
                    AevatarOAuthClientProjectionKind),
            ],
            _ => [],
        };
    }

    private static bool IsExternalIdentityBindingEvent(Any payload) =>
        payload.Is(ExternalIdentityBoundEvent.Descriptor) ||
        payload.Is(ExternalIdentityBindingRevokedEvent.Descriptor) ||
        payload.Is(ExternalIdentityBindingProjectionRebuildRequestedEvent.Descriptor);

    private static bool IsAevatarOAuthClientEvent(Any payload) =>
        payload.Is(AevatarOAuthClientProvisionedEvent.Descriptor) ||
        payload.Is(AevatarOAuthClientHmacKeyRotatedEvent.Descriptor) ||
        payload.Is(AevatarOAuthClientBrokerCapabilityObservedEvent.Descriptor) ||
        payload.Is(AevatarOAuthClientProjectionRebuildRequestedEvent.Descriptor);

    private static ProjectionActivationPlan DurablePlan<TLease>(
        string actorId,
        string projectionKind) =>
        new()
        {
            LeaseType = typeof(TLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = projectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };
}
