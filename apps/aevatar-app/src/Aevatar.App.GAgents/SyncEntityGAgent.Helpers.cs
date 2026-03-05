using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.GAgents;

public sealed partial class SyncEntityGAgent
{
    private void CollectCascadeDeleteEvents(
        string userId,
        string parentClientId,
        ref int currentRevision,
        List<IMessage> domainEvents,
        int depth = 0)
    {
        if (depth > 5) return;

        var toDelete = State.Entities
            .Where(kv => kv.Value.DeletedAt is null
                && kv.Value.Refs.Values.Contains(parentClientId))
            .Select(kv => kv.Key)
            .ToList();

        if (toDelete.Count == 0) return;

        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var deletedIds = new List<string>();

        foreach (var clientId in toDelete)
        {
            currentRevision++;
            var entity = State.Entities[clientId];
            deletedIds.Add(clientId);
            domainEvents.Add(new EntityDeletedEvent
            {
                UserId = entity.UserId,
                ClientId = entity.ClientId,
                EntityType = entity.EntityType,
                Revision = currentRevision,
                DeletedAt = now
            });
        }

        domainEvents.Add(new CascadeDeleteEvent
        {
            UserId = userId,
            ParentClientId = parentClientId,
            Depth = depth,
            DeletedAt = now,
            DeletedClientIds = { deletedIds }
        });

        foreach (var clientId in toDelete)
            CollectCascadeDeleteEvents(userId, clientId, ref currentRevision, domainEvents, depth + 1);
    }

    private static EntityCreatedEvent BuildEntityCreatedEvent(SyncEntity entity) => new()
    {
        UserId = entity.UserId,
        ClientId = entity.ClientId,
        EntityType = entity.EntityType,
        Revision = entity.Revision,
        Source = entity.Source,
        Refs = { entity.Refs },
        Inputs = entity.Inputs,
        Output = entity.Output,
        State = entity.State,
        Position = entity.Position,
        BankEligible = entity.BankEligible,
        BankHash = entity.BankHash,
        CreatedAt = entity.CreatedAt
    };

    private static EntityUpdatedEvent BuildEntityUpdatedEvent(
        SyncEntity entity, int previousRevision) => new()
    {
        UserId = entity.UserId,
        ClientId = entity.ClientId,
        EntityType = entity.EntityType,
        PreviousRevision = previousRevision,
        Revision = entity.Revision,
        Source = entity.Source,
        Refs = { entity.Refs },
        Inputs = entity.Inputs,
        Output = entity.Output,
        State = entity.State,
        Position = entity.Position,
        BankEligible = entity.BankEligible,
        BankHash = entity.BankHash,
        UpdatedAt = entity.UpdatedAt
    };

    private static EntityDeletedEvent BuildEntityDeletedEvent(SyncEntity entity) => new()
    {
        UserId = entity.UserId,
        ClientId = entity.ClientId,
        EntityType = entity.EntityType,
        Revision = entity.Revision,
        DeletedAt = entity.DeletedAt
    };

    protected override SyncEntityState TransitionState(SyncEntityState current, IMessage evt)
    {
        switch (evt)
        {
            case EntityCreatedEvent created:
            {
                var entity = new SyncEntity
                {
                    UserId = created.UserId,
                    ClientId = created.ClientId,
                    EntityType = created.EntityType,
                    Revision = created.Revision,
                    Source = created.Source,
                    Inputs = created.Inputs,
                    Output = created.Output,
                    State = created.State,
                    Position = created.Position,
                    BankEligible = created.BankEligible,
                    BankHash = created.BankHash,
                    CreatedAt = created.CreatedAt
                };
                entity.Refs.Add(created.Refs);
                current.Entities[entity.ClientId] = entity;
                current.Meta ??= new SyncMeta();
                current.Meta.UserId = created.UserId;
                current.Meta.Revision = Math.Max(current.Meta.Revision, created.Revision);
                return current;
            }
            case EntityUpdatedEvent updated:
            {
                var entity = new SyncEntity
                {
                    UserId = updated.UserId,
                    ClientId = updated.ClientId,
                    EntityType = updated.EntityType,
                    Revision = updated.Revision,
                    Source = updated.Source,
                    Inputs = updated.Inputs,
                    Output = updated.Output,
                    State = updated.State,
                    Position = updated.Position,
                    BankEligible = updated.BankEligible,
                    BankHash = updated.BankHash,
                    UpdatedAt = updated.UpdatedAt
                };
                entity.Refs.Add(updated.Refs);
                current.Entities[entity.ClientId] = entity;
                current.Meta ??= new SyncMeta();
                current.Meta.UserId = updated.UserId;
                current.Meta.Revision = Math.Max(current.Meta.Revision, updated.Revision);
                return current;
            }
            case EntityDeletedEvent deleted:
            {
                if (current.Entities.TryGetValue(deleted.ClientId, out var entity))
                {
                    entity.DeletedAt = deleted.DeletedAt;
                    entity.Revision = deleted.Revision;
                    entity.BankEligible = false;
                    current.Entities[deleted.ClientId] = entity;
                }
                current.Meta ??= new SyncMeta();
                current.Meta.UserId = deleted.UserId;
                current.Meta.Revision = Math.Max(current.Meta.Revision, deleted.Revision);
                return current;
            }
            case EntitiesSyncedEvent synced:
            {
                current.Meta ??= new SyncMeta();
                current.Meta.UserId = synced.UserId;
                current.Meta.Revision = Math.Max(current.Meta.Revision, synced.ServerRevision);
                current.LastSyncResult = new LastSyncResult
                {
                    SyncId = synced.SyncId,
                    ClientRevision = synced.ClientRevision,
                    ServerRevision = synced.ServerRevision,
                };
                current.LastSyncResult.Accepted.AddRange(synced.Accepted);
                current.LastSyncResult.Rejected.AddRange(synced.Rejected);
                return current;
            }
            case AccountDeletedEvent deleted:
            {
                if (string.Equals(deleted.Mode, "hard", StringComparison.OrdinalIgnoreCase))
                {
                    current.Entities.Clear();
                    current.Meta = null;
                    return current;
                }

                if (string.Equals(deleted.Mode, "soft", StringComparison.OrdinalIgnoreCase))
                {
                    var deletedUserId = $"deleted_{deleted.UserId}";
                    foreach (var kv in current.Entities)
                    {
                        AnonymizeEntity(kv.Value, deletedUserId, deleted.DeletedAt);
                    }

                    if (current.Meta is not null)
                        current.Meta.UserId = deletedUserId;
                }
                return current;
            }
        }

        return current;
    }

    internal static void AnonymizeEntity(SyncEntity entity, string deletedUserId, Timestamp? deletedAt)
    {
        entity.UserId = deletedUserId;
        entity.DeletedAt ??= deletedAt;
        entity.BankEligible = false;
        entity.BankHash = string.Empty;

        if (entity.Inputs is not null)
        {
            entity.Inputs.Fields.Clear();
            entity.Inputs.Fields["userGoal"] = Value.ForString("[deleted]");
        }

        if (entity.Output is not null)
            entity.Output.Fields.Clear();
    }
}
