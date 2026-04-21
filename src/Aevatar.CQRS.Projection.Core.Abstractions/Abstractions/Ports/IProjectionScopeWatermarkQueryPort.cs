namespace Aevatar.CQRS.Projection.Core.Abstractions;

public interface IProjectionScopeWatermarkQueryPort
{
    Task<long?> GetLastSuccessfulVersionAsync(
        ProjectionRuntimeScopeKey scopeKey,
        CancellationToken ct = default);
}
