namespace Aevatar.Studio.Application.Studio.ProjectionRecovery;

public interface IStudioWorkspaceVersionRegressionRepairService
{
    Task<StudioWorkspaceVersionRegressionInspection> InspectAsync(
        string scopeId,
        CancellationToken ct = default);

    Task<StudioWorkspaceVersionRegressionRepairResult> RepairAsync(
        StudioWorkspaceVersionRegressionRepairRequest request,
        CancellationToken ct = default);
}

public interface IStudioWorkspaceVersionRegressionStorePort
{
    Task<StudioWorkspaceVersionRegressionInspection> InspectAsync(
        string scopeId,
        CancellationToken ct = default);

    Task<StudioWorkspaceReplicaDeleteDisposition> DeleteIfMatchesAsync(
        StudioWorkspaceVersionRegressionRepairRequest request,
        CancellationToken ct = default);
}

public interface IStudioWorkspaceProjectionRepublishPort
{
    Task<StudioWorkspaceProjectionRepublishReceipt> DispatchAsync(
        string scopeId,
        long minimumStateVersion,
        string repairRequestId,
        CancellationToken ct = default);
}

public sealed record StudioWorkspaceVersionRegressionInspection(
    string ScopeId,
    string ActorId,
    long SourceStateVersion,
    long? DocumentStateVersion,
    string DocumentLastEventId,
    string DocumentActorId,
    bool Repairable,
    string Detail);

public sealed record StudioWorkspaceVersionRegressionRepairRequest(
    string ScopeId,
    string ExpectedActorId,
    long ExpectedSourceStateVersion,
    long ExpectedDocumentStateVersion,
    string ExpectedDocumentLastEventId,
    string RepairRequestId,
    string RepairReason,
    string RequestedBySubjectId);

public sealed record StudioWorkspaceProjectionRepublishReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId);

public enum StudioWorkspaceReplicaDeleteDisposition
{
    Deleted = 0,
    AlreadyAbsent = 1,
    SourceChanged = 2,
    DocumentChanged = 3,
    RevisionConflict = 4,
}

public enum StudioWorkspaceVersionRegressionRepairStatus
{
    Conflict = 0,
    Accepted = 1,
}

public sealed record StudioWorkspaceVersionRegressionRepairResult(
    StudioWorkspaceVersionRegressionRepairStatus Status,
    StudioWorkspaceVersionRegressionInspection Inspection,
    string RepairRequestId,
    StudioWorkspaceReplicaDeleteDisposition? DeleteDisposition,
    StudioWorkspaceProjectionRepublishReceipt? RepublishReceipt,
    string Detail)
{
    public string CommandId => RepublishReceipt?.CommandId ?? string.Empty;

    public static StudioWorkspaceVersionRegressionRepairResult Conflict(
        StudioWorkspaceVersionRegressionInspection inspection,
        string repairRequestId,
        string detail,
        StudioWorkspaceReplicaDeleteDisposition? deleteDisposition = null) =>
        new(
            StudioWorkspaceVersionRegressionRepairStatus.Conflict,
            inspection,
            repairRequestId,
            deleteDisposition,
            RepublishReceipt: null,
            detail);

    public static StudioWorkspaceVersionRegressionRepairResult Accepted(
        StudioWorkspaceVersionRegressionInspection inspection,
        string repairRequestId,
        StudioWorkspaceReplicaDeleteDisposition deleteDisposition,
        StudioWorkspaceProjectionRepublishReceipt republishReceipt) =>
        new(
            StudioWorkspaceVersionRegressionRepairStatus.Accepted,
            inspection,
            repairRequestId,
            deleteDisposition,
            republishReceipt,
            "Workspace projection repair command was accepted for dispatch.");
}
