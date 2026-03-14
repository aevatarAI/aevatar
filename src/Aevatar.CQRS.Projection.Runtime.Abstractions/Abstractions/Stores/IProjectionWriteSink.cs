namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionWriteSink<in TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    string SinkName { get; }

    bool IsEnabled { get; }

    string DisabledReason { get; }

    Task UpsertAsync(TReadModel readModel, CancellationToken ct = default);
}
