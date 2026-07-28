using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Identity;

// Refactor (iter52/issue-895-provider-coverage-contract):
//   Old pattern: New current-state readmodels added ad-hoc without enforced activation provider coverage; provider creation was a convention only.
//   New principle: CI guard requires every new current-state readmodel to have an associated IProjectionActivationPlanProvider implementation + DI + test, or an explicit [ProjectionExempt] classification.
public sealed class ChannelIdentityCommittedStateProjectionActivationPlanProvider
    : IProjectionActivationPlanProvider
{
    internal const string ExternalIdentityBindingProjectionKind = "external-identity-binding";
    internal const string AevatarOAuthClientProjectionKind = "aevatar-oauth-client";
    internal const string ManagedCodexCredentialProjectionKind = "managed-codex-credential";

    // Refactor (iter71/cluster-071-identity-projection-rebuild-events):
    //   Old pattern: emit no-op ProjectionRebuildRequested event in command handler to trigger projection materialization
    //   New principle: Identity actor only persists real identity facts; projection materialization owned by projection lifecycle/materializer/bootstrap
    // Refactor (iter97/cluster-097): Old pattern: identity no-op command
    // branches called a hidden committed-state activation service that
    // side-read the event store and dispatched projection envelopes. New
    // principle: only the committed-state publication hook requests these
    // activation plans; drift repair is explicit maintenance/admin work.
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
            var type when type == typeof(ManagedCodexCredentialGAgent) &&
                          IsManagedCodexCredentialEvent(payload) =>
            [
                DurablePlan<ManagedCodexCredentialMaterializationRuntimeLease>(
                    context.ActorId,
                    ManagedCodexCredentialProjectionKind),
            ],
            _ => [],
        };
    }

    private static bool IsExternalIdentityBindingEvent(Any payload) =>
        payload.Is(ExternalIdentityBoundEvent.Descriptor) ||
        payload.Is(ExternalIdentityBindingReplacedEvent.Descriptor) ||
        payload.Is(ExternalIdentityBindingRetirementQueuedEvent.Descriptor) ||
        payload.Is(ExternalIdentityBindingRetiredEvent.Descriptor) ||
        payload.Is(ExternalIdentityBindingRevokedEvent.Descriptor);

    private static bool IsAevatarOAuthClientEvent(Any payload) =>
        payload.Is(AevatarOAuthClientProvisionedEvent.Descriptor) ||
        payload.Is(AevatarOAuthClientHmacKeyRotatedEvent.Descriptor) ||
        payload.Is(AevatarOAuthClientBrokerCapabilityObservedEvent.Descriptor);

    private static bool IsManagedCodexCredentialEvent(Any payload) =>
        payload.Is(ManagedCodexCredentialProvisionedEvent.Descriptor) ||
        payload.Is(ManagedCodexCredentialRotatedEvent.Descriptor) ||
        payload.Is(ManagedCodexCredentialPolicyReconciledEvent.Descriptor) ||
        payload.Is(ManagedCodexCredentialReadinessConfirmedEvent.Descriptor) ||
        payload.Is(ManagedCodexCredentialRevokedEvent.Descriptor) ||
        payload.Is(ManagedCodexCredentialCleanupQueuedEvent.Descriptor) ||
        payload.Is(ManagedCodexCredentialCleanupTrackCompletedEvent.Descriptor);

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
