using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GAgents.ChatRouting;

/// <summary>
/// Materialization context for the ChatRoutePolicy projection scope. Carries
/// the authority actor id and projection kind into
/// <see cref="ChatRoutePolicyCurrentStateProjector"/>.
/// </summary>
public sealed class ChatRoutePolicyMaterializationContext : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
