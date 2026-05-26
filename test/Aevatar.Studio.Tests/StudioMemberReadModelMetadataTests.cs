using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Projection.Metadata;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Surface tests for the StudioMember projection metadata + read-model
/// adapter. These are intentionally narrow — they only ensure the
/// configured index name stays under <c>studio-members</c> and that the
/// read-model's IProjectionReadModel surface returns the values the
/// projector wrote.
/// </summary>
public sealed class StudioMemberReadModelMetadataTests
{
    [Fact]
    public void MetadataProvider_ShouldExposeStudioMembersIndex()
    {
        var provider = new StudioMemberCurrentStateDocumentMetadataProvider();
        provider.Metadata.IndexName.Should().Be("studio-members");
        provider.Metadata.Mappings.Should().ContainKey("dynamic");
    }

    [Fact]
    public void BindingRunMetadataProvider_ShouldExposeStudioMemberBindingRunsIndex()
    {
        var provider = new StudioMemberBindingRunCurrentStateDocumentMetadataProvider();
        provider.Metadata.IndexName.Should().Be("studio-member-binding-runs");
        provider.Metadata.Mappings.Should().ContainKey("dynamic");
        provider.Metadata.Settings.Should().BeEmpty();
        provider.Metadata.Aliases.Should().BeEmpty();
    }

    [Fact]
    public void ReadModel_ShouldSurfaceProjectionContractFields()
    {
        var updatedAt = DateTimeOffset.Parse("2026-04-27T01:02:03Z");
        var doc = new StudioMemberCurrentStateDocument
        {
            Id = "studio-member:scope-1:m-1",
            ActorId = "studio-member:scope-1:m-1",
            StateVersion = 7,
            LastEventId = "evt-7",
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
        };

        IProjectionReadModel readModel = doc;
        readModel.ActorId.Should().Be(doc.ActorId);
        readModel.StateVersion.Should().Be(doc.StateVersion);
        readModel.LastEventId.Should().Be(doc.LastEventId);
        readModel.UpdatedAt.Should().Be(updatedAt);
    }

    [Fact]
    public void ReadModel_ShouldReturnMinValueWhenUpdatedAtIsNull()
    {
        IProjectionReadModel readModel = new StudioMemberCurrentStateDocument
        {
            Id = "studio-member:scope-1:m-1",
        };
        readModel.UpdatedAt.Should().Be(DateTimeOffset.MinValue);
    }

    [Fact]
    public void BindingRunReadModel_ShouldSurfaceProjectionContractFields()
    {
        var updatedAt = DateTimeOffset.Parse("2026-04-30T01:02:03Z");
        var doc = new StudioMemberBindingRunCurrentStateDocument
        {
            Id = "studio-member-binding-run:bind-1",
            ActorId = "studio-member-binding-run:bind-1",
            StateVersion = 3,
            LastEventId = "evt-3",
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
        };

        IProjectionReadModel readModel = doc;
        readModel.ActorId.Should().Be(doc.ActorId);
        readModel.StateVersion.Should().Be(doc.StateVersion);
        readModel.LastEventId.Should().Be(doc.LastEventId);
        readModel.UpdatedAt.Should().Be(updatedAt);
    }

    [Fact]
    public void BindingRunReadModel_ShouldReturnMinValueWhenUpdatedAtIsNull()
    {
        IProjectionReadModel readModel = new StudioMemberBindingRunCurrentStateDocument
        {
            Id = "studio-member-binding-run:bind-1",
        };
        readModel.UpdatedAt.Should().Be(DateTimeOffset.MinValue);
    }
}
