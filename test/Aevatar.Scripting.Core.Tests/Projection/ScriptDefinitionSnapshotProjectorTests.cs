using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptDefinitionSnapshotProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldMaterializeDefinitionDocument_FromCommittedState()
    {
        var dispatcher = new RecordingDispatcher();
        var projector = new ScriptDefinitionSnapshotProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptAuthorityProjectionContext
        {
            RootActorId = "definition-1",
            ProjectionKind = "script-authority-read-model",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                eventId: "evt-definition-upserted",
                version: 4,
                payload: Any.Pack(new ScriptReadModelSchemaValidatedEvent
                {
                    ScriptId = "script-1",
                    ScriptRevision = "rev-3",
                }),
                state: new ScriptDefinitionState
                {
                    ScriptId = "script-1",
                    Revision = "rev-3",
                    SourceText = "source",
                    SourceHash = "hash-3",
                    StateTypeUrl = Any.Pack(new Empty()).TypeUrl,
                    ReadModelTypeUrl = Any.Pack(new Empty()).TypeUrl,
                    ReadModelSchemaVersion = "3",
                    ReadModelSchemaHash = "schema-hash-3",
                    ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource("source"),
                    ProtocolDescriptorSet = ByteString.CopyFromUtf8("descriptor-set"),
                    StateDescriptorFullName = "example.State",
                    ReadModelDescriptorFullName = "example.ReadModel",
                    RuntimeSemantics = new ScriptRuntimeSemanticsSpec(),
                    LastAppliedEventVersion = 4,
                    LastEventId = "rev-3:schema-validated",
                }),
            CancellationToken.None);

        dispatcher.LastUpsert.Should().NotBeNull();
        var document = dispatcher.LastUpsert!;
        document.Id.Should().Be("definition-1");
        document.DefinitionActorId.Should().Be("definition-1");
        document.ScriptId.Should().Be("script-1");
        document.Revision.Should().Be("rev-3");
        document.SourceText.Should().Be("source");
        document.SourceHash.Should().Be("hash-3");
        document.ReadModelSchemaVersion.Should().Be("3");
        document.ReadModelSchemaHash.Should().Be("schema-hash-3");
        document.ProtocolDescriptorSetBase64.Should().Be(ByteString.CopyFromUtf8("descriptor-set").ToBase64());
        document.StateVersion.Should().Be(4);
        document.LastEventId.Should().Be("evt-definition-upserted");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnore_EnvelopesWithoutCommittedDefinitionState()
    {
        var dispatcher = new RecordingDispatcher();
        var projector = new ScriptDefinitionSnapshotProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptAuthorityProjectionContext
        {
            RootActorId = "definition-1",
            ProjectionKind = "script-authority-read-model",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "evt-raw",
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication("projection-test", TopologyAudience.Self),
                Payload = Any.Pack(new ScriptDefinitionUpsertedEvent
                {
                    ScriptId = "script-1",
                    ScriptRevision = "rev-1",
                }),
            },
            CancellationToken.None);

        dispatcher.LastUpsert.Should().BeNull();
    }

    private static EventEnvelope BuildCommittedEnvelope(
        string eventId,
        long version,
        Any payload,
        ScriptDefinitionState state)
    {
        var occurredAt = Timestamp.FromDateTime(DateTime.UtcNow);
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = occurredAt.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("projection-test"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = occurredAt.Clone(),
                    EventData = payload.Clone(),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class RecordingDispatcher : IProjectionWriteDispatcher<ScriptDefinitionSnapshotDocument>
    {
        public ScriptDefinitionSnapshotDocument? LastUpsert { get; private set; }

        public string? LastDeletedId { get; private set; }

        public Task<ProjectionWriteResult> UpsertAsync(
            ScriptDefinitionSnapshotDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastUpsert = readModel.Clone();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastDeletedId = id;
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
