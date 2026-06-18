using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core;

internal static class WorkflowRoleAgentEnvelopeFactory
{
    public static EventEnvelope CreateInitializeEnvelope(RoleDefinition role, string publisherActorId, string targetActorId)
    {
        var initialize = new WorkflowRoleInitializeEvent
        {
            // Refactor (iter15/cluster-028):
            //   Old pattern: role actors received display/config data and downstream code recovered RoleId from actor id text.
            //   New principle: workflow initialization sends RoleDefinition.Id as a typed RoleId field.
            RoleId = role.Id ?? string.Empty,
            RoleName = role.Name ?? string.Empty,
            ProviderName = string.IsNullOrWhiteSpace(role.Provider) ? string.Empty : role.Provider,
            Model = string.IsNullOrWhiteSpace(role.Model) ? string.Empty : role.Model,
            SystemPrompt = role.SystemPrompt ?? string.Empty,
            MaxTokens = role.MaxTokens ?? 0,
            MaxToolRounds = role.MaxToolRounds ?? 0,
            MaxHistoryMessages = role.MaxHistoryMessages ?? 0,
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
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, targetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };
    }
}
