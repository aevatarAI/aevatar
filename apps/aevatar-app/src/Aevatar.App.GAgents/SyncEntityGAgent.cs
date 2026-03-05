using Aevatar.App.GAgents.Rules;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.GAgents;

public sealed partial class SyncEntityGAgent : GAgentBase<SyncEntityState>
{
    [EventHandler]
    public async Task HandleSyncEntities(EntitiesSyncRequestedEvent @event)
    {
        if (@event.IncomingEntities.Count > 500)
            throw new ArgumentException("Too many entities per sync (max 500)");

        var currentRevision = State.Meta?.Revision ?? 0;
        var accepted = new List<string>();
        var rejected = new List<RejectedEntity>();
        var domainEvents = new List<IMessage>();

        foreach (var incoming in @event.IncomingEntities)
        {
            incoming.UserId = @event.UserId;
            State.Entities.TryGetValue(incoming.ClientId, out var existing);
            var rule = SyncRules.Evaluate(existing, incoming);

            switch (rule)
            {
                case SyncRuleResult.Created:
                {
                    currentRevision++;
                    var entity = incoming.Clone();
                    entity.Revision = currentRevision;
                    if (entity.DeletedAt is not null)
                        entity.BankEligible = false;

                    accepted.Add(entity.ClientId);
                    domainEvents.Add(BuildEntityCreatedEvent(entity));
                    break;
                }
                case SyncRuleResult.Updated:
                {
                    var previousRevision = existing!.Revision;
                    currentRevision++;

                    var outputChanged = existing.Source == EntitySource.Ai
                        && !string.IsNullOrEmpty(existing.BankHash)
                        && incoming.BankHash != existing.BankHash;

                    var updated = incoming.Clone();
                    updated.Revision = currentRevision;
                    if (outputChanged)
                    {
                        updated.Source = EntitySource.Edited;
                        updated.BankEligible = false;
                    }
                    if (updated.DeletedAt is not null)
                        updated.BankEligible = false;

                    var deletedNow = incoming.DeletedAt is not null && existing.DeletedAt is null;
                    if (deletedNow)
                        CollectCascadeDeleteEvents(@event.UserId, updated.ClientId, ref currentRevision, domainEvents);

                    accepted.Add(updated.ClientId);
                    domainEvents.Add(BuildEntityUpdatedEvent(updated, previousRevision));
                    break;
                }
                case SyncRuleResult.Stale:
                {
                    var reason = existing is not null
                        ? $"Stale: client={incoming.Revision}, server={existing.Revision}"
                        : "Unknown entity with revision > 0";
                    rejected.Add(new RejectedEntity
                    {
                        ClientId = incoming.ClientId,
                        ServerRevision = existing?.Revision ?? 0,
                        Reason = reason
                    });
                    break;
                }
            }
        }

        var syncEvent = new EntitiesSyncedEvent
        {
            SyncId = @event.SyncId,
            UserId = @event.UserId,
            ClientRevision = @event.ClientRevision,
            ServerRevision = currentRevision,
            AcceptedCount = accepted.Count,
            RejectedCount = rejected.Count,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };
        syncEvent.Accepted.AddRange(accepted);
        syncEvent.Rejected.AddRange(rejected);
        domainEvents.Add(syncEvent);

        await PersistDomainEventsAsync(domainEvents);

        foreach (var domainEvent in domainEvents)
            await PublishAsync(domainEvent);
    }

    [EventHandler]
    public async Task HandleSoftDeleteEntities(EntitiesSoftDeleteRequestedEvent @event)
    {
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var evt = new AccountDeletedEvent
        {
            UserId = @event.UserId,
            Mode = "soft",
            EntitiesAnonymizedCount = State.Entities.Count,
            DeletedAt = now
        };
        await PersistDomainEventAsync(evt);
        await PublishAsync(evt);
    }

    [EventHandler]
    public async Task HandleHardDeleteEntities(EntitiesHardDeleteRequestedEvent @event)
    {
        var count = State.Entities.Count;
        var evt = new AccountDeletedEvent
        {
            UserId = string.Empty,
            Mode = "hard",
            EntitiesDeletedCount = count,
            DeletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };
        await PersistDomainEventAsync(evt);
        await PublishAsync(evt);
    }
}
