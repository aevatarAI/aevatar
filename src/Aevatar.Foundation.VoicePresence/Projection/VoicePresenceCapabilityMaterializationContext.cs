using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Projection;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: host voice resolution inspected local actor/module object shape to infer session capability.
//   New principle: voice capability is materialized from committed actor state into actor-scoped current-state read models.
public sealed class VoicePresenceCapabilityMaterializationContext : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
