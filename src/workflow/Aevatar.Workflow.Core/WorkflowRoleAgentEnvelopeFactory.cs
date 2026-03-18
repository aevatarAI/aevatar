using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core;

internal static class WorkflowRoleAgentEnvelopeFactory
{
    public static EventEnvelope CreateInitializeEnvelope(RoleDefinition role, string actorId)
    {
        var initialize = new InitializeRoleAgentEvent
        {
            RoleName = role.Name ?? string.Empty,
            ProviderName = string.IsNullOrWhiteSpace(role.Provider) ? string.Empty : role.Provider,
            Model = string.IsNullOrWhiteSpace(role.Model) ? string.Empty : role.Model,
            SystemPrompt = role.SystemPrompt ?? string.Empty,
            MaxTokens = role.MaxTokens ?? 0,
            MaxToolRounds = role.MaxToolRounds ?? 0,
            MaxHistoryMessages = role.MaxHistoryMessages ?? 0,
            StreamBufferCapacity = role.StreamBufferCapacity ?? 0,
            EventModules = role.EventModules ?? string.Empty,
            EventRoutes = role.EventRoutes ?? string.Empty,
        };

        if (role.Temperature.HasValue)
            initialize.Temperature = role.Temperature.Value;

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
