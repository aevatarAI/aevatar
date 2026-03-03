namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionReadModel
{
    string Id { get; }

    string ActorId { get; }

    long StateVersion { get; }

    string LastEventId { get; }

    DateTimeOffset UpdatedAt { get; }
}
