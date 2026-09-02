using System.Reflection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Authorization;

public sealed class ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePortTests
{
    private const string VerifiedOwnerSubject = "owner-alpha";
    private static readonly string ActorId = NyxIdAuthorizationCatalogActorIds.Build(Owner());

    [Fact]
    public async Task InspectPersonalAsync_ShouldDeriveCanonicalActorFromNormalizedOwner()
    {
        var eventStore = new RecordingEventStore { Version = 3 };
        var repairStore = new RecordingRepairStore
        {
            Lease = Lease(Document()),
        };
        var port = new ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePort(
            eventStore,
            repairStore);

        var result = await port.InspectPersonalAsync($" {VerifiedOwnerSubject} ");

        result.VerifiedOwnerSubject.Should().Be(VerifiedOwnerSubject);
        result.ActorId.Should().Be(ActorId);
        result.SourceStateVersion.Should().Be(3);
        result.DocumentStateVersion.Should().Be(4);
        result.DocumentLastEventId.Should().Be("event-4");
        result.DocumentActorId.Should().Be(ActorId);
        eventStore.VersionRequests.Should().ContainSingle().Which.Should().Be(ActorId);
        repairStore.InspectKeys.Should().ContainSingle().Which.Should().Be(ActorId);
    }

    [Fact]
    public async Task DeleteIfMatchesAsync_WhenExpectedActorIsWrong_ShouldNotReadEventStoreOrElasticsearch()
    {
        var eventStore = new RecordingEventStore { Version = 1 };
        var repairStore = new RecordingRepairStore
        {
            Lease = Lease(Document()),
        };
        var port = new ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePort(
            eventStore,
            repairStore);

        var result = await port.DeleteIfMatchesAsync(Request() with
        {
            ExpectedActorId = "catalog-actor-other",
        });

        result.Should().Be(NyxIdAuthorizationCatalogReplicaDeleteDisposition.SourceChanged);
        eventStore.VersionRequests.Should().BeEmpty();
        repairStore.InspectKeys.Should().BeEmpty();
        repairStore.DeleteLeases.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 4)]
    [InlineData(4, 3)]
    public async Task DeleteIfMatchesAsync_WhenManifestIsNotARegression_ShouldRejectBeforeStorageAccess(
        long sourceVersion,
        long documentVersion)
    {
        var eventStore = new RecordingEventStore { Version = 1 };
        var repairStore = new RecordingRepairStore
        {
            Lease = Lease(Document()),
        };
        var port = new ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePort(
            eventStore,
            repairStore);
        var request = Request() with
        {
            ExpectedSourceStateVersion = sourceVersion,
            ExpectedDocumentStateVersion = documentVersion,
        };

        var act = () => port.DeleteIfMatchesAsync(request);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        eventStore.VersionRequests.Should().BeEmpty();
        repairStore.InspectKeys.Should().BeEmpty();
        repairStore.DeleteLeases.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteIfMatchesAsync_WhenSourceVersionChanged_ShouldNotReadElasticsearch()
    {
        var eventStore = new RecordingEventStore { Version = 2 };
        var repairStore = new RecordingRepairStore
        {
            Lease = Lease(Document()),
        };
        var port = new ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePort(
            eventStore,
            repairStore);

        var result = await port.DeleteIfMatchesAsync(Request());

        result.Should().Be(NyxIdAuthorizationCatalogReplicaDeleteDisposition.SourceChanged);
        eventStore.VersionRequests.Should().ContainSingle().Which.Should().Be(ActorId);
        repairStore.InspectKeys.Should().BeEmpty();
        repairStore.DeleteLeases.Should().BeEmpty();
    }

    [Theory]
    [InlineData("id")]
    [InlineData("actor")]
    [InlineData("version")]
    [InlineData("event")]
    public async Task DeleteIfMatchesAsync_WhenDocumentFingerprintChanged_ShouldNotDelete(
        string mismatch)
    {
        var document = Document();
        switch (mismatch)
        {
            case "id":
                document.Id = "catalog-document-other";
                break;
            case "actor":
                document.ActorId = "catalog-actor-other";
                break;
            case "version":
                document.StateVersion = 5;
                break;
            case "event":
                document.LastEventId = "event-other";
                break;
        }

        var eventStore = new RecordingEventStore { Version = 1 };
        var repairStore = new RecordingRepairStore
        {
            Lease = Lease(document),
        };
        var port = new ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePort(
            eventStore,
            repairStore);

        var result = await port.DeleteIfMatchesAsync(Request());

        result.Should().Be(NyxIdAuthorizationCatalogReplicaDeleteDisposition.DocumentChanged);
        repairStore.InspectKeys.Should().ContainSingle().Which.Should().Be(ActorId);
        repairStore.DeleteLeases.Should().BeEmpty();
    }

    [Theory]
    [InlineData(
        ElasticsearchProjectionDocumentRepairDeleteDisposition.Deleted,
        NyxIdAuthorizationCatalogReplicaDeleteDisposition.Deleted)]
    [InlineData(
        ElasticsearchProjectionDocumentRepairDeleteDisposition.AlreadyAbsent,
        NyxIdAuthorizationCatalogReplicaDeleteDisposition.AlreadyAbsent)]
    [InlineData(
        ElasticsearchProjectionDocumentRepairDeleteDisposition.RevisionConflict,
        NyxIdAuthorizationCatalogReplicaDeleteDisposition.RevisionConflict)]
    public async Task DeleteIfMatchesAsync_ShouldMapElasticsearchDeleteDisposition(
        ElasticsearchProjectionDocumentRepairDeleteDisposition storeDisposition,
        NyxIdAuthorizationCatalogReplicaDeleteDisposition expected)
    {
        var eventStore = new RecordingEventStore { Version = 1 };
        var repairStore = new RecordingRepairStore
        {
            Lease = Lease(Document()),
            DeleteDisposition = storeDisposition,
        };
        var port = new ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePort(
            eventStore,
            repairStore);

        var result = await port.DeleteIfMatchesAsync(Request());

        result.Should().Be(expected);
        repairStore.DeleteLeases.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteIfMatchesAsync_WhenDocumentIsMissing_ShouldReturnAlreadyAbsentWithoutDelete()
    {
        var eventStore = new RecordingEventStore { Version = 1 };
        var repairStore = new RecordingRepairStore();
        var port = new ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePort(
            eventStore,
            repairStore);

        var result = await port.DeleteIfMatchesAsync(Request());

        result.Should().Be(NyxIdAuthorizationCatalogReplicaDeleteDisposition.AlreadyAbsent);
        repairStore.InspectKeys.Should().ContainSingle().Which.Should().Be(ActorId);
        repairStore.DeleteLeases.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteIfMatchesAsync_WhenAuthorityChangesAfterLeaseValidation_ShouldNotDelete()
    {
        var eventStore = new RecordingEventStore
        {
            Versions = new Queue<long>([1, 2]),
        };
        var repairStore = new RecordingRepairStore
        {
            Lease = Lease(Document()),
        };
        var port = new ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePort(
            eventStore,
            repairStore);

        var result = await port.DeleteIfMatchesAsync(Request());

        result.Should().Be(NyxIdAuthorizationCatalogReplicaDeleteDisposition.SourceChanged);
        eventStore.VersionRequests.Should().Equal(ActorId, ActorId);
        repairStore.DeleteLeases.Should().BeEmpty();
    }

    private static NyxIdAuthorizationCatalogVersionRegressionRepairRequest Request() =>
        new(
            VerifiedOwnerSubject,
            ExpectedActorId: ActorId,
            BearerToken: "bearer-secret",
            ExpectedSourceStateVersion: 1,
            ExpectedDocumentStateVersion: 4,
            ExpectedDocumentLastEventId: "event-4",
            RepairRequestId: "repair-alpha",
            RepairReason: "rebuild from NyxID",
            RequestedBySubjectId: VerifiedOwnerSubject);

    private static AuthorizationOwnerIdentity Owner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = VerifiedOwnerSubject,
    };

    private static NyxIdAuthorizationCatalogDocument Document() => new()
    {
        Id = ActorId,
        ActorId = ActorId,
        StateVersion = 4,
        LastEventId = "event-4",
    };

    private static ElasticsearchProjectionDocumentRepairLease<
        NyxIdAuthorizationCatalogDocument,
        string> Lease(NyxIdAuthorizationCatalogDocument document)
    {
        var leaseType = typeof(ElasticsearchProjectionDocumentRepairLease<
            NyxIdAuthorizationCatalogDocument,
            string>);
        var constructor = leaseType
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Should()
            .ContainSingle()
            .Subject;
        return (ElasticsearchProjectionDocumentRepairLease<
            NyxIdAuthorizationCatalogDocument,
            string>)constructor.Invoke(
            [
                ActorId,
                document,
                "catalog-index-000001",
                7L,
                3L,
            ]);
    }

    private sealed class RecordingEventStore : IEventStore
    {
        public long Version { get; init; }

        public Queue<long> Versions { get; init; } = [];

        public List<string> VersionRequests { get; } = [];

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            VersionRequests.Add(agentId);
            return Task.FromResult(Versions.Count == 0 ? Version : Versions.Dequeue());
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
        : IElasticsearchProjectionDocumentRepairStore<
            NyxIdAuthorizationCatalogDocument,
            string>
    {
        public ElasticsearchProjectionDocumentRepairLease<
            NyxIdAuthorizationCatalogDocument,
            string>? Lease { get; init; }

        public ElasticsearchProjectionDocumentRepairDeleteDisposition DeleteDisposition
        {
            get;
            init;
        } = ElasticsearchProjectionDocumentRepairDeleteDisposition.Deleted;

        public List<string> InspectKeys { get; } = [];

        public List<ElasticsearchProjectionDocumentRepairLease<
            NyxIdAuthorizationCatalogDocument,
            string>> DeleteLeases { get; } = [];

        public Task<ElasticsearchProjectionDocumentRepairLease<
            NyxIdAuthorizationCatalogDocument,
            string>?> InspectAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            InspectKeys.Add(key);
            return Task.FromResult(Lease);
        }

        public Task<ElasticsearchProjectionDocumentRepairDeleteDisposition>
            DeleteIfUnchangedAsync(
                ElasticsearchProjectionDocumentRepairLease<
                    NyxIdAuthorizationCatalogDocument,
                    string> lease,
                CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DeleteLeases.Add(lease);
            return Task.FromResult(DeleteDisposition);
        }
    }
}
