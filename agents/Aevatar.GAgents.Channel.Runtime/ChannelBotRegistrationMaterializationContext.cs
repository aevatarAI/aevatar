using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed class ChannelBotRegistrationMaterializationContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}
