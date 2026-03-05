namespace Aevatar.App.GAgents;

public sealed record SyncResult(
    string SyncId,
    int ServerRevision,
    Dictionary<string, SyncEntity> DeltaEntities,
    List<string> Accepted,
    List<RejectedEntity> Rejected);
