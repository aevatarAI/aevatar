namespace Aevatar.App.Application.Projection.Orchestration;

public interface IAppProjectionManager
{
    Task EnsureSubscribedAsync(string actorId, CancellationToken ct = default);

    Task UnsubscribeAsync(string actorId, CancellationToken ct = default);
}
