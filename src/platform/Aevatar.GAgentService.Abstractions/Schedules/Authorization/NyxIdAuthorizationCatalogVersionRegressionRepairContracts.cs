namespace Aevatar.GAgentService.Abstractions.Schedules.Authorization;

public interface INyxIdAuthorizationCatalogVersionRegressionRepairService
{
    Task<NyxIdAuthorizationCatalogVersionRegressionInspection> InspectPersonalAsync(
        string verifiedOwnerSubject,
        CancellationToken ct = default);

    Task<NyxIdAuthorizationCatalogVersionRegressionRepairResult> RepairPersonalAsync(
        NyxIdAuthorizationCatalogVersionRegressionRepairRequest request,
        CancellationToken ct = default);
}

public interface INyxIdAuthorizationCatalogVersionRegressionStorePort
{
    Task<NyxIdAuthorizationCatalogVersionRegressionInspection> InspectPersonalAsync(
        string verifiedOwnerSubject,
        CancellationToken ct = default);

    Task<NyxIdAuthorizationCatalogReplicaDeleteDisposition> DeleteIfMatchesAsync(
        NyxIdAuthorizationCatalogVersionRegressionRepairRequest request,
        CancellationToken ct = default);
}

public sealed record NyxIdAuthorizationCatalogVersionRegressionInspection(
    string VerifiedOwnerSubject,
    string ActorId,
    long SourceStateVersion,
    long? DocumentStateVersion,
    string DocumentLastEventId,
    string DocumentActorId,
    bool Repairable,
    string Detail);

public sealed record NyxIdAuthorizationCatalogVersionRegressionRepairRequest(
    string VerifiedOwnerSubject,
    string ExpectedActorId,
    string BearerToken,
    long ExpectedSourceStateVersion,
    long ExpectedDocumentStateVersion,
    string ExpectedDocumentLastEventId,
    string RepairRequestId,
    string RepairReason,
    string RequestedBySubjectId)
{
    public override string ToString() =>
        $"{nameof(NyxIdAuthorizationCatalogVersionRegressionRepairRequest)} {{ " +
        $"{nameof(VerifiedOwnerSubject)} = {VerifiedOwnerSubject}, " +
        $"{nameof(ExpectedActorId)} = {ExpectedActorId}, " +
        $"{nameof(BearerToken)} = [REDACTED], " +
        $"{nameof(ExpectedSourceStateVersion)} = {ExpectedSourceStateVersion}, " +
        $"{nameof(ExpectedDocumentStateVersion)} = {ExpectedDocumentStateVersion}, " +
        $"{nameof(ExpectedDocumentLastEventId)} = {ExpectedDocumentLastEventId}, " +
        $"{nameof(RepairRequestId)} = {RepairRequestId}, " +
        $"{nameof(RepairReason)} = {RepairReason}, " +
        $"{nameof(RequestedBySubjectId)} = {RequestedBySubjectId} }}";
}

public enum NyxIdAuthorizationCatalogReplicaDeleteDisposition
{
    Deleted = 0,
    AlreadyAbsent = 1,
    SourceChanged = 2,
    DocumentChanged = 3,
    RevisionConflict = 4,
}

public enum NyxIdAuthorizationCatalogVersionRegressionRepairStatus
{
    Conflict = 0,
    Failed = 1,
    Ready = 2,
    ProjectionPending = 3,
}

public sealed record NyxIdAuthorizationCatalogVersionRegressionRepairResult(
    NyxIdAuthorizationCatalogVersionRegressionRepairStatus Status,
    NyxIdAuthorizationCatalogVersionRegressionInspection Inspection,
    string RepairRequestId,
    NyxIdAuthorizationCatalogReplicaDeleteDisposition? DeleteDisposition,
    NyxIdAuthorizationCatalogRefreshResult? Refresh,
    NyxIdAuthorizationCatalogVisibilityResult? Visibility,
    string Detail)
{
    public static NyxIdAuthorizationCatalogVersionRegressionRepairResult Conflict(
        NyxIdAuthorizationCatalogVersionRegressionInspection inspection,
        string repairRequestId,
        string detail,
        NyxIdAuthorizationCatalogReplicaDeleteDisposition? deleteDisposition = null) =>
        new(
            NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Conflict,
            inspection,
            repairRequestId,
            deleteDisposition,
            Refresh: null,
            Visibility: null,
            detail);

    public static NyxIdAuthorizationCatalogVersionRegressionRepairResult Failed(
        NyxIdAuthorizationCatalogVersionRegressionInspection inspection,
        NyxIdAuthorizationCatalogRefreshResult refresh,
        string repairRequestId = "",
        NyxIdAuthorizationCatalogReplicaDeleteDisposition? deleteDisposition = null,
        NyxIdAuthorizationCatalogVisibilityResult? visibility = null) =>
        new(
            NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Failed,
            inspection,
            repairRequestId,
            deleteDisposition,
            refresh,
            visibility,
            visibility is null
                ? "NyxID authorization catalog refresh failed."
                : "NyxID authorization catalog visibility is unavailable.");

    public static NyxIdAuthorizationCatalogVersionRegressionRepairResult Ready(
        NyxIdAuthorizationCatalogVersionRegressionInspection inspection,
        string repairRequestId,
        NyxIdAuthorizationCatalogReplicaDeleteDisposition deleteDisposition,
        NyxIdAuthorizationCatalogRefreshResult refresh,
        NyxIdAuthorizationCatalogVisibilityResult visibility) =>
        new(
            NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Ready,
            inspection,
            repairRequestId,
            deleteDisposition,
            refresh,
            visibility,
            "NyxID authorization catalog was refreshed and is visible.");

    public static NyxIdAuthorizationCatalogVersionRegressionRepairResult ProjectionPending(
        NyxIdAuthorizationCatalogVersionRegressionInspection inspection,
        string repairRequestId,
        NyxIdAuthorizationCatalogReplicaDeleteDisposition deleteDisposition,
        NyxIdAuthorizationCatalogRefreshResult refresh,
        NyxIdAuthorizationCatalogVisibilityResult visibility) =>
        new(
            NyxIdAuthorizationCatalogVersionRegressionRepairStatus.ProjectionPending,
            inspection,
            repairRequestId,
            deleteDisposition,
            refresh,
            visibility,
            "NyxID authorization catalog refresh committed; projection visibility is pending.");
}
