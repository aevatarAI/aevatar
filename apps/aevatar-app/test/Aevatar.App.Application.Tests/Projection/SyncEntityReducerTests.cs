using FluentAssertions;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Reducers;
using Aevatar.App.GAgents;
using Google.Protobuf.WellKnownTypes;
using static Aevatar.App.Application.Tests.Projection.ProjectionTestHelpers;

namespace Aevatar.App.Application.Tests.Projection;

public sealed class SyncEntityReducerTests
{
    private readonly DateTimeOffset _now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EntityCreated_Adds_Entry()
    {
        var reducer = new EntityCreatedEventReducer();
        var model = new AppSyncEntityReadModel { Id = "syncentity:u1" };
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var evt = new EntityCreatedEvent
        {
            UserId = "u1",
            ClientId = "c1",
            EntityType = "manifestation",
            Revision = 1,
            Source = EntitySource.User,
            Position = 0,
            BankEligible = true,
            BankHash = "hash1",
            CreatedAt = Timestamp.FromDateTime(createdAt),
        };

        reducer.Reduce(model, CreateContext("syncentity:u1"), PackEnvelope(evt), _now).Should().BeTrue();

        model.Entities.Should().ContainKey("c1");
        var entry = model.Entities["c1"];
        entry.UserId.Should().Be("u1");
        entry.ClientId.Should().Be("c1");
        entry.EntityType.Should().Be("manifestation");
        entry.Revision.Should().Be(1);
        entry.Source.Should().Be("user");
        entry.BankEligible.Should().BeTrue();
        entry.BankHash.Should().Be("hash1");
        entry.CreatedAt.Should().Be(new DateTimeOffset(createdAt));
        model.UserId.Should().Be("u1");
        model.ServerRevision.Should().Be(1);
    }

    [Fact]
    public void EntityUpdated_Preserves_CreatedAt()
    {
        var reducer = new EntityUpdatedEventReducer();
        var existingCreated = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var model = new AppSyncEntityReadModel
        {
            Id = "syncentity:u1",
            Entities = { ["c1"] = new SyncEntityEntry { ClientId = "c1", CreatedAt = existingCreated } }
        };
        var updatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var evt = new EntityUpdatedEvent
        {
            UserId = "u1",
            ClientId = "c1",
            EntityType = "manifestation",
            Revision = 2,
            Source = EntitySource.Edited,
            UpdatedAt = Timestamp.FromDateTime(updatedAt),
        };

        reducer.Reduce(model, CreateContext("syncentity:u1"), PackEnvelope(evt), _now).Should().BeTrue();

        model.Entities["c1"].CreatedAt.Should().Be(existingCreated);
        model.Entities["c1"].Revision.Should().Be(2);
        model.Entities["c1"].Source.Should().Be("edited");
        model.Entities["c1"].UpdatedAt.Should().Be(new DateTimeOffset(updatedAt));
        model.ServerRevision.Should().Be(2);
    }

    [Fact]
    public void EntityDeleted_Sets_DeletedAt_And_Clears_BankEligible()
    {
        var reducer = new EntityDeletedEventReducer();
        var model = new AppSyncEntityReadModel
        {
            Id = "syncentity:u1",
            Entities = { ["c1"] = new SyncEntityEntry { ClientId = "c1", BankEligible = true, Revision = 1 } }
        };
        var deletedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var evt = new EntityDeletedEvent
        {
            UserId = "u1",
            ClientId = "c1",
            Revision = 3,
            DeletedAt = Timestamp.FromDateTime(deletedAt),
        };

        reducer.Reduce(model, CreateContext("syncentity:u1"), PackEnvelope(evt), _now).Should().BeTrue();

        var entry = model.Entities["c1"];
        entry.DeletedAt.Should().Be(new DateTimeOffset(deletedAt));
        entry.Revision.Should().Be(3);
        entry.BankEligible.Should().BeFalse();
        model.ServerRevision.Should().Be(3);
    }

    [Fact]
    public void EntityDeleted_Ignores_Unknown_ClientId()
    {
        var reducer = new EntityDeletedEventReducer();
        var model = new AppSyncEntityReadModel { Id = "syncentity:u1" };
        var evt = new EntityDeletedEvent { UserId = "u1", ClientId = "unknown", Revision = 1 };

        reducer.Reduce(model, CreateContext("syncentity:u1"), PackEnvelope(evt), _now).Should().BeTrue();

        model.Entities.Should().BeEmpty();
        model.ServerRevision.Should().Be(1);
    }

    [Fact]
    public void AccountDeleted_Hard_Clears_Everything()
    {
        var reducer = new AccountDeletedEventSyncEntityReducer();
        var model = new AppSyncEntityReadModel
        {
            Id = "syncentity:u1",
            UserId = "u1",
            ServerRevision = 5,
            Entities = { ["c1"] = new SyncEntityEntry { ClientId = "c1" } }
        };
        var evt = new AccountDeletedEvent { UserId = "u1", Mode = "hard" };

        reducer.Reduce(model, CreateContext("syncentity:u1"), PackEnvelope(evt), _now).Should().BeTrue();

        model.Entities.Should().BeEmpty();
        model.UserId.Should().BeEmpty();
        model.ServerRevision.Should().Be(0);
    }

    [Fact]
    public void AccountDeleted_Soft_Anonymizes_Entries()
    {
        var reducer = new AccountDeletedEventSyncEntityReducer();
        var deletedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var model = new AppSyncEntityReadModel
        {
            Id = "syncentity:u1",
            UserId = "u1",
            ServerRevision = 5,
            Entities =
            {
                ["c1"] = new SyncEntityEntry
                {
                    ClientId = "c1", UserId = "u1", BankEligible = true, BankHash = "h1"
                }
            }
        };
        var evt = new AccountDeletedEvent
        {
            UserId = "u1",
            Mode = "soft",
            DeletedAt = Timestamp.FromDateTime(deletedAt),
        };

        reducer.Reduce(model, CreateContext("syncentity:u1"), PackEnvelope(evt), _now).Should().BeTrue();

        model.UserId.Should().Be("deleted_u1");
        var entry = model.Entities["c1"];
        entry.UserId.Should().Be("deleted_u1");
        entry.BankEligible.Should().BeFalse();
        entry.BankHash.Should().BeEmpty();
        entry.DeletedAt.Should().Be(new DateTimeOffset(deletedAt));
    }

    [Theory]
    [InlineData(EntitySource.Ai, "ai")]
    [InlineData(EntitySource.Bank, "bank")]
    [InlineData(EntitySource.User, "user")]
    [InlineData(EntitySource.Edited, "edited")]
    public void EntityCreated_Maps_SourceEnum_Correctly(EntitySource source, string expected)
    {
        var reducer = new EntityCreatedEventReducer();
        var model = new AppSyncEntityReadModel { Id = "syncentity:u1" };
        var evt = new EntityCreatedEvent
        {
            UserId = "u1", ClientId = "c1", EntityType = "t", Revision = 1, Source = source,
        };

        reducer.Reduce(model, CreateContext("syncentity:u1"), PackEnvelope(evt), _now);

        model.Entities["c1"].Source.Should().Be(expected);
    }
}
