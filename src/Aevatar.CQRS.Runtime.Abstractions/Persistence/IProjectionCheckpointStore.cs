namespace Aevatar.CQRS.Runtime.Abstractions.Persistence;

public interface IProjectionCheckpointStore
{
    Task<long?> GetAsync(string projectionName, CancellationToken ct = default);

    Task SaveAsync(string projectionName, long checkpoint, CancellationToken ct = default);
}
