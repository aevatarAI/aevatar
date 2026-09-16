namespace Aevatar.GAgents.Channel.Identity.ProjectionRecovery;

public interface IAevatarOAuthClientVersionRegressionRepairService
{
    Task<AevatarOAuthClientVersionRegressionInspection> InspectAsync(
        CancellationToken ct = default);

    Task<AevatarOAuthClientVersionRegressionRepairResult> RepairAsync(
        AevatarOAuthClientVersionRegressionRepairRequest request,
        CancellationToken ct = default);
}

public interface IAevatarOAuthClientVersionRegressionStorePort
{
    Task<AevatarOAuthClientVersionRegressionInspection> InspectAsync(
        CancellationToken ct = default);

    Task<AevatarOAuthClientReplicaDeleteDisposition> DeleteIfMatchesAsync(
        AevatarOAuthClientVersionRegressionRepairRequest request,
        CancellationToken ct = default);
}

public interface IAevatarOAuthClientProjectionRepublishPort
{
    Task<AevatarOAuthClientProjectionRepublishReceipt> DispatchAsync(
        long expectedStateVersion,
        string repairRequestId,
        CancellationToken ct = default);
}

public sealed record AevatarOAuthClientVersionRegressionInspection(
    string ActorId,
    long SourceStateVersion,
    long? DocumentStateVersion,
    string DocumentLastEventId,
    string DocumentActorId,
    bool Repairable,
    string Detail);

public sealed record AevatarOAuthClientVersionRegressionRepairRequest(
    string ExpectedActorId,
    long ExpectedSourceStateVersion,
    long ExpectedDocumentStateVersion,
    string ExpectedDocumentLastEventId,
    string RepairRequestId,
    string RepairReason,
    string RequestedBySubjectId);

public sealed record AevatarOAuthClientProjectionRepublishReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId);

public enum AevatarOAuthClientReplicaDeleteDisposition
{
    Deleted = 0,
    AlreadyAbsent = 1,
    SourceChanged = 2,
    DocumentChanged = 3,
    RevisionConflict = 4,
}

public enum AevatarOAuthClientVersionRegressionRepairStatus
{
    Conflict = 0,
    Accepted = 1,
}

public sealed record AevatarOAuthClientVersionRegressionRepairResult(
    AevatarOAuthClientVersionRegressionRepairStatus Status,
    AevatarOAuthClientVersionRegressionInspection Inspection,
    string RepairRequestId,
    AevatarOAuthClientReplicaDeleteDisposition? DeleteDisposition,
    AevatarOAuthClientProjectionRepublishReceipt? RepublishReceipt,
    string Detail)
{
    public string CommandId => RepublishReceipt?.CommandId ?? string.Empty;

    public static AevatarOAuthClientVersionRegressionRepairResult Conflict(
        AevatarOAuthClientVersionRegressionInspection inspection,
        string repairRequestId,
        string detail,
        AevatarOAuthClientReplicaDeleteDisposition? deleteDisposition = null) =>
        new(
            AevatarOAuthClientVersionRegressionRepairStatus.Conflict,
            inspection,
            repairRequestId,
            deleteDisposition,
            RepublishReceipt: null,
            detail);

    public static AevatarOAuthClientVersionRegressionRepairResult Accepted(
        AevatarOAuthClientVersionRegressionInspection inspection,
        string repairRequestId,
        AevatarOAuthClientReplicaDeleteDisposition deleteDisposition,
        AevatarOAuthClientProjectionRepublishReceipt republishReceipt) =>
        new(
            AevatarOAuthClientVersionRegressionRepairStatus.Accepted,
            inspection,
            repairRequestId,
            deleteDisposition,
            republishReceipt,
            "OAuth client projection repair command was accepted for dispatch.");
}
