using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionScopeWatermarkReadModelQueryPort : IProjectionScopeWatermarkQueryPort
{
    private readonly IProjectionDocumentReader<ProjectionScopeWatermarkReadModel, string> _reader;

    // Refactor (iter18/cluster-003):
    //   Old pattern: Projection watermark query port reads IEventStore and rebuilds ProjectionScopeState in the query call.
    //   New principle: Watermark queries read a materialized actor-owned/projection-owned read model, with rebuild only in maintenance paths.
    // New pattern: query reads materialized watermark; event replay is repair/recovery only.
    public ProjectionScopeWatermarkReadModelQueryPort(
        IProjectionDocumentReader<ProjectionScopeWatermarkReadModel, string> reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<long?> GetLastSuccessfulVersionAsync(
        ProjectionRuntimeScopeKey scopeKey,
        CancellationToken ct = default)
    {
        var watermark = await _reader.GetAsync(ProjectionScopeActorId.Build(scopeKey), ct).ConfigureAwait(false);
        if (watermark is not { Active: true, Released: false })
            return null;

        return watermark.LastSuccessfulVersion;
    }
}
