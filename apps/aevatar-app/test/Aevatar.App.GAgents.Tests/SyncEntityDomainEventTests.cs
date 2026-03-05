using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.App.GAgents.Tests;

public sealed class SyncEntityDomainEventTests
{
    private static Task SendSyncAsync(
        SyncEntityGAgent agent, string syncId, string userId,
        int clientRev, IReadOnlyList<SyncEntity> entities)
    {
        var cmd = new EntitiesSyncRequestedEvent
        {
            SyncId = syncId,
            UserId = userId,
            ClientRevision = clientRev,
        };
        cmd.IncomingEntities.AddRange(entities);
        return GAgentTestHelper.SendCommandAsync(agent, cmd);
    }

    [Fact]
    public async Task SyncAsync_Create_EmitsEntityCreatedAndEntitiesSyncedEvents()
    {
        var store = new InMemoryEventStore();
        var services = GAgentTestHelper.BuildServices(store);
        var (agent, _) = GAgentTestHelper.Create<SyncEntityGAgent, SyncEntityState>("sync-entity:evt-create", services);
        await agent.ActivateAsync();

        await SendSyncAsync(agent, "s1", "user1", 0, [MakeEntity("c1"), MakeEntity("c2")]);

        var events = await store.GetEventsAsync("sync-entity:evt-create");
        events.Should().NotBeEmpty();

        var unpacked = events.SelectMany(e =>
        {
            var list = new List<object>();
            if (e.EventData is null) return list;
            if (e.EventData.Is(EntitiesSyncedEvent.Descriptor))
                list.Add(e.EventData.Unpack<EntitiesSyncedEvent>());
            else if (e.EventData.Is(EntityCreatedEvent.Descriptor))
                list.Add(e.EventData.Unpack<EntityCreatedEvent>());
            else if (e.EventData.Is(EntityUpdatedEvent.Descriptor))
                list.Add(e.EventData.Unpack<EntityUpdatedEvent>());
            return list;
        }).ToList();

        unpacked.OfType<EntityCreatedEvent>().Should().HaveCount(2);
        unpacked.OfType<EntitiesSyncedEvent>().Should().ContainSingle();

        var sync = unpacked.OfType<EntitiesSyncedEvent>().Single();
        sync.SyncId.Should().Be("s1");
        sync.UserId.Should().Be("user1");
        sync.AcceptedCount.Should().Be(2);
        sync.RejectedCount.Should().Be(0);
        sync.Accepted.Should().Contain("c1").And.Contain("c2");
    }

    [Fact]
    public async Task SyncAsync_Update_EmitsEntityUpdatedEvent()
    {
        var store = new InMemoryEventStore();
        var services = GAgentTestHelper.BuildServices(store);
        var (agent, _) = GAgentTestHelper.Create<SyncEntityGAgent, SyncEntityState>("sync-entity:evt-update", services);
        await agent.ActivateAsync();

        await SendSyncAsync(agent, "s1", "user1", 0, [MakeEntity("c1")]);

        var updated = MakeEntity("c1", revision: 1);
        await SendSyncAsync(agent, "s2", "user1", 1, [updated]);

        var events = await store.GetEventsAsync("sync-entity:evt-update");
        var updateEvents = events
            .Where(e => e.EventData?.Is(EntityUpdatedEvent.Descriptor) == true)
            .Select(e => e.EventData!.Unpack<EntityUpdatedEvent>())
            .ToList();

        updateEvents.Should().ContainSingle();
        updateEvents[0].ClientId.Should().Be("c1");
        updateEvents[0].PreviousRevision.Should().Be(1);
    }

    [Fact]
    public async Task SyncAsync_Stale_DoesNotEmitEntityEvent()
    {
        var store = new InMemoryEventStore();
        var services = GAgentTestHelper.BuildServices(store);
        var (agent, _) = GAgentTestHelper.Create<SyncEntityGAgent, SyncEntityState>("sync-entity:evt-stale", services);
        await agent.ActivateAsync();

        await SendSyncAsync(agent, "s1", "user1", 0, [MakeEntity("c1")]);
        var versionAfterCreate = await store.GetVersionAsync("sync-entity:evt-stale");

        var stale = MakeEntity("c1", revision: 0);
        await SendSyncAsync(agent, "s2", "user1", 0, [stale]);

        var newEvents = await store.GetEventsAsync("sync-entity:evt-stale", versionAfterCreate);
        var syncEvents = newEvents
            .Where(e => e.EventData?.Is(EntitiesSyncedEvent.Descriptor) == true)
            .Select(e => e.EventData!.Unpack<EntitiesSyncedEvent>())
            .ToList();

        syncEvents.Should().ContainSingle();
        syncEvents[0].RejectedCount.Should().Be(1);

        newEvents
            .Where(e => e.EventData?.Is(EntityCreatedEvent.Descriptor) == true)
            .Should().BeEmpty("stale should not emit entity events");
    }

    [Fact]
    public async Task SyncAsync_CascadeDelete_EmitsCascadeDeleteEvent()
    {
        var store = new InMemoryEventStore();
        var services = GAgentTestHelper.BuildServices(store);
        var (agent, _) = GAgentTestHelper.Create<SyncEntityGAgent, SyncEntityState>("sync-entity:evt-cascade", services);
        await agent.ActivateAsync();

        var parent = MakeEntity("parent");
        var child = MakeEntity("child", entityType: "affirmation");
        child.Refs["manifestation"] = "parent";
        await SendSyncAsync(agent, "s1", "user1", 0, [parent, child]);
        var versionAfterCreate = await store.GetVersionAsync("sync-entity:evt-cascade");

        var serverRev = agent.State.Entities["parent"].Revision;
        var parentDel = MakeEntity("parent", revision: serverRev,
            deletedAt: Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow));
        await SendSyncAsync(agent, "s2", "user1", agent.State.Meta.Revision, [parentDel]);

        var newEvents = await store.GetEventsAsync("sync-entity:evt-cascade", versionAfterCreate);
        var cascadeEvents = newEvents
            .Where(e => e.EventData?.Is(CascadeDeleteEvent.Descriptor) == true)
            .Select(e => e.EventData!.Unpack<CascadeDeleteEvent>())
            .ToList();

        cascadeEvents.Should().ContainSingle();
        cascadeEvents[0].ParentClientId.Should().Be("parent");
        cascadeEvents[0].DeletedClientIds.Should().Contain("child");
    }

    private static SyncEntity MakeEntity(
        string clientId,
        string entityType = "manifestation",
        int revision = 0,
        Timestamp? deletedAt = null)
    {
        var e = new SyncEntity
        {
            ClientId = clientId,
            EntityType = entityType,
            Revision = revision,
            Source = EntitySource.Ai,
            BankEligible = true,
            BankHash = "",
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        if (deletedAt is not null) e.DeletedAt = deletedAt;
        return e;
    }
}
