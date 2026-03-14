namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public sealed class ProjectionStoreDispatchCompensationContext<TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    public string DispatchId { get; init; } = Guid.NewGuid().ToString("N");

    public required string Operation { get; init; }

    public required string FailedStore { get; init; }

    public required IReadOnlyList<string> SucceededStores { get; init; }

    public required TReadModel ReadModel { get; init; }

    public required Exception Exception { get; init; }

    public string ReadModelType { get; init; } =
        typeof(TReadModel).FullName ?? typeof(TReadModel).Name;

    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
