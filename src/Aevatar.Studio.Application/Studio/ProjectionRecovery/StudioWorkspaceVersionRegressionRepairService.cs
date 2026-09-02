namespace Aevatar.Studio.Application.Studio.ProjectionRecovery;

public sealed class StudioWorkspaceVersionRegressionRepairService
    : IStudioWorkspaceVersionRegressionRepairService
{
    private readonly IStudioWorkspaceVersionRegressionStorePort _store;
    private readonly IStudioWorkspaceProjectionRepublishPort _republish;

    public StudioWorkspaceVersionRegressionRepairService(
        IStudioWorkspaceVersionRegressionStorePort store,
        IStudioWorkspaceProjectionRepublishPort republish)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _republish = republish ?? throw new ArgumentNullException(nameof(republish));
    }

    public async Task<StudioWorkspaceVersionRegressionInspection> InspectAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var inspection = await _store.InspectAsync(normalizedScopeId, ct);
        return Classify(inspection with { ScopeId = normalizedScopeId });
    }

    public async Task<StudioWorkspaceVersionRegressionRepairResult> RepairAsync(
        StudioWorkspaceVersionRegressionRepairRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = Normalize(request);
        var inspection = await InspectAsync(normalized.ScopeId, ct);

        if (!string.Equals(
                normalized.ExpectedActorId,
                inspection.ActorId,
                StringComparison.Ordinal))
        {
            return StudioWorkspaceVersionRegressionRepairResult.Conflict(
                inspection,
                normalized.RepairRequestId,
                "Workspace projection repair source actor identity changed.");
        }

        if (inspection.SourceStateVersion <= 0 ||
            inspection.SourceStateVersion != normalized.ExpectedSourceStateVersion)
        {
            return StudioWorkspaceVersionRegressionRepairResult.Conflict(
                inspection,
                normalized.RepairRequestId,
                "Workspace projection repair source version changed.");
        }

        if (inspection.DocumentStateVersion.HasValue)
        {
            if (!inspection.Repairable ||
                !string.Equals(
                    normalized.ExpectedActorId,
                    inspection.DocumentActorId,
                    StringComparison.Ordinal) ||
                inspection.DocumentStateVersion.Value != normalized.ExpectedDocumentStateVersion ||
                !string.Equals(
                    inspection.DocumentLastEventId,
                    normalized.ExpectedDocumentLastEventId,
                    StringComparison.Ordinal))
            {
                return StudioWorkspaceVersionRegressionRepairResult.Conflict(
                    inspection,
                    normalized.RepairRequestId,
                    "Workspace projection repair document fingerprint changed.");
            }
        }

        var deleteDisposition = await _store.DeleteIfMatchesAsync(normalized, ct);
        if (deleteDisposition is not StudioWorkspaceReplicaDeleteDisposition.Deleted and
            not StudioWorkspaceReplicaDeleteDisposition.AlreadyAbsent)
        {
            return StudioWorkspaceVersionRegressionRepairResult.Conflict(
                inspection,
                normalized.RepairRequestId,
                DeleteConflictDetail(deleteDisposition),
                deleteDisposition);
        }

        var receipt = await _republish.DispatchAsync(
            normalized.ScopeId,
            normalized.ExpectedSourceStateVersion,
            normalized.RepairRequestId,
            CancellationToken.None);
        return StudioWorkspaceVersionRegressionRepairResult.Accepted(
            inspection,
            normalized.RepairRequestId,
            deleteDisposition,
            receipt);
    }

    private static StudioWorkspaceVersionRegressionInspection Classify(
        StudioWorkspaceVersionRegressionInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        if (inspection.SourceStateVersion <= 0)
        {
            return inspection with
            {
                Repairable = false,
                Detail = "Workspace source has no committed state.",
            };
        }

        if (!inspection.DocumentStateVersion.HasValue)
        {
            return inspection with
            {
                Repairable = false,
                Detail = "Workspace projection document is absent.",
            };
        }

        if (string.IsNullOrWhiteSpace(inspection.ActorId) ||
            !string.Equals(
                inspection.ActorId,
                inspection.DocumentActorId,
                StringComparison.Ordinal))
        {
            return inspection with
            {
                Repairable = false,
                Detail = "Workspace projection document actor identity does not match the source actor.",
            };
        }

        if (inspection.DocumentStateVersion.Value <= inspection.SourceStateVersion)
        {
            return inspection with
            {
                Repairable = false,
                Detail = "Workspace projection document is not ahead of the source.",
            };
        }

        return inspection with
        {
            Repairable = true,
            Detail = "Workspace projection document version exceeds the authoritative source version.",
        };
    }

    private static StudioWorkspaceVersionRegressionRepairRequest Normalize(
        StudioWorkspaceVersionRegressionRepairRequest request)
    {
        if (request.ExpectedSourceStateVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Expected source state version must be positive.");
        }

        if (request.ExpectedDocumentStateVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Expected document state version must be positive.");
        }

        if (request.ExpectedDocumentStateVersion <= request.ExpectedSourceStateVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Expected document state version must exceed the expected source state version.");
        }

        return request with
        {
            ScopeId = NormalizeRequired(request.ScopeId, nameof(request.ScopeId)),
            ExpectedActorId = request.ExpectedActorId?.Trim() ?? string.Empty,
            ExpectedDocumentLastEventId = NormalizeRequired(
                request.ExpectedDocumentLastEventId,
                nameof(request.ExpectedDocumentLastEventId)),
            RepairRequestId = NormalizeRequired(
                request.RepairRequestId,
                nameof(request.RepairRequestId)),
            RepairReason = NormalizeRequired(
                request.RepairReason,
                nameof(request.RepairReason)),
            RequestedBySubjectId = NormalizeRequired(
                request.RequestedBySubjectId,
                nameof(request.RequestedBySubjectId)),
        };
    }

    private static string NormalizeRequired(string value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new ArgumentException($"{fieldName} is required.", fieldName);
        return normalized;
    }

    private static string DeleteConflictDetail(
        StudioWorkspaceReplicaDeleteDisposition disposition) =>
        disposition switch
        {
            StudioWorkspaceReplicaDeleteDisposition.SourceChanged =>
                "Workspace projection repair source version changed during delete.",
            StudioWorkspaceReplicaDeleteDisposition.DocumentChanged =>
                "Workspace projection repair document fingerprint changed during delete.",
            StudioWorkspaceReplicaDeleteDisposition.RevisionConflict =>
                "Workspace projection repair document revision changed during delete.",
            _ => "Workspace projection repair delete was rejected.",
        };
}
