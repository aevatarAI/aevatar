using System.Text.Json;
using Aevatar.App.Application.Contracts;
using Aevatar.App.GAgents;
using Aevatar.App.Host.Api.Endpoints.Mappers;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.Host.Api.Tests;

public sealed class EntityMapMapperTests
{
    private static SyncEntity CreateSyncEntity(
        string clientId = "c1",
        string entityType = "manifestation",
        EntitySource source = EntitySource.Ai)
    {
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var entity = new SyncEntity
        {
            ClientId = clientId,
            EntityType = entityType,
            UserId = "user-1",
            Revision = 3,
            Position = 1,
            Source = source,
            BankEligible = true,
            BankHash = "abc123",
            Inputs = new Struct { Fields = { ["prompt"] = Google.Protobuf.WellKnownTypes.Value.ForString("hello") } },
            Output = new Struct { Fields = { ["url"] = Google.Protobuf.WellKnownTypes.Value.ForString("https://example.com/img.png") } },
            CreatedAt = now,
            UpdatedAt = now,
        };
        entity.Refs["parent"] = "p1";
        return entity;
    }

    [Fact]
    public void ToDto_MapsAllFields()
    {
        var entity = CreateSyncEntity();
        var dto = EntityMapMapper.ToDto(entity);

        dto.ClientId.Should().Be("c1");
        dto.EntityType.Should().Be("manifestation");
        dto.UserId.Should().Be("user-1");
        dto.Revision.Should().Be(3);
        dto.Position.Should().Be(1);
        dto.Source.Should().Be("ai");
        dto.BankEligible.Should().BeTrue();
        dto.BankHash.Should().Be("abc123");
        dto.Refs.Should().ContainKey("parent").WhoseValue.Should().Be("p1");
        dto.Inputs.Should().NotBeNull();
        dto.Output.Should().NotBeNull();
        dto.CreatedAt.Should().NotBeNullOrEmpty();
        dto.UpdatedAt.Should().NotBeNullOrEmpty();
        dto.DeletedAt.Should().BeNull();
    }

    [Fact]
    public void ToDto_WithDeletedAt_MapsIso8601()
    {
        var entity = CreateSyncEntity();
        entity.DeletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var dto = EntityMapMapper.ToDto(entity);

        dto.DeletedAt.Should().NotBeNull();
        DateTimeOffset.TryParse(dto.DeletedAt, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(EntitySource.Ai, "ai")]
    [InlineData(EntitySource.Bank, "bank")]
    [InlineData(EntitySource.User, "user")]
    [InlineData(EntitySource.Edited, "edited")]
    public void ToDto_MapsSourceCorrectly(EntitySource source, string expected)
    {
        var entity = CreateSyncEntity(source: source);
        var dto = EntityMapMapper.ToDto(entity);
        dto.Source.Should().Be(expected);
    }

    [Fact]
    public void ToDto_Grouped_PreservesStructure()
    {
        var delta = new Dictionary<string, SyncEntity>
        {
            ["c1"] = CreateSyncEntity("c1"),
            ["c2"] = CreateSyncEntity("c2"),
            ["c3"] = CreateSyncEntity("c3", "affirmation"),
        };

        var result = EntityMapMapper.DeltaToDto(delta);

        result.Should().ContainKey("manifestation");
        result["manifestation"].Should().HaveCount(2);
        result.Should().ContainKey("affirmation");
        result["affirmation"].Should().HaveCount(1);
    }

    [Fact]
    public void DeltaToDto_GroupsByEntityType()
    {
        var delta = new Dictionary<string, SyncEntity>
        {
            ["c1"] = CreateSyncEntity("c1", "manifestation"),
            ["c2"] = CreateSyncEntity("c2", "affirmation"),
            ["c3"] = CreateSyncEntity("c3", "manifestation"),
        };

        var result = EntityMapMapper.DeltaToDto(delta);

        result.Should().ContainKey("manifestation");
        result["manifestation"].Should().HaveCount(2);
        result.Should().ContainKey("affirmation");
        result["affirmation"].Should().HaveCount(1);
    }

    [Fact]
    public void FromDto_RoundTrip_PreservesValues()
    {
        var original = CreateSyncEntity();
        var dto = EntityMapMapper.ToDto(original);
        var roundTripped = EntityMapMapper.FromDto(dto);

        roundTripped.ClientId.Should().Be(original.ClientId);
        roundTripped.EntityType.Should().Be(original.EntityType);
        roundTripped.UserId.Should().Be(original.UserId);
        roundTripped.Revision.Should().Be(original.Revision);
        roundTripped.Position.Should().Be(original.Position);
        roundTripped.Source.Should().Be(original.Source);
        roundTripped.BankEligible.Should().Be(original.BankEligible);
        roundTripped.BankHash.Should().Be(original.BankHash);
        roundTripped.Refs.Should().BeEquivalentTo(original.Refs);
    }

    [Fact]
    public void FromDto_NullStructFields_HandledSafely()
    {
        var dto = new EntityDto
        {
            ClientId = "c1",
            EntityType = "test",
            Revision = 1,
            Inputs = null,
            Output = null,
            State = null,
        };

        var entity = EntityMapMapper.FromDto(dto);

        entity.Inputs.Should().BeNull();
        entity.Output.Should().BeNull();
        entity.State.Should().BeNull();
    }

    [Fact]
    public void ToDto_NullStructFields_ReturnsNullJsonElements()
    {
        var entity = new SyncEntity
        {
            ClientId = "c1",
            EntityType = "test",
            UserId = "u1",
        };

        var dto = EntityMapMapper.ToDto(entity);

        dto.Inputs.Should().BeNull();
        dto.Output.Should().BeNull();
        dto.State.Should().BeNull();
    }

    [Theory]
    [InlineData("ai", EntitySource.Ai)]
    [InlineData("bank", EntitySource.Bank)]
    [InlineData("user", EntitySource.User)]
    [InlineData("edited", EntitySource.Edited)]
    [InlineData(null, EntitySource.Ai)]
    [InlineData("unknown", EntitySource.Ai)]
    public void FromDto_MapsSourceCorrectly(string? source, EntitySource expected)
    {
        var dto = new EntityDto
        {
            ClientId = "c1",
            EntityType = "test",
            Source = source,
        };

        var entity = EntityMapMapper.FromDto(dto);
        entity.Source.Should().Be(expected);
    }
}
