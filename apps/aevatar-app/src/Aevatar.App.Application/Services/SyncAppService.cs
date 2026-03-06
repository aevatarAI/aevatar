using Aevatar.App.Application.Completion;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.GAgents;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.App.Application.Services;

public sealed class SyncAppService : ISyncAppService
{
    private readonly IActorAccessAppService _actors;
    private readonly ICompletionPort _completionPort;
    private readonly IProjectionDocumentStore<AppSyncEntityReadModel, string> _syncStore;
    private readonly ILogger<SyncAppService> _logger;

    public SyncAppService(
        IActorAccessAppService actors,
        ICompletionPort completionPort,
        IProjectionDocumentStore<AppSyncEntityReadModel, string> syncStore,
        ILogger<SyncAppService> logger)
    {
        _actors = actors;
        _completionPort = completionPort;
        _syncStore = syncStore;
        _logger = logger;
    }

    public async Task<SyncResult> SyncAsync(
        string syncId,
        string userId,
        int clientRevision,
        IReadOnlyList<SyncEntity> incomingEntities)
    {
        var cmd = new EntitiesSyncRequestedEvent
        {
            SyncId = syncId,
            UserId = userId,
            ClientRevision = clientRevision,
        };
        cmd.IncomingEntities.AddRange(incomingEntities);

        var waitTask = _completionPort.WaitAsync(syncId);
        await _actors.SendCommandAsync<SyncEntityGAgent>(userId, cmd);
        await waitTask;
        _logger.LogInformation("Sync projection completed. syncId={SyncId} userId={UserId}", syncId, userId);

        var syncKey = _actors.ResolveActorId<SyncEntityGAgent>(userId);
        var readModel = await _syncStore.GetAsync(syncKey);
        if (readModel is null)
            throw new InvalidOperationException($"ReadModel not found after sync completion: {syncKey}");

        _logger.LogDebug(
            "ReadModel loaded. syncKey={SyncKey} syncResultsCount={SyncResultsCount} syncResultOrderCount={OrderCount} revision={Revision}",
            syncKey, readModel.SyncResults.Count, readModel.SyncResultOrder.Count, readModel.ServerRevision);

        if (!readModel.SyncResults.TryGetValue(syncId, out var syncResult))
            throw new InvalidOperationException($"SyncResult for syncId '{syncId}' not found in ReadModel");

        return BuildSyncResult(readModel, syncResult);
    }

    public async Task<StateResult> GetStateAsync(string userId)
    {
        var readModel = await _syncStore.GetAsync(_actors.ResolveActorId<SyncEntityGAgent>(userId));
        if (readModel is null)
            return new StateResult(new Dictionary<string, Dictionary<string, SyncEntityEntry>>(), 0);

        return BuildStateResult(readModel);
    }

    private static SyncResult BuildSyncResult(AppSyncEntityReadModel model, SyncResultEntry result)
    {
        var deltaEntities = new Dictionary<string, SyncEntity>(StringComparer.Ordinal);
        foreach (var (key, entry) in model.Entities)
        {
            if (entry.Revision <= result.ClientRevision) continue;
            deltaEntities[key] = EntryToProto(entry);
        }

        var rejected = result.Rejected
            .Select(r => new RejectedEntity
            {
                ClientId = r.ClientId,
                ServerRevision = r.ServerRevision,
                Reason = r.Reason,
            })
            .ToList();

        return new SyncResult(
            result.SyncId,
            result.ServerRevision,
            deltaEntities,
            [..result.Accepted],
            rejected);
    }

    private static StateResult BuildStateResult(AppSyncEntityReadModel readModel)
    {
        var grouped = new Dictionary<string, Dictionary<string, SyncEntityEntry>>();
        foreach (var kv in readModel.Entities)
        {
            if (kv.Value.DeletedAt is not null) continue;
            if (!grouped.TryGetValue(kv.Value.EntityType, out var typeMap))
            {
                typeMap = new Dictionary<string, SyncEntityEntry>();
                grouped[kv.Value.EntityType] = typeMap;
            }
            typeMap[kv.Key] = kv.Value;
        }
        return new StateResult(grouped, readModel.ServerRevision);
    }

    private static SyncEntity EntryToProto(SyncEntityEntry e)
    {
        var proto = new SyncEntity
        {
            ClientId = e.ClientId,
            EntityType = e.EntityType,
            UserId = e.UserId,
            Revision = e.Revision,
            Position = e.Position,
            Source = StringToSource(e.Source),
            BankEligible = e.BankEligible,
            BankHash = e.BankHash,
            Inputs = JsonToStruct(e.Inputs),
            Output = JsonToStruct(e.Output),
            State = JsonToStruct(e.State),
            DeletedAt = OffsetToTimestamp(e.DeletedAt),
            CreatedAt = OffsetToTimestamp(e.CreatedAt),
            UpdatedAt = OffsetToTimestamp(e.UpdatedAt),
        };
        foreach (var (rk, rv) in e.Refs)
            proto.Refs[rk] = rv;
        return proto;
    }

    private static EntitySource StringToSource(string? source) => source switch
    {
        "bank" => EntitySource.Bank,
        "user" => EntitySource.User,
        "edited" => EntitySource.Edited,
        _ => EntitySource.Ai
    };

    private static Google.Protobuf.WellKnownTypes.Struct? JsonToStruct(System.Text.Json.JsonElement? element)
    {
        if (element is null) return null;
        var json = element.Value.GetRawText();
        return Google.Protobuf.JsonParser.Default.Parse<Google.Protobuf.WellKnownTypes.Struct>(json);
    }

    private static Google.Protobuf.WellKnownTypes.Timestamp? OffsetToTimestamp(DateTimeOffset? offset) =>
        offset.HasValue ? Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(offset.Value) : null;
}
