using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.ReadModels;

public sealed class AppSyncEntityReadModel : IProjectionReadModel
{
    public const int MaxSyncResults = 16;

    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public int ServerRevision { get; set; }
    public Dictionary<string, SyncEntityEntry> Entities { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, SyncResultEntry> SyncResults { get; set; } = new(StringComparer.Ordinal);
    public List<string> SyncResultOrder { get; set; } = [];
}

public sealed class SyncResultEntry
{
    public string SyncId { get; set; } = "";
    public int ClientRevision { get; set; }
    public int ServerRevision { get; set; }
    public List<string> Accepted { get; set; } = [];
    public List<RejectedEntityEntry> Rejected { get; set; } = [];
}
