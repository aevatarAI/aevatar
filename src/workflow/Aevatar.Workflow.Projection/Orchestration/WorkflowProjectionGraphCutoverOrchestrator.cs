using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionGraphCutoverOrchestrator
{
    private readonly IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> _reportReader;
    private readonly IVersionedProjectionGraphStore _graphStore;
    private readonly WorkflowRunIncrementalGraphMaterializer _materializer;
    private readonly ProjectionGraphProviderStatus? _graphProviderStatus;

    public WorkflowProjectionGraphCutoverOrchestrator(
        IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> reportReader,
        IVersionedProjectionGraphStore graphStore,
        WorkflowRunIncrementalGraphMaterializer materializer,
        ProjectionGraphProviderStatus? graphProviderStatus = null)
    {
        _reportReader = reportReader ?? throw new ArgumentNullException(nameof(reportReader));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        _graphProviderStatus = graphProviderStatus;
    }

    public async Task<WorkflowProjectionGraphCandidate?> BuildCandidateAsync(
        string rootActorId,
        string projectionKind,
        ProjectionMaterializationRouteFingerprint candidateRoute,
        CancellationToken ct = default)
    {
        if (_graphProviderStatus is { Enabled: false })
            return null;

        var report = await ReadAuthoritativeReportAsync(rootActorId, ct);
        if (report == null)
            return null;

        var storeRoute = _materializer.ResolveStoreRoute(projectionKind, report.Id, candidateRoute);
        var delta = _materializer.BuildFullCandidateDelta(report, projectionKind, candidateRoute);
        var applied = await _graphStore.ApplyDeltaAsync(delta, ct);
        if (applied.Disposition is not ProjectionGraphDeltaApplyDisposition.Applied and
            not ProjectionGraphDeltaApplyDisposition.ExactDuplicate)
        {
            throw new InvalidOperationException(
                $"Workflow graph candidate backfill was rejected with {applied.Disposition}: {applied.Detail}");
        }

        var after = await _graphStore.ReadOwnerSnapshotAsync(storeRoute, ct);
        if (after.Disposition != ProjectionGraphOwnerSnapshotReadDisposition.Found || after.Snapshot == null)
        {
            throw new InvalidOperationException(
                $"Workflow graph candidate snapshot is unavailable after backfill: {after.Disposition}.");
        }

        var snapshot = after.Snapshot;
        EnsureSameSource(report, snapshot.Source);
        var fingerprint = _materializer.ComputeSnapshotFingerprint(snapshot);
        var expected = _materializer.ComputeExpectedCandidateFingerprint(report, projectionKind, candidateRoute);
        if (!string.Equals(fingerprint, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("Workflow graph candidate does not match the golden full materialization.");

        return new WorkflowProjectionGraphCandidate(
            new ProjectionSourceCoordinate
            {
                ActorId = snapshot.Source.ActorId,
                StateVersion = snapshot.Source.StateVersion,
                EventId = snapshot.Source.EventId,
            },
            fingerprint);
    }

    public async Task<bool> VerifyCandidateAsync(
        string rootActorId,
        string projectionKind,
        ProjectionMaterializationRouteFingerprint candidateRoute,
        ProjectionSourceCoordinate candidateSource,
        string candidateFingerprint,
        CancellationToken ct = default)
    {
        if (_graphProviderStatus is { Enabled: false })
            return false;

        var report = await ReadAuthoritativeReportAsync(rootActorId, ct);
        if (report == null || !HasSameSource(report, candidateSource))
            return false;

        var storeRoute = _materializer.ResolveStoreRoute(projectionKind, report.Id, candidateRoute);
        var read = await _graphStore.ReadOwnerSnapshotAsync(storeRoute, ct);
        if (read.Disposition != ProjectionGraphOwnerSnapshotReadDisposition.Found || read.Snapshot == null)
            return false;
        if (!HasSameSource(candidateSource, read.Snapshot.Source))
            return false;

        var actual = _materializer.ComputeSnapshotFingerprint(read.Snapshot);
        if (!string.Equals(actual, candidateFingerprint, StringComparison.Ordinal))
            return false;

        var expected = _materializer.ComputeExpectedCandidateFingerprint(report, projectionKind, candidateRoute);
        return string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private async Task<WorkflowRunInsightReportDocument?> ReadAuthoritativeReportAsync(
        string rootActorId,
        CancellationToken ct)
    {
        var report = await _reportReader.GetAsync(rootActorId, ct);
        if (report == null)
            return null;
        if (report.StateVersion <= 0 ||
            string.IsNullOrWhiteSpace(report.LastEventId) ||
            !string.Equals(report.RootActorId, rootActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Workflow graph cutover requires a report with an exact authoritative source coordinate.");
        }

        return report;
    }

    private static void EnsureSameSource(
        WorkflowRunInsightReportDocument report,
        ProjectionGraphSourceCoordinate? source)
    {
        if (source == null ||
            source.StateVersion != report.StateVersion ||
            !string.Equals(source.ActorId, report.RootActorId, StringComparison.Ordinal) ||
            !string.Equals(source.EventId, report.LastEventId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Workflow graph candidate source does not match the authoritative report coordinate.");
        }
    }

    private static bool HasSameSource(
        WorkflowRunInsightReportDocument report,
        ProjectionSourceCoordinate? source) =>
        source != null &&
        source.StateVersion == report.StateVersion &&
        string.Equals(source.ActorId, report.RootActorId, StringComparison.Ordinal) &&
        string.Equals(source.EventId, report.LastEventId, StringComparison.Ordinal);

    private static bool HasSameSource(
        ProjectionSourceCoordinate expected,
        ProjectionGraphSourceCoordinate? actual) =>
        actual != null &&
        expected.StateVersion == actual.StateVersion &&
        string.Equals(expected.ActorId, actual.ActorId, StringComparison.Ordinal) &&
        string.Equals(expected.EventId, actual.EventId, StringComparison.Ordinal);
}

public sealed record WorkflowProjectionGraphCandidate(
    ProjectionSourceCoordinate Source,
    string Fingerprint);
