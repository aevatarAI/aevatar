using Aevatar.GAgentService.Abstractions.Schedules.Authorization;

namespace Aevatar.GAgentService.Application.Schedules.Authorization;

public sealed class NyxIdAuthorizationCatalogVersionRegressionRepairService
    : INyxIdAuthorizationCatalogVersionRegressionRepairService
{
    private readonly INyxIdAuthorizationCatalogVersionRegressionStorePort _store;
    private readonly INyxIdAuthorizationCatalogRepairRefreshPort _refreshPort;
    private readonly INyxIdAuthorizationCatalogVisibilityPort _visibilityPort;

    public NyxIdAuthorizationCatalogVersionRegressionRepairService(
        INyxIdAuthorizationCatalogVersionRegressionStorePort store,
        INyxIdAuthorizationCatalogRepairRefreshPort refreshPort,
        INyxIdAuthorizationCatalogVisibilityPort visibilityPort)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _refreshPort = refreshPort ?? throw new ArgumentNullException(nameof(refreshPort));
        _visibilityPort = visibilityPort ?? throw new ArgumentNullException(nameof(visibilityPort));
    }

    public async Task<NyxIdAuthorizationCatalogVersionRegressionInspection> InspectPersonalAsync(
        string verifiedOwnerSubject,
        CancellationToken ct = default)
    {
        var normalizedSubject = NormalizeRequired(
            verifiedOwnerSubject,
            nameof(verifiedOwnerSubject));
        var inspection = await _store
            .InspectPersonalAsync(normalizedSubject, ct)
            .ConfigureAwait(false);
        return Classify(inspection with { VerifiedOwnerSubject = normalizedSubject });
    }

    public async Task<NyxIdAuthorizationCatalogVersionRegressionRepairResult> RepairPersonalAsync(
        NyxIdAuthorizationCatalogVersionRegressionRepairRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = Normalize(request);
        if (!string.Equals(
                normalized.RequestedBySubjectId,
                normalized.VerifiedOwnerSubject,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Requested subject must match the verified owner subject.",
                nameof(request));
        }

        var owner = new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = normalized.VerifiedOwnerSubject,
        };
        var canonicalActorId = NyxIdAuthorizationCatalogActorIds.Build(owner);
        if (!string.Equals(
                normalized.ExpectedActorId,
                canonicalActorId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Expected actor must match the verified owner's personal NyxID authorization catalog.",
                nameof(request));
        }

        var inspection = await InspectPersonalAsync(normalized.VerifiedOwnerSubject, ct)
            .ConfigureAwait(false);

        if (!string.Equals(
                normalized.ExpectedActorId,
                inspection.ActorId,
                StringComparison.Ordinal))
        {
            return NyxIdAuthorizationCatalogVersionRegressionRepairResult.Conflict(
                inspection,
                normalized.RepairRequestId,
                "NyxID authorization catalog repair source actor identity changed.");
        }

        if (inspection.SourceStateVersion <= 0 ||
            inspection.SourceStateVersion != normalized.ExpectedSourceStateVersion)
        {
            return NyxIdAuthorizationCatalogVersionRegressionRepairResult.Conflict(
                inspection,
                normalized.RepairRequestId,
                "NyxID authorization catalog repair source version changed.");
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
                return NyxIdAuthorizationCatalogVersionRegressionRepairResult.Conflict(
                    inspection,
                    normalized.RepairRequestId,
                    "NyxID authorization catalog repair document fingerprint changed.");
            }
        }

        var deleteDisposition = await _store
            .DeleteIfMatchesAsync(normalized, ct)
            .ConfigureAwait(false);
        if (deleteDisposition is not NyxIdAuthorizationCatalogReplicaDeleteDisposition.Deleted and
            not NyxIdAuthorizationCatalogReplicaDeleteDisposition.AlreadyAbsent)
        {
            return NyxIdAuthorizationCatalogVersionRegressionRepairResult.Conflict(
                inspection,
                normalized.RepairRequestId,
                DeleteConflictDetail(deleteDisposition),
                deleteDisposition);
        }

        var refresh = await _refreshPort
            .RefreshPersonalAsync(
                normalized.VerifiedOwnerSubject,
                normalized.BearerToken,
                normalized.ExpectedSourceStateVersion,
                normalized.RepairRequestId,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!refresh.Success)
        {
            return NyxIdAuthorizationCatalogVersionRegressionRepairResult.Failed(
                inspection,
                refresh,
                normalized.RepairRequestId,
                deleteDisposition);
        }

        var visibility = await _visibilityPort
            .ResolveAsync(owner, refresh.StateVersion, ct)
            .ConfigureAwait(false);
        return visibility.Status switch
        {
            NyxIdAuthorizationCatalogVisibilityStatus.Ready =>
                NyxIdAuthorizationCatalogVersionRegressionRepairResult.Ready(
                    inspection,
                    normalized.RepairRequestId,
                    deleteDisposition,
                    refresh,
                    visibility),
            NyxIdAuthorizationCatalogVisibilityStatus.ProjectionPending =>
                NyxIdAuthorizationCatalogVersionRegressionRepairResult.ProjectionPending(
                    inspection,
                    normalized.RepairRequestId,
                    deleteDisposition,
                    refresh,
                    visibility),
            _ => NyxIdAuthorizationCatalogVersionRegressionRepairResult.Failed(
                inspection,
                refresh,
                normalized.RepairRequestId,
                deleteDisposition,
                visibility),
        };
    }

    private static NyxIdAuthorizationCatalogVersionRegressionInspection Classify(
        NyxIdAuthorizationCatalogVersionRegressionInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        if (inspection.SourceStateVersion <= 0)
        {
            return inspection with
            {
                Repairable = false,
                Detail = "NyxID authorization catalog source has no committed state.",
            };
        }

        if (!inspection.DocumentStateVersion.HasValue)
        {
            return inspection with
            {
                Repairable = false,
                Detail = "NyxID authorization catalog projection document is absent.",
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
                Detail =
                    "NyxID authorization catalog projection document actor identity does not match the source actor.",
            };
        }

        if (inspection.DocumentStateVersion.Value <= inspection.SourceStateVersion)
        {
            return inspection with
            {
                Repairable = false,
                Detail = "NyxID authorization catalog projection document is not ahead of the source.",
            };
        }

        return inspection with
        {
            Repairable = true,
            Detail =
                "NyxID authorization catalog projection document version exceeds the authoritative source version.",
        };
    }

    private static NyxIdAuthorizationCatalogVersionRegressionRepairRequest Normalize(
        NyxIdAuthorizationCatalogVersionRegressionRepairRequest request)
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

        ArgumentException.ThrowIfNullOrWhiteSpace(request.BearerToken);
        return request with
        {
            VerifiedOwnerSubject = NormalizeRequired(
                request.VerifiedOwnerSubject,
                nameof(request.VerifiedOwnerSubject)),
            ExpectedActorId = NormalizeRequired(
                request.ExpectedActorId,
                nameof(request.ExpectedActorId)),
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
        NyxIdAuthorizationCatalogReplicaDeleteDisposition disposition) =>
        disposition switch
        {
            NyxIdAuthorizationCatalogReplicaDeleteDisposition.SourceChanged =>
                "NyxID authorization catalog repair source changed during delete.",
            NyxIdAuthorizationCatalogReplicaDeleteDisposition.DocumentChanged =>
                "NyxID authorization catalog repair document fingerprint changed during delete.",
            NyxIdAuthorizationCatalogReplicaDeleteDisposition.RevisionConflict =>
                "NyxID authorization catalog repair document revision changed during delete.",
            _ => "NyxID authorization catalog repair delete was rejected.",
        };
}
