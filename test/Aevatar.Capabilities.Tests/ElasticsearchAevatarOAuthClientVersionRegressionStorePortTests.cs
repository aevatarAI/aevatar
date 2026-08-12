using System.Reflection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.ProjectionRecovery;
using Aevatar.Mainnet.Host.Api.ProjectionRecovery;
using FluentAssertions;

namespace Aevatar.Capabilities.Tests;

public sealed class ElasticsearchAevatarOAuthClientVersionRegressionStorePortTests
{
    private const string ActorId = AevatarOAuthClientGAgent.WellKnownId;

    [Fact]
    public async Task InspectAsync_ShouldReadWellKnownSourceAndDocumentFingerprint()
    {
        var eventStore = new RecordingEventStore(2);
        var repairStore = new RecordingRepairStore { Lease = Lease(Document()) };
        var port = new ElasticsearchAevatarOAuthClientVersionRegressionStorePort(
            eventStore,
            repairStore);

        var result = await port.InspectAsync();

        result.ActorId.Should().Be(ActorId);
        result.SourceStateVersion.Should().Be(2);
        result.DocumentStateVersion.Should().Be(3);
        result.DocumentLastEventId.Should().Be("event-3");
        result.DocumentActorId.Should().Be(ActorId);
        eventStore.VersionRequests.Should().ContainSingle().Which.Should().Be(ActorId);
        repairStore.InspectKeys.Should().ContainSingle().Which.Should().Be(ActorId);
    }

    [Fact]
    public async Task DeleteIfMatchesAsync_WhenActorIsNotWellKnown_ShouldRejectBeforeStorageAccess()
    {
        var eventStore = new RecordingEventStore(2);
        var repairStore = new RecordingRepairStore { Lease = Lease(Document()) };
        var port = new ElasticsearchAevatarOAuthClientVersionRegressionStorePort(
            eventStore,
            repairStore);

        var result = await port.DeleteIfMatchesAsync(Request() with
        {
            ExpectedActorId = "oauth-client-other",
        });

        result.Should().Be(AevatarOAuthClientReplicaDeleteDisposition.SourceChanged);
        eventStore.VersionRequests.Should().BeEmpty();
        repairStore.InspectKeys.Should().BeEmpty();
        repairStore.DeleteLeases.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteIfMatchesAsync_WhenSourceChangesAfterFingerprintCheck_ShouldNotDelete()
    {
        var eventStore = new RecordingEventStore(2, 4);
        var repairStore = new RecordingRepairStore { Lease = Lease(Document()) };
        var port = new ElasticsearchAevatarOAuthClientVersionRegressionStorePort(
            eventStore,
            repairStore);

        var result = await port.DeleteIfMatchesAsync(Request());

        result.Should().Be(AevatarOAuthClientReplicaDeleteDisposition.SourceChanged);
        eventStore.VersionRequests.Should().Equal(ActorId, ActorId);
        repairStore.DeleteLeases.Should().BeEmpty();
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("event")]
    public async Task DeleteIfMatchesAsync_WhenFingerprintChanges_ShouldNotDelete(string mismatch)
    {
        var document = Document();
        switch (mismatch)
        {
            case "id":
                document.Id = "oauth-client-other";
                break;
            case "version":
                document.StateVersion = 4;
                break;
            case "event":
                document.LastEventId = "event-other";
                break;
        }

        var repairStore = new RecordingRepairStore { Lease = Lease(document) };
        var port = new ElasticsearchAevatarOAuthClientVersionRegressionStorePort(
            new RecordingEventStore(2),
            repairStore);

        var result = await port.DeleteIfMatchesAsync(Request());

        result.Should().Be(AevatarOAuthClientReplicaDeleteDisposition.DocumentChanged);
        repairStore.DeleteLeases.Should().BeEmpty();
    }

    [Theory]
    [InlineData(
        ElasticsearchProjectionDocumentRepairDeleteDisposition.Deleted,
        AevatarOAuthClientReplicaDeleteDisposition.Deleted)]
    [InlineData(
        ElasticsearchProjectionDocumentRepairDeleteDisposition.AlreadyAbsent,
        AevatarOAuthClientReplicaDeleteDisposition.AlreadyAbsent)]
    [InlineData(
        ElasticsearchProjectionDocumentRepairDeleteDisposition.RevisionConflict,
        AevatarOAuthClientReplicaDeleteDisposition.RevisionConflict)]
    public async Task DeleteIfMatchesAsync_ShouldMapConditionalDeleteDisposition(
        ElasticsearchProjectionDocumentRepairDeleteDisposition storeDisposition,
        AevatarOAuthClientReplicaDeleteDisposition expected)
    {
        var repairStore = new RecordingRepairStore
        {
            Lease = Lease(Document()),
            DeleteDisposition = storeDisposition,
        };
        var port = new ElasticsearchAevatarOAuthClientVersionRegressionStorePort(
            new RecordingEventStore(2, 2),
            repairStore);

        var result = await port.DeleteIfMatchesAsync(Request());

        result.Should().Be(expected);
        repairStore.DeleteLeases.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteIfMatchesAsync_WhenDocumentIsAbsentBeforeLease_ShouldReject()
    {
        var repairStore = new RecordingRepairStore { Lease = null };
        var port = new ElasticsearchAevatarOAuthClientVersionRegressionStorePort(
            new RecordingEventStore(2),
            repairStore);

        var result = await port.DeleteIfMatchesAsync(Request());

        result.Should().Be(AevatarOAuthClientReplicaDeleteDisposition.DocumentChanged);
        repairStore.DeleteLeases.Should().BeEmpty();
    }

    private static AevatarOAuthClientVersionRegressionRepairRequest Request() =>
        new(
            ActorId,
            ExpectedSourceStateVersion: 2,
            ExpectedDocumentStateVersion: 3,
            "event-3",
            "repair-1",
            "restore OAuth projection",
            "admin-1");

    private static AevatarOAuthClientDocument Document() => new()
    {
        Id = ActorId,
        StateVersion = 3,
        LastEventId = "event-3",
    };

    private static ElasticsearchProjectionDocumentRepairLease<
        AevatarOAuthClientDocument,
        string> Lease(AevatarOAuthClientDocument document)
    {
        var leaseType = typeof(ElasticsearchProjectionDocumentRepairLease<
            AevatarOAuthClientDocument,
            string>);
        var constructor = leaseType
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Should()
            .ContainSingle()
            .Subject;
        return (ElasticsearchProjectionDocumentRepairLease<
            AevatarOAuthClientDocument,
            string>)constructor.Invoke(
            [
                ActorId,
                document,
                "oauth-client-index-000001",
                7L,
                3L,
            ]);
    }

    private sealed class RecordingEventStore(params long[] versions) : IEventStore
    {
        private readonly Queue<long> _versions = new(versions);

        public List<string> VersionRequests { get; } = [];

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            VersionRequests.Add(agentId);
            return Task.FromResult(_versions.Dequeue());
        }

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRepairStore
        : IElasticsearchProjectionDocumentRepairStore<AevatarOAuthClientDocument, string>
    {
        public ElasticsearchProjectionDocumentRepairLease<
            AevatarOAuthClientDocument,
            string>? Lease { get; init; }

        public ElasticsearchProjectionDocumentRepairDeleteDisposition DeleteDisposition { get; init; } =
            ElasticsearchProjectionDocumentRepairDeleteDisposition.Deleted;

        public List<string> InspectKeys { get; } = [];

        public List<ElasticsearchProjectionDocumentRepairLease<
            AevatarOAuthClientDocument,
            string>> DeleteLeases { get; } = [];

        public Task<ElasticsearchProjectionDocumentRepairLease<
            AevatarOAuthClientDocument,
            string>?> InspectAsync(
            string key,
            CancellationToken ct = default)
        {
            InspectKeys.Add(key);
            return Task.FromResult(Lease);
        }

        public Task<ElasticsearchProjectionDocumentRepairDeleteDisposition> DeleteIfUnchangedAsync(
            ElasticsearchProjectionDocumentRepairLease<
                AevatarOAuthClientDocument,
                string> lease,
            CancellationToken ct = default)
        {
            DeleteLeases.Add(lease);
            return Task.FromResult(DeleteDisposition);
        }
    }
}
