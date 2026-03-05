using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.GAgents.Tests;

public sealed class SyncEntityStateTests
{
    private static SyncEntity MakeEntity(
        string clientId,
        string entityType = "manifestation",
        Timestamp? deletedAt = null)
    {
        var e = new SyncEntity
        {
            ClientId = clientId,
            EntityType = entityType,
            Revision = 0,
            Source = EntitySource.Ai,
            BankEligible = true,
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
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

    private sealed record GAgentStateSnapshot(
        Dictionary<string, Dictionary<string, SyncEntity>> GroupedEntities,
        int ServerRevision);

    private static GAgentStateSnapshot BuildStateResult(SyncEntityGAgent agent)
    {
        var grouped = agent.State.Entities
            .Where(kv => kv.Value.DeletedAt is null)
            .GroupBy(kv => kv.Value.EntityType)
            .ToDictionary(g => g.Key, g => g.ToDictionary(kv => kv.Key, kv => kv.Value));
        return new GAgentStateSnapshot(grouped, agent.State.Meta?.Revision ?? 0);
    }

    [Fact]
    public async Task GetState_FiltersDeletedEntities()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("state:filter");
        var alive = MakeEntity("alive");
        var dead = MakeEntity("dead", deletedAt: Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow));
        await SendSyncAsync(agent, "s1", "user1", 0, [alive, dead]);

        var state = BuildStateResult(agent);

        state.GroupedEntities.Should().ContainKey("manifestation");
        state.GroupedEntities["manifestation"].Should().ContainKey("alive");
        state.GroupedEntities["manifestation"].Should().NotContainKey("dead");
    }

    [Fact]
    public async Task GetState_GroupsByEntityType()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("state:group");
        var m = MakeEntity("m1", "manifestation");
        var a = MakeEntity("a1", "affirmation");
        await SendSyncAsync(agent, "s1", "user1", 0, [m, a]);

        var state = BuildStateResult(agent);

        state.GroupedEntities.Should().ContainKey("manifestation");
        state.GroupedEntities.Should().ContainKey("affirmation");
        state.GroupedEntities["manifestation"].Should().ContainKey("m1");
        state.GroupedEntities["affirmation"].Should().ContainKey("a1");
    }

    [Fact]
    public async Task GetState_ReturnsServerRevision()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("state:rev");
        await SendSyncAsync(agent, "s1", "user1", 0, [MakeEntity("c1"), MakeEntity("c2")]);

        var state = BuildStateResult(agent);

        state.ServerRevision.Should().Be(2);
    }

    [Fact]
    public async Task SoftDeleteForAccount_AnonymizesEntities()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("state:soft-del");
        var entity = MakeEntity("m1");
        entity.Inputs = new Struct();
        entity.Inputs.Fields["userGoal"] = Google.Protobuf.WellKnownTypes.Value.ForString("my goal");
        entity.Inputs.Fields["extra"] = Google.Protobuf.WellKnownTypes.Value.ForString("some data");
        entity.Output = new Struct();
        entity.Output.Fields["mantra"] = Google.Protobuf.WellKnownTypes.Value.ForString("secret mantra");
        entity.BankHash = "hash123";
        await SendSyncAsync(agent, "s1", "user1", 0, [entity]);

        await GAgentTestHelper.SendCommandAsync(agent,
            new EntitiesSoftDeleteRequestedEvent { UserId = "user1" });

        var anon = agent.State.Entities["m1"];
        anon.UserId.Should().Be("deleted_user1");
        anon.Inputs.Fields.Should().ContainKey("userGoal");
        anon.Inputs.Fields["userGoal"].StringValue.Should().Be("[deleted]");
        anon.Inputs.Fields.Should().NotContainKey("extra");
        anon.Output.Fields.Should().BeEmpty();
        anon.BankEligible.Should().BeFalse();
        anon.BankHash.Should().BeEmpty();
        anon.DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HardDeleteForAccount_ClearsEverything()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("state:hard-del");
        await SendSyncAsync(agent, "s1", "user1", 0, [MakeEntity("m1"), MakeEntity("m2")]);

        await GAgentTestHelper.SendCommandAsync(agent, new EntitiesHardDeleteRequestedEvent());

        agent.State.Entities.Should().BeEmpty();
        agent.State.Meta.Should().BeNull();
    }
}
