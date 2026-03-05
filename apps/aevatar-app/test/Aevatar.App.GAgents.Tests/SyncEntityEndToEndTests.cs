using Aevatar.App.Application.Projection;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Reducers;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.GAgents.Tests;

public sealed class SyncEntityEndToEndTests
{
    private static SyncEntity MakeEntity(
        string clientId,
        string entityType = "manifestation",
        int revision = 0,
        EntitySource source = EntitySource.User,
        string bankHash = "hash",
        Timestamp? deletedAt = null)
    {
        var e = new SyncEntity
        {
            ClientId = clientId,
            EntityType = entityType,
            Revision = revision,
            Source = source,
            BankEligible = true,
            BankHash = bankHash,
            CreatedAt = Timestamp.FromDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            UpdatedAt = Timestamp.FromDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        };
        if (deletedAt is not null) e.DeletedAt = deletedAt;
        return e;
    }

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

    private static EventEnvelope ToProjectionEnvelope(StateEvent stateEvent) => new()
    {
        Id = stateEvent.EventId,
        Timestamp = stateEvent.Timestamp,
        Payload = stateEvent.EventData,
        PublisherId = stateEvent.AgentId,
        Direction = EventDirection.Self,
    };

    private static AppProjectionContext CreateContext(string actorId) => new()
    {
        ActorId = actorId,
        RootActorId = actorId,
    };

    [Fact]
    public async Task AddSecondEntity_GAgentState_And_Reducer_Consistent()
    {
        var store = new InMemoryEventStore();
        var services = GAgentTestHelper.BuildServices(store);
        var (agent, _) = GAgentTestHelper.Create<SyncEntityGAgent, SyncEntityState>(
            "sync-entity:e2e-add-second", services);
        await agent.ActivateAsync();

        await SendSyncAsync(agent, "s1", "user1", 0, [MakeEntity("c1")]);
        await SendSyncAsync(agent, "s2", "user1", agent.State.Meta.Revision,
            [MakeEntity("c2", entityType: "affirmation", bankHash: "hash2")]);

        agent.State.Entities.Should().HaveCount(2);
        agent.State.Entities.Should().ContainKey("c1");
        agent.State.Entities.Should().ContainKey("c2");
        agent.State.Entities["c1"].Revision.Should().Be(1);
        agent.State.Entities["c2"].Revision.Should().Be(2);
        agent.State.Entities["c2"].EntityType.Should().Be("affirmation");
        agent.State.Meta.Revision.Should().Be(2);

        var stateEvents = await store.GetEventsAsync("sync-entity:e2e-add-second");
        var createdReducer = new EntityCreatedEventReducer();
        var readModel = new AppSyncEntityReadModel { Id = "syncentity:user1" };
        var ctx = CreateContext("syncentity:user1");
        var now = DateTimeOffset.UtcNow;

        foreach (var se in stateEvents)
        {
            var envelope = ToProjectionEnvelope(se);
            createdReducer.Reduce(readModel, ctx, envelope, now);
        }

        readModel.Entities.Should().HaveCount(2);
        readModel.Entities.Should().ContainKey("c1");
        readModel.Entities.Should().ContainKey("c2");
        readModel.Entities["c1"].EntityType.Should().Be("manifestation");
        readModel.Entities["c1"].Source.Should().Be("user");
        readModel.Entities["c1"].Revision.Should().Be(1);
        readModel.Entities["c2"].EntityType.Should().Be("affirmation");
        readModel.Entities["c2"].BankHash.Should().Be("hash2");
        readModel.Entities["c2"].Revision.Should().Be(2);
        readModel.ServerRevision.Should().Be(2);
        readModel.UserId.Should().Be("user1");
    }

    [Fact]
    public async Task DeleteEntity_GAgentState_And_Reducer_Consistent()
    {
        var store = new InMemoryEventStore();
        var services = GAgentTestHelper.BuildServices(store);
        var (agent, _) = GAgentTestHelper.Create<SyncEntityGAgent, SyncEntityState>(
            "sync-entity:e2e-delete", services);
        await agent.ActivateAsync();

        await SendSyncAsync(agent, "s1", "user1", 0,
            [MakeEntity("c1"), MakeEntity("c2", entityType: "affirmation", bankHash: "hash2")]);

        var revAfterCreate = agent.State.Meta.Revision;
        agent.State.Entities.Should().HaveCount(2);

        var deletedAt = Timestamp.FromDateTime(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        var delEntity = MakeEntity("c1",
            revision: agent.State.Entities["c1"].Revision,
            deletedAt: deletedAt);

        await SendSyncAsync(agent, "s2", "user1", revAfterCreate, [delEntity]);

        agent.State.Entities["c1"].DeletedAt.Should().NotBeNull();
        agent.State.Entities["c1"].BankEligible.Should().BeFalse();
        agent.State.Entities["c2"].DeletedAt.Should().BeNull();
        agent.State.Entities["c2"].BankEligible.Should().BeTrue();
        agent.State.Meta.Revision.Should().BeGreaterThan(revAfterCreate);

        var stateEvents = await store.GetEventsAsync("sync-entity:e2e-delete");
        var createdReducer = new EntityCreatedEventReducer();
        var updatedReducer = new EntityUpdatedEventReducer();
        var readModel = new AppSyncEntityReadModel { Id = "syncentity:user1" };
        var ctx = CreateContext("syncentity:user1");
        var now = DateTimeOffset.UtcNow;

        foreach (var se in stateEvents)
        {
            var envelope = ToProjectionEnvelope(se);
            createdReducer.Reduce(readModel, ctx, envelope, now);
            updatedReducer.Reduce(readModel, ctx, envelope, now);
        }

        readModel.Entities.Should().HaveCount(2);
        readModel.Entities["c1"].DeletedAt.Should().Be(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        readModel.Entities["c1"].BankEligible.Should().BeFalse();
        readModel.Entities["c1"].Revision.Should().BeGreaterThan(1);
        readModel.Entities["c2"].DeletedAt.Should().BeNull();
        readModel.Entities["c2"].BankEligible.Should().BeTrue();
        readModel.Entities["c2"].EntityType.Should().Be("affirmation");
        readModel.UserId.Should().Be("user1");
    }
}
