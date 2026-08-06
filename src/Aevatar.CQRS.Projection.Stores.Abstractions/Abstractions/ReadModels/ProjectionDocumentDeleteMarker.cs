namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed record ProjectionDocumentDeleteMarker(
    string Id,
    string ActorId,
    long StateVersion,
    string LastEventId,
    DateTimeOffset UpdatedAt) : IProjectionReadModel;
