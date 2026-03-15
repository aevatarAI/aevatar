namespace Aevatar.CQRS.Projection.Core.Abstractions;

public interface IProjectionRuntimeLeaseStopHandler
{
    Task OnProjectionStoppedAsync(CancellationToken ct = default);
}
