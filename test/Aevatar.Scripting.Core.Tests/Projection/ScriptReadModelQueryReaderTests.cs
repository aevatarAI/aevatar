using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Projection.Queries;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptReadModelQueryReaderTests
{
    [Fact]
    public async Task GetSnapshotAsync_ShouldReturnMappedSnapshot()
    {
        var store = new InMemoryProjectionDocumentStore<ScriptReadModelDocument>();
        var reader = new ScriptReadModelQueryReader(store);
        var now = new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero);

        await store.UpsertAsync(new ScriptReadModelDocument
        {
            Id = "runtime-1",
            ScriptId = "script-1",
            DefinitionActorId = "definition-1",
            Revision = "rev-1",
            ReadModelTypeUrl = Any.Pack(new Struct()).TypeUrl,
            ReadModelPayload = Any.Pack(new Struct
            {
                Fields = { ["status"] = Google.Protobuf.WellKnownTypes.Value.ForString("ok") },
            }),
            StateVersion = 7,
            LastEventId = "evt-7",
            UpdatedAt = now,
        });

        var snapshot = await reader.GetSnapshotAsync("runtime-1", CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot!.ActorId.Should().Be("runtime-1");
        snapshot.ReadModelPayload.Should().NotBeNull();
        snapshot.ReadModelPayload!.Unpack<Struct>().Fields["status"].StringValue.Should().Be("ok");
        snapshot.StateVersion.Should().Be(7);
        snapshot.LastEventId.Should().Be("evt-7");
        snapshot.UpdatedAt.Should().Be(now);
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldReturnNull_WhenDocumentMissing()
    {
        var reader = new ScriptReadModelQueryReader(new InMemoryProjectionDocumentStore<ScriptReadModelDocument>());

        var snapshot = await reader.GetSnapshotAsync("missing", CancellationToken.None);

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task ListSnapshotsAsync_ShouldClampTake_AndReturnMappedDocuments()
    {
        var store = new InMemoryProjectionDocumentStore<ScriptReadModelDocument>();
        var reader = new ScriptReadModelQueryReader(store);

        await store.UpsertAsync(new ScriptReadModelDocument
        {
            Id = "runtime-1",
            ScriptId = "script-1",
            DefinitionActorId = "definition-1",
            Revision = "rev-1",
            ReadModelTypeUrl = string.Empty,
            ReadModelPayload = Any.Pack(new Empty()),
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await store.UpsertAsync(new ScriptReadModelDocument
        {
            Id = "runtime-2",
            ScriptId = "script-2",
            DefinitionActorId = "definition-2",
            Revision = "rev-2",
            ReadModelTypeUrl = string.Empty,
            ReadModelPayload = Any.Pack(new Empty()),
            StateVersion = 2,
            LastEventId = "evt-2",
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var snapshots = await reader.ListSnapshotsAsync(2, CancellationToken.None);

        snapshots.Should().HaveCount(2);
        snapshots.Select(static x => x.ActorId).Should().BeEquivalentTo(["runtime-1", "runtime-2"]);
    }
}
