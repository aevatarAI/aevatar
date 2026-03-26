using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Projection.Queries;
using Aevatar.GroupChat.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Projection;

public sealed class SourceCatalogQueryPortTests
{
    [Fact]
    public async Task GetSourceAsync_ShouldMapDocumentToSnapshot()
    {
        var store = new RecordingDocumentStore<SourceCatalogReadModel>(x => x.Id);
        await store.UpsertAsync(new SourceCatalogReadModel
        {
            Id = "group-chat:source:doc-1",
            ActorId = "group-chat:source:doc-1",
            SourceId = "doc-1",
            SourceKindValue = (int)GroupSourceKind.Document,
            CanonicalLocator = "doc://architecture/spec-1",
            AuthorityClassValue = (int)GroupSourceAuthorityClass.InternalAuthoritative,
            VerificationStatusValue = (int)GroupSourceVerificationStatus.Verified,
            StateVersion = 3,
            LastEventId = "source:doc-1:trust-updated",
            UpdatedAt = DateTimeOffset.Parse("2026-03-25T09:00:00+00:00"),
        });
        var queryPort = new SourceCatalogQueryPort(store);

        var snapshot = await queryPort.GetSourceAsync("doc-1");

        snapshot.Should().NotBeNull();
        snapshot!.ActorId.Should().Be("group-chat:source:doc-1");
        snapshot.SourceId.Should().Be("doc-1");
        snapshot.SourceKind.Should().Be(GroupSourceKind.Document);
        snapshot.CanonicalLocator.Should().Be("doc://architecture/spec-1");
        snapshot.AuthorityClass.Should().Be(GroupSourceAuthorityClass.InternalAuthoritative);
        snapshot.VerificationStatus.Should().Be(GroupSourceVerificationStatus.Verified);
        snapshot.StateVersion.Should().Be(3);
    }
}
