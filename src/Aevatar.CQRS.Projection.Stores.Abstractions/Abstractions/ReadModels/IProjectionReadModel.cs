namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionReadModel
{
    string Id { get; }

    long StateVersion { get; }

    string LastEventId { get; }
}
