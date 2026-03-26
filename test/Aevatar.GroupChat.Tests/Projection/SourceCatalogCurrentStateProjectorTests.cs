using Aevatar.GroupChat.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Projection.Contexts;
using Aevatar.GroupChat.Projection.Projectors;
using Aevatar.GroupChat.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GroupChat.Tests.Projection;

public sealed class SourceCatalogCurrentStateProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldMaterializeCurrentStateReplica()
    {
        var store = new RecordingDocumentStore<SourceCatalogReadModel>(x => x.Id);
        var projector = new SourceCatalogCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-03-25T00:00:00+00:00")));

        await projector.ProjectAsync(
            new SourceCatalogProjectionContext
            {
                RootActorId = "group-chat:source:doc-1",
                ProjectionKind = "group-chat-source-catalog",
            },
            new EventEnvelope
            {
                Id = "outer-1",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-25T09:00:00+00:00")),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "source:doc-1:trust-updated",
                        Version = 2,
                        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-25T09:00:00+00:00")),
                    },
                    StateRoot = Any.Pack(new GroupSourceRegistryState
                    {
                        SourceId = "doc-1",
                        SourceKind = GroupSourceKind.Document,
                        CanonicalLocator = "doc://architecture/spec-1",
                        AuthorityClass = GroupSourceAuthorityClass.InternalAuthoritative,
                        VerificationStatus = GroupSourceVerificationStatus.Verified,
                    }),
                }),
            });

        var readModel = await store.GetAsync("group-chat:source:doc-1");
        readModel.Should().NotBeNull();
        readModel!.SourceId.Should().Be("doc-1");
        readModel.SourceKindValue.Should().Be((int)GroupSourceKind.Document);
        readModel.CanonicalLocator.Should().Be("doc://architecture/spec-1");
        readModel.AuthorityClassValue.Should().Be((int)GroupSourceAuthorityClass.InternalAuthoritative);
        readModel.VerificationStatusValue.Should().Be((int)GroupSourceVerificationStatus.Verified);
        readModel.StateVersion.Should().Be(2);
        readModel.LastEventId.Should().Be("source:doc-1:trust-updated");
    }
}
