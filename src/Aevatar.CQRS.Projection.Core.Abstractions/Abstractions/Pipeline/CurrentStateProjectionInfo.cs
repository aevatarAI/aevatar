using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Framework-owned values resolved from one committed current-state observation.
/// </summary>
public sealed record CurrentStateProjectionInfo(
    string RootActorId,
    string CommandId,
    string CorrelationId,
    long StateVersion,
    string LastEventId,
    DateTimeOffset ObservedAt,
    EventEnvelope Envelope,
    Any? ObservedPayload);
