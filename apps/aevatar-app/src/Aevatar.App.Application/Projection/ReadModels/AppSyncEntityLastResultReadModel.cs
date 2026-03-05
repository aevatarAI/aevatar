using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.ReadModels;

public sealed class AppSyncEntityLastResultReadModel : IProjectionReadModel
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string SyncId { get; set; } = "";
    public int ClientRevision { get; set; }
    public int ServerRevision { get; set; }
    public List<string> Accepted { get; set; } = [];
    public List<RejectedEntityEntry> Rejected { get; set; } = [];
}
