namespace Aevatar.CQRS.Projection.Core.Abstractions;

public interface IProjectionFailureAlertSink
{
    Task PublishAsync(ProjectionFailureAlert alert, CancellationToken ct = default);
}
