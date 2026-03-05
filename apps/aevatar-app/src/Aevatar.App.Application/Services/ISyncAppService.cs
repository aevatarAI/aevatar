using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.GAgents;

namespace Aevatar.App.Application.Services;

public interface ISyncAppService
{
    Task<SyncResult> SyncAsync(
        string syncId,
        string userId,
        int clientRevision,
        IReadOnlyList<SyncEntity> incomingEntities);

    Task<StateResult> GetStateAsync(string userId);
}

public sealed record StateResult(
    Dictionary<string, Dictionary<string, SyncEntityEntry>> GroupedEntities,
    int ServerRevision);
