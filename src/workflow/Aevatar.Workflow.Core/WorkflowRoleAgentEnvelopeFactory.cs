using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core;

internal static class WorkflowRoleAgentEnvelopeFactory
{
    public static EventEnvelope CreateInitializeEnvelope(RoleDefinition role, string actorId)
    {
        var initialize = new WorkflowRoleActorInitializedEvent
        {
            TargetRole = role.Id ?? string.Empty,
        };

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(initialize),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(actorId, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };
    }
}
