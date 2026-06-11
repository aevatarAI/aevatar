using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Device;

// Refactor (iter52/issue-895-provider-coverage-contract):
//   Old pattern: New current-state readmodels added ad-hoc without enforced activation provider coverage; provider creation was a convention only.
//   New principle: CI guard requires every new current-state readmodel to have an associated IProjectionActivationPlanProvider implementation + DI + test, or an explicit [ProjectionExempt] classification.
public sealed class DeviceRegistrationCommittedStateProjectionActivationPlanProvider
    : IProjectionActivationPlanProvider
{
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var payload = context.Published.StateEvent?.EventData;
        if (context.ActorType != typeof(DeviceRegistrationGAgent) ||
            payload == null ||
            !IsDeviceRegistrationEvent(payload))
        {
            return [];
        }

        return
        [
            new ProjectionActivationPlan
            {
                LeaseType = typeof(DeviceRegistrationMaterializationRuntimeLease),
                StartRequest = new ProjectionScopeStartRequest
                {
                    RootActorId = context.ActorId,
                    ProjectionKind = DeviceRegistrationProjectionBootstrapActivator.ProjectionKind,
                    Mode = ProjectionRuntimeMode.DurableMaterialization,
                },
            },
        ];
    }

    private static bool IsDeviceRegistrationEvent(Any payload) =>
        payload.Is(DeviceRegisteredEvent.Descriptor) ||
        payload.Is(DeviceUnregisteredEvent.Descriptor) ||
        payload.Is(DeviceTombstonesCompactedEvent.Descriptor);
}
