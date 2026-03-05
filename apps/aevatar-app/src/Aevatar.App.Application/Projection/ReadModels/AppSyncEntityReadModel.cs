using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.ReadModels;

public sealed class AppSyncEntityReadModel : IProjectionReadModel
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public int ServerRevision { get; set; }
    public Dictionary<string, SyncEntityEntry> Entities { get; set; } = new(StringComparer.Ordinal);
}
