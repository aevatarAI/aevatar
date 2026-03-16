namespace Aevatar.CQRS.Projection.Core.Abstractions;

public interface IProjectionFailureReplayService
{
    Task<bool> ReplayAsync(
        ProjectionRuntimeScopeKey scopeKey,
        int maxItems = 100,
        CancellationToken ct = default);
}
