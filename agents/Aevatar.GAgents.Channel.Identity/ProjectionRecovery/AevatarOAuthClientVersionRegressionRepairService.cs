using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.ProjectionRecovery;

public sealed class AevatarOAuthClientVersionRegressionRepairService
    : IAevatarOAuthClientVersionRegressionRepairService
{
    private readonly IAevatarOAuthClientVersionRegressionStorePort _store;
    private readonly IAevatarOAuthClientProjectionRepublishPort _republish;
    private readonly ILogger<AevatarOAuthClientVersionRegressionRepairService> _logger;

    public AevatarOAuthClientVersionRegressionRepairService(
        IAevatarOAuthClientVersionRegressionStorePort store,
        IAevatarOAuthClientProjectionRepublishPort republish,
        ILogger<AevatarOAuthClientVersionRegressionRepairService>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _republish = republish ?? throw new ArgumentNullException(nameof(republish));
        _logger = logger ?? NullLogger<AevatarOAuthClientVersionRegressionRepairService>.Instance;
    }

    public async Task<AevatarOAuthClientVersionRegressionInspection> InspectAsync(
        CancellationToken ct = default)
    {
        var inspection = await _store.InspectAsync(ct).ConfigureAwait(false);
        return Classify(inspection);
    }

    public async Task<AevatarOAuthClientVersionRegressionRepairResult> RepairAsync(
        AevatarOAuthClientVersionRegressionRepairRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = Normalize(request);
        var inspection = await InspectAsync(ct).ConfigureAwait(false);

        if (!string.Equals(
                normalized.ExpectedActorId,
                inspection.ActorId,
                StringComparison.Ordinal))
        {
            return AevatarOAuthClientVersionRegressionRepairResult.Conflict(
                inspection,
                normalized.RepairRequestId,
                "OAuth client projection repair source actor identity changed.");
        }

        if (inspection.SourceStateVersion <= 0 ||
            inspection.SourceStateVersion != normalized.ExpectedSourceStateVersion)
        {
            return AevatarOAuthClientVersionRegressionRepairResult.Conflict(
                inspection,
                normalized.RepairRequestId,
                "OAuth client projection repair source version changed.");
        }

        // A missing replica is not proof that this caller previously held the
        // exact deleted revision. Only an AlreadyAbsent returned by the
        // conditional delete below is provenance-safe: that disposition is
        // reached after this invocation acquired and verified the ES lease.
        if (!inspection.DocumentStateVersion.HasValue)
        {
            return AevatarOAuthClientVersionRegressionRepairResult.Conflict(
                inspection,
                normalized.RepairRequestId,
                "OAuth client projection repair document is absent; use the governed projection rebuild recovery path.");
        }

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
            return AevatarOAuthClientVersionRegressionRepairResult.Conflict(
                inspection,
                normalized.RepairRequestId,
                "OAuth client projection repair document fingerprint changed.");
        }

        var deleteDisposition = await _store
            .DeleteIfMatchesAsync(normalized, ct)
            .ConfigureAwait(false);
        if (deleteDisposition is not AevatarOAuthClientReplicaDeleteDisposition.Deleted and
            not AevatarOAuthClientReplicaDeleteDisposition.AlreadyAbsent)
        {
            return AevatarOAuthClientVersionRegressionRepairResult.Conflict(
                inspection,
                normalized.RepairRequestId,
                DeleteConflictDetail(deleteDisposition),
                deleteDisposition);
        }

        AevatarOAuthClientProjectionRepublishReceipt receipt;
        try
        {
            receipt = await _republish
                .DispatchAsync(
                    normalized.ExpectedSourceStateVersion,
                    normalized.RepairRequestId,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "OAuth client projection repair dispatch failed after guarded replica delete. repair_request_id={RepairRequestId} delete_disposition={DeleteDisposition} exception_type={ExceptionType}",
                normalized.RepairRequestId,
                deleteDisposition,
                ex.GetType().Name);
            throw;
        }
        return AevatarOAuthClientVersionRegressionRepairResult.Accepted(
            inspection,
            normalized.RepairRequestId,
            deleteDisposition,
            receipt);
    }

    private static AevatarOAuthClientVersionRegressionInspection Classify(
        AevatarOAuthClientVersionRegressionInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        if (inspection.SourceStateVersion <= 0)
        {
            return inspection with
            {
                Repairable = false,
                Detail = "OAuth client source has no committed state.",
            };
        }

        if (!inspection.DocumentStateVersion.HasValue)
        {
            return inspection with
            {
                Repairable = false,
                Detail = "OAuth client projection document is absent.",
            };
        }

        if (!string.Equals(
                AevatarOAuthClientGAgent.WellKnownId,
                inspection.ActorId,
                StringComparison.Ordinal) ||
            !string.Equals(
                inspection.ActorId,
                inspection.DocumentActorId,
                StringComparison.Ordinal))
        {
            return inspection with
            {
                Repairable = false,
                Detail = "OAuth client projection document actor identity does not match the source actor.",
            };
        }

        if (inspection.DocumentStateVersion.Value <= inspection.SourceStateVersion)
        {
            return inspection with
            {
                Repairable = false,
                Detail = "OAuth client projection document is not ahead of the source.",
            };
        }

        return inspection with
        {
            Repairable = true,
            Detail = "OAuth client projection document version exceeds the authoritative source version.",
        };
    }

    private static AevatarOAuthClientVersionRegressionRepairRequest Normalize(
        AevatarOAuthClientVersionRegressionRepairRequest request)
    {
        if (request.ExpectedSourceStateVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Expected source state version must be positive.");
        }

        if (request.ExpectedDocumentStateVersion <= request.ExpectedSourceStateVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Expected document state version must exceed the expected source state version.");
        }

        return request with
        {
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
        if (fieldName == nameof(AevatarOAuthClientVersionRegressionRepairRequest.RepairRequestId) &&
            (normalized.Length > 128 || normalized.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not ':' and not '-')))
        {
            throw new ArgumentException(
                $"{fieldName} must be a 1-128 character opaque identifier.",
                fieldName);
        }

        if (fieldName == nameof(AevatarOAuthClientVersionRegressionRepairRequest.RepairReason) &&
            (normalized.Length > 256 || normalized.Any(static character =>
                char.IsControl(character) || character is '\u2028' or '\u2029')))
        {
            throw new ArgumentException(
                $"{fieldName} must be a single-line value no longer than 256 characters.",
                fieldName);
        }

        return normalized;
    }

    private static string DeleteConflictDetail(
        AevatarOAuthClientReplicaDeleteDisposition disposition) =>
        disposition switch
        {
            AevatarOAuthClientReplicaDeleteDisposition.SourceChanged =>
                "OAuth client projection repair source version changed during delete.",
            AevatarOAuthClientReplicaDeleteDisposition.DocumentChanged =>
                "OAuth client projection repair document fingerprint changed during delete.",
            AevatarOAuthClientReplicaDeleteDisposition.RevisionConflict =>
                "OAuth client projection repair document revision changed during delete.",
            _ => "OAuth client projection repair delete was rejected.",
        };
}
