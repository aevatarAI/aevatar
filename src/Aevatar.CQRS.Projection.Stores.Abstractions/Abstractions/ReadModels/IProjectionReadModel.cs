using Google.Protobuf;

namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionReadModel
{
    string Id { get; }

    string ActorId { get; }

    long StateVersion { get; }

    string LastEventId { get; }

    DateTimeOffset UpdatedAt { get; }
}

public interface IProjectionReadModel<TSelf> : IProjectionReadModel, IMessage<TSelf>
    where TSelf : class, IProjectionReadModel<TSelf>, IMessage<TSelf>, new()
{
}
