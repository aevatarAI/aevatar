namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IVersionedProjectionGraphStore
{
    Task<ProjectionGraphDeltaApplyResult> ApplyDeltaAsync(
        ProjectionGraphDelta delta,
        CancellationToken ct = default);

    Task<ProjectionGraphOwnerSnapshotReadResult> ReadOwnerSnapshotAsync(
        ProjectionGraphRouteFingerprint route,
        CancellationToken ct = default);
}
