using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.GAgents.Tests;

public sealed class SyncEntitySyncTests
{
    private static SyncEntity MakeEntity(
        string clientId,
        string entityType = "manifestation",
        int revision = 0,
        EntitySource source = EntitySource.Ai,
        string? bankHash = null,
        Timestamp? deletedAt = null)
    {
        var e = new SyncEntity
        {
            ClientId = clientId,
            EntityType = entityType,
            Revision = revision,
            Source = source,
            BankEligible = true,
            BankHash = bankHash ?? "",
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        if (deletedAt is not null) e.DeletedAt = deletedAt;
        return e;
    }

    private static SyncResult BuildSyncResult(SyncEntityGAgent agent, int clientRev)
    {
        var last = agent.State.LastSyncResult;
        var delta = agent.State.Entities
            .Where(kv => kv.Value.Revision > clientRev)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return new SyncResult(
            last?.SyncId ?? string.Empty,
            last?.ServerRevision ?? 0,
            delta,
            last?.Accepted.ToList() ?? [],
            last?.Rejected.ToList() ?? []);
    }

    private static async Task<SyncResult> SendSyncAsync(
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
        await GAgentTestHelper.SendCommandAsync(agent, cmd);
        return BuildSyncResult(agent, clientRev);
    }

    [Fact]
    public async Task SyncAsync_CreatesNewEntity()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("sync-entity:create");

        var result = await SendSyncAsync(agent, "s1", "user1", 0, [MakeEntity("c1")]);

        result.Accepted.Should().ContainSingle().Which.Should().Be("c1");
        result.Rejected.Should().BeEmpty();
        result.ServerRevision.Should().Be(1);
        agent.State.Entities.Should().ContainKey("c1");
        agent.State.Entities["c1"].UserId.Should().Be("user1");
    }

    [Fact]
    public async Task SyncAsync_UpdatesExistingEntity()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("sync-entity:update");
        await SendSyncAsync(agent, "s1", "user1", 0, [MakeEntity("c1")]);

        var updated = MakeEntity("c1", revision: 1);
        var result = await SendSyncAsync(agent, "s2", "user1", 1, [updated]);

        result.Accepted.Should().ContainSingle().Which.Should().Be("c1");
        result.ServerRevision.Should().Be(2);
    }

    [Fact]
    public async Task SyncAsync_RejectsStaleEntity()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("sync-entity:stale");
        await SendSyncAsync(agent, "s1", "user1", 0, [MakeEntity("c1")]);

        var stale = MakeEntity("c1", revision: 0);
        var result = await SendSyncAsync(agent, "s2", "user1", 0, [stale]);

        result.Rejected.Should().ContainSingle();
        result.Rejected[0].ClientId.Should().Be("c1");
    }

    [Fact]
    public async Task SyncAsync_TooManyEntities_Throws()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("sync-entity:limit");
        var entities = Enumerable.Range(0, 501).Select(i => MakeEntity($"c_{i}")).ToList();

        var cmd = new EntitiesSyncRequestedEvent { SyncId = "s1", UserId = "user1", ClientRevision = 0 };
        cmd.IncomingEntities.AddRange(entities);
        var act = () => GAgentTestHelper.SendCommandAsync(agent, cmd);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*max 500*");
    }

    [Fact]
    public async Task SyncAsync_EditDetection_MarksSourceAsEdited()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("sync-entity:edit");
        var original = MakeEntity("c1", source: EntitySource.Ai, bankHash: "hash_original");
        await SendSyncAsync(agent, "s1", "user1", 0, [original]);

        var edited = MakeEntity("c1", revision: 1, source: EntitySource.Ai, bankHash: "hash_changed");
        var result = await SendSyncAsync(agent, "s2", "user1", 1, [edited]);

        result.Accepted.Should().ContainSingle();
        agent.State.Entities["c1"].Source.Should().Be(EntitySource.Edited);
        agent.State.Entities["c1"].BankEligible.Should().BeFalse();
    }

    [Fact]
    public async Task SyncAsync_CascadeDelete_DeletesChildren()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("sync-entity:cascade");

        var parent = MakeEntity("parent", entityType: "manifestation");
        var child = MakeEntity("child", entityType: "affirmation");
        child.Refs["manifestation"] = "parent";
        await SendSyncAsync(agent, "s1", "user1", 0, [parent, child]);

        var serverRev = agent.State.Entities["parent"].Revision;
        var parentDel = MakeEntity("parent", revision: serverRev,
            deletedAt: Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow));
        await SendSyncAsync(agent, "s2", "user1", serverRev, [parentDel]);

        agent.State.Entities["child"].DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SyncAsync_CascadeDelete_MaxDepth5()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("sync-entity:cascade-depth");

        var entities = new List<SyncEntity>();
        for (var i = 0; i < 8; i++)
        {
            var e = MakeEntity($"level_{i}", entityType: "node");
            if (i > 0) e.Refs["parent"] = $"level_{i - 1}";
            entities.Add(e);
        }
        await SendSyncAsync(agent, "s1", "user1", 0, entities);

        var serverRev = agent.State.Entities["level_0"].Revision;
        var currentGlobalRev = agent.State.Meta.Revision;
        var del = MakeEntity("level_0", revision: serverRev,
            deletedAt: Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow));
        await SendSyncAsync(agent, "s2", "user1", currentGlobalRev, [del]);

        for (var i = 1; i <= 6; i++)
            agent.State.Entities[$"level_{i}"].DeletedAt.Should().NotBeNull($"level_{i} should be cascade-deleted");

        agent.State.Entities["level_7"].DeletedAt.Should().BeNull("depth > 5 guard should prevent this level");
    }

    [Fact]
    public async Task SyncAsync_ForceUserId_OverridesIncoming()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("sync-entity:userid");
        var entity = MakeEntity("c1");
        entity.UserId = "attacker";

        var result = await SendSyncAsync(agent, "s1", "real_user", 0, [entity]);

        result.Accepted.Should().ContainSingle();
        agent.State.Entities["c1"].UserId.Should().Be("real_user");
    }

    [Fact]
    public async Task SyncAsync_DeltaEntities_OnlyReturnsChangedSinceClientRevision()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("sync-entity:delta");
        await SendSyncAsync(agent, "s1", "user1", 0, [MakeEntity("c1"), MakeEntity("c2")]);

        var result = await SendSyncAsync(agent, "s2", "user1", 1, [MakeEntity("c3")]);

        result.DeltaEntities.Should().ContainKey("c2");
        result.DeltaEntities.Should().ContainKey("c3");
        result.DeltaEntities.Should().NotContainKey("c1");
    }

    [Fact]
    public async Task SyncAsync_DuplicateSyncId_IsIdempotent()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("sync-entity:idempotent");
        var entity = MakeEntity("c1");
        await SendSyncAsync(agent, "s1", "user1", 0, [entity]);

        var revAfterFirst = agent.State.Meta!.Revision;
        var entityCountAfterFirst = agent.State.Entities.Count;

        await SendSyncAsync(agent, "s1", "user1", 0, [entity]);

        agent.State.Meta.Revision.Should().Be(revAfterFirst);
        agent.State.Entities.Should().HaveCount(entityCountAfterFirst);
    }

    [Fact]
    public async Task SyncAsync_ProcessedSyncIds_RecordedInState()
    {
        var agent = await GAgentTestHelper.CreateAndActivate<SyncEntityGAgent, SyncEntityState>("sync-entity:syncid-track");
        await SendSyncAsync(agent, "s1", "user1", 0, [MakeEntity("c1")]);

        agent.State.ProcessedSyncIds.Should().Contain("s1");
    }
}
