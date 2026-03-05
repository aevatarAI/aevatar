using System.Text.Json;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.GAgents;
using Aevatar.App.GAgents.Rules;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.Application.Services;

public sealed class SyncAppService : ISyncAppService
{
    private readonly IActorAccessAppService _actors;
    private readonly IProjectionDocumentStore<AppSyncEntityReadModel, string> _syncStore;
    private readonly IProjectionDocumentStore<AppSyncEntityLastResultReadModel, string> _syncLastResultStore;

    public SyncAppService(
        IActorAccessAppService actors,
        IProjectionDocumentStore<AppSyncEntityReadModel, string> syncStore,
        IProjectionDocumentStore<AppSyncEntityLastResultReadModel, string> syncLastResultStore)
    {
        _actors = actors;
        _syncStore = syncStore;
        _syncLastResultStore = syncLastResultStore;
    }

    public async Task<SyncResult> SyncAsync(
        string syncId,
        string userId,
        int clientRevision,
        IReadOnlyList<SyncEntity> incomingEntities)
    {
        var syncKey = _actors.ResolveActorId<SyncEntityGAgent>(userId);
        var currentModel = await _syncStore.GetAsync(syncKey);
        var currentEntities = ToProtoMap(currentModel?.Entities);
        var currentRevision = currentModel?.ServerRevision ?? 0;

        var accepted = new List<string>();
        var rejected = new List<RejectedEntity>();
        var updatedEntities = new Dictionary<string, SyncEntity>(
            currentEntities.Select(kv => KeyValuePair.Create(kv.Key, kv.Value.Clone())),
            StringComparer.Ordinal);

        foreach (var incoming in incomingEntities)
        {
            currentEntities.TryGetValue(incoming.ClientId, out var existing);
            var rule = SyncRules.Evaluate(existing, incoming);

            switch (rule)
            {
                case SyncRuleResult.Created:
                    currentRevision++;
                    var created = incoming.Clone();
                    created.UserId = userId;
                    created.Revision = currentRevision;
                    if (created.DeletedAt is not null) created.BankEligible = false;
                    updatedEntities[created.ClientId] = created;
                    accepted.Add(created.ClientId);
                    break;

                case SyncRuleResult.Updated:
                    currentRevision++;
                    var updated = incoming.Clone();
                    updated.UserId = userId;
                    updated.Revision = currentRevision;
                    if (existing!.Source == EntitySource.Ai
                        && !string.IsNullOrEmpty(existing.BankHash)
                        && incoming.BankHash != existing.BankHash)
                    {
                        updated.Source = EntitySource.Edited;
                        updated.BankEligible = false;
                    }
                    if (updated.DeletedAt is not null) updated.BankEligible = false;

                    if (incoming.DeletedAt is not null && existing!.DeletedAt is null)
                        ApplyCascadeDeletes(userId, updated.ClientId, updatedEntities, ref currentRevision);

                    updatedEntities[updated.ClientId] = updated;
                    accepted.Add(updated.ClientId);
                    break;

                case SyncRuleResult.Stale:
                    rejected.Add(new RejectedEntity
                    {
                        ClientId = incoming.ClientId,
                        ServerRevision = existing?.Revision ?? 0,
                        Reason = existing is not null
                            ? $"Stale: client={incoming.Revision}, server={existing.Revision}"
                            : "Unknown entity with revision > 0"
                    });
                    break;
            }
        }

        var deltaEntities = updatedEntities
            .Where(kv => kv.Value.Revision > clientRevision)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var cmd = new EntitiesSyncRequestedEvent
        {
            SyncId = syncId,
            UserId = userId,
            ClientRevision = clientRevision,
        };
        cmd.IncomingEntities.AddRange(incomingEntities);

        await _actors.SendCommandAsync<SyncEntityGAgent>(userId, cmd);

        return new SyncResult(syncId, currentRevision, deltaEntities, accepted, rejected);
    }

    public async Task<StateResult> GetStateAsync(string userId)
    {
        var readModel = await _syncStore.GetAsync(_actors.ResolveActorId<SyncEntityGAgent>(userId));
        if (readModel is null)
            return new StateResult(new Dictionary<string, Dictionary<string, SyncEntityEntry>>(), 0);

        return BuildStateResult(readModel);
    }

    private static void ApplyCascadeDeletes(
        string userId,
        string parentClientId,
        Dictionary<string, SyncEntity> entities,
        ref int currentRevision,
        int depth = 0)
    {
        if (depth > 5) return;

        var toDelete = entities
            .Where(kv => kv.Value.DeletedAt is null
                && kv.Value.Refs.Values.Contains(parentClientId))
            .Select(kv => kv.Key)
            .ToList();

        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        foreach (var clientId in toDelete)
        {
            currentRevision++;
            var entity = entities[clientId].Clone();
            entity.DeletedAt = now;
            entity.Revision = currentRevision;
            entity.BankEligible = false;
            entities[clientId] = entity;
            ApplyCascadeDeletes(userId, clientId, entities, ref currentRevision, depth + 1);
        }
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

    private static Dictionary<string, SyncEntity> ToProtoMap(
        Dictionary<string, SyncEntityEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
            return new Dictionary<string, SyncEntity>(StringComparer.Ordinal);

        var result = new Dictionary<string, SyncEntity>(entries.Count, StringComparer.Ordinal);
        foreach (var (key, e) in entries)
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
            result[key] = proto;
        }
        return result;
    }

    private static EntitySource StringToSource(string? source) => source switch
    {
        "bank" => EntitySource.Bank,
        "user" => EntitySource.User,
        "edited" => EntitySource.Edited,
        _ => EntitySource.Ai
    };

    private static Struct? JsonToStruct(JsonElement? element)
    {
        if (element is null) return null;
        var json = element.Value.GetRawText();
        return Google.Protobuf.JsonParser.Default.Parse<Struct>(json);
    }

    private static Timestamp? OffsetToTimestamp(DateTimeOffset? offset) =>
        offset.HasValue ? Timestamp.FromDateTimeOffset(offset.Value) : null;
}
