using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Projection;

public sealed class VoiceRealtimeProjectionContext : IProjectionSessionContext
{
    public string RootActorId { get; init; } = string.Empty;

    public string ProjectionKind { get; init; } = VoiceRealtimeProjectionKinds.RealtimeSession;

    public string SessionId { get; init; } = string.Empty;
}
