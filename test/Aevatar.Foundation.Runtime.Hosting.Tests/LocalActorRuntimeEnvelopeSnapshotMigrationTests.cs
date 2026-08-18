using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Local.ActivationIndex;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class LocalActorRuntimeEnvelopeSnapshotMigrationTests
{
    [Fact]
    public async Task CompareExchange_ConcurrentMigrationCandidates_ShouldCommitOneWholeEnvelope()
    {
        const string actorId = "concurrent-migration-actor";
        var store = new InMemoryLocalActorRuntimeEnvelopeStore();
        var baseline = new RuntimeActorStateEnvelope
        {
            Identity = new RuntimeActorIdentity
            {
                Kind = "tests.concurrent-migration",
                StateSchemaVersion = 0,
            },
            StateContractTypeName = typeof(Int32Value).FullName,
            StateSnapshot = ByteString.CopyFrom(new Int32Value { Value = 0 }.ToByteArray()),
        };
        (await store.CompareExchangeAsync(actorId, null, baseline)).Should().BeTrue();
        var expected = await store.GetAsync(actorId);
        var first = MigrationCandidate(expected!, value: 1, digest: "digest-first");
        var second = MigrationCandidate(expected!, value: 2, digest: "digest-second");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = new[] { first, second }
            .Select(candidate => Task.Run(async () =>
            {
                await start.Task;
                return await store.CompareExchangeAsync(actorId, expected, candidate);
            }))
            .ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);

        results.Count(static result => result).Should().Be(1);
        var winner = results[0] ? first : second;
        var committed = await store.GetAsync(actorId);
        committed.Should().NotBeNull();
        committed!.Equals(winner).Should().BeTrue();
        committed.Identity!.StateSchemaVersion.Should().Be(1);
        committed.Identity.StateSchemaAdoptions.Should().ContainSingle();
        Int32Value.Parser.ParseFrom(committed.StateSnapshot).Value.Should()
            .Be(results[0] ? 1 : 2);
    }

    [Fact]
    public async Task ActivationImport_ShouldRecoverLegacySnapshotAfterEventCompaction()
    {
        const string actorId = "legacy-compacted-actor";
        var root = Path.Combine(
            Path.GetTempPath(),
            "aevatar-runtime-envelope-migration-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileEventStoreOptions { RootDirectory = root };
            var legacySnapshots = new FileEventSourcingSnapshotStore<Int32Value>(options);
            await legacySnapshots.SaveAsync(
                actorId,
                new EventSourcingSnapshot<Int32Value>(new Int32Value { Value = 42 }, 2));

            var events = new FileEventStore(options);
            await events.AppendAsync(
                actorId,
                [
                    new StateEvent { AgentId = actorId, EventId = "event-1", Version = 1 },
                    new StateEvent { AgentId = actorId, EventId = "event-2", Version = 2 },
                ],
                expectedVersion: 0);
            (await events.DeleteEventsUpToAsync(actorId, 2)).Should().Be(2);
            (await events.GetEventsAsync(actorId)).Should().BeEmpty();
            (await events.GetVersionAsync(actorId)).Should().Be(2);

            var envelopes = new FileLocalActorRuntimeEnvelopeStore(options);
            var imported = await envelopes.GetForActivationAsync(
                actorId,
                typeof(Int32Value).FullName!);
            imported.Should().NotBeNull();
            imported!.StateContractTypeName.Should().Be(typeof(Int32Value).FullName);
            imported.StateSnapshotVersion.Should().Be(2);
            Int32Value.Parser.ParseFrom(imported.StateSnapshot).Value.Should().Be(42);

            var adopted = imported.Clone();
            adopted.Identity = new RuntimeActorIdentity
            {
                Kind = "tests.legacy-compacted",
                StateSchemaVersion = 1,
            };
            (await envelopes.CompareExchangeAsync(actorId, imported, adopted)).Should().BeTrue();

            var snapshotStore = new LocalActorRuntimeEnvelopeSnapshotStore<Int32Value>(envelopes);
            var behavior = new EventSourcingBehavior<Int32Value>(
                events,
                actorId,
                snapshotStore);
            var reactivated = await behavior.ReplayAsync(actorId);

            reactivated.Should().NotBeNull();
            reactivated!.Value.Should().Be(42);
            behavior.CurrentVersion.Should().Be(2);

            await legacySnapshots.SaveAsync(
                actorId,
                new EventSourcingSnapshot<Int32Value>(new Int32Value { Value = 99 }, 2));
            var reloaded = await envelopes.GetForActivationAsync(
                actorId,
                typeof(Int32Value).FullName!);
            Int32Value.Parser.ParseFrom(reloaded!.StateSnapshot).Value.Should().Be(42);
            reloaded.Identity!.Kind.Should().Be("tests.legacy-compacted");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static RuntimeActorStateEnvelope MigrationCandidate(
        RuntimeActorStateEnvelope baseline,
        int value,
        string digest)
    {
        var candidate = baseline.Clone();
        candidate.Identity.StateSchemaVersion = 1;
        candidate.Identity.StateSchemaAdoptions.Add(new RuntimeActorStateSchemaAdoptionReceipt
        {
            StateSchemaVersion = 1,
            RequiredCapability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            RequiredContractId = "tests.concurrent-migration.v1",
            RequiredContractVersion = 1,
            CapabilityEpoch = 1,
            AuthorityStateVersion = 1,
            MembershipEpoch = 1,
            DeploymentRevision = "revision-a",
            AdoptedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            MembershipDigest = digest,
        });
        candidate.StateSnapshot = ByteString.CopyFrom(new Int32Value { Value = value }.ToByteArray());
        return candidate;
    }
}
