using System.Reflection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Studio.Application.Studio.ProjectionRecovery;
using Aevatar.Studio.Infrastructure.ProjectionRecovery;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Studio.Workspace;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class ElasticsearchStudioWorkspaceVersionRegressionStorePortTests
{
    private const string ScopeId = "scope-alpha";
    private static readonly string ActorId = StudioWorkspaceConventions.BuildActorId(ScopeId);

    [Fact]
    public async Task DeleteIfMatchesAsync_WhenAuthorityChangesAfterLeaseValidation_ShouldNotDelete()
    {
        var eventStore = new RecordingEventStore(1, 2);
        var repairStore = new RecordingRepairStore
        {
            Lease = Lease(Document()),
        };
        var port = new ElasticsearchStudioWorkspaceVersionRegressionStorePort(
            eventStore,
            repairStore);

        var result = await port.DeleteIfMatchesAsync(Request());

        result.Should().Be(StudioWorkspaceReplicaDeleteDisposition.SourceChanged);
        eventStore.VersionRequests.Should().Equal(ActorId, ActorId);
        repairStore.InspectKeys.Should().ContainSingle().Which.Should().Be(ActorId);
        repairStore.DeleteLeases.Should().BeEmpty();
    }

    private static StudioWorkspaceVersionRegressionRepairRequest Request() =>
        new(
            ScopeId,
            ExpectedActorId: ActorId,
            ExpectedSourceStateVersion: 1,
            ExpectedDocumentStateVersion: 4,
            ExpectedDocumentLastEventId: "event-4",
            RepairRequestId: "repair-alpha",
            RepairReason: "restore authoritative workspace",
            RequestedBySubjectId: "operator-alpha");

    private static StudioWorkspaceCurrentStateDocument Document() => new()
    {
        Id = ActorId,
        ActorId = ActorId,
        StateVersion = 4,
        LastEventId = "event-4",
    };

    private static ElasticsearchProjectionDocumentRepairLease<
        StudioWorkspaceCurrentStateDocument,
        string> Lease(StudioWorkspaceCurrentStateDocument document)
    {
        var leaseType = typeof(ElasticsearchProjectionDocumentRepairLease<
            StudioWorkspaceCurrentStateDocument,
            string>);
        var constructor = leaseType
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Should()
            .ContainSingle()
            .Subject;
        return (ElasticsearchProjectionDocumentRepairLease<
            StudioWorkspaceCurrentStateDocument,
            string>)constructor.Invoke(
            [
                ActorId,
                document,
                "workspace-index-000001",
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
        : IElasticsearchProjectionDocumentRepairStore<
            StudioWorkspaceCurrentStateDocument,
            string>
    {
        public ElasticsearchProjectionDocumentRepairLease<
            StudioWorkspaceCurrentStateDocument,
            string>? Lease { get; init; }

        public List<string> InspectKeys { get; } = [];

        public List<ElasticsearchProjectionDocumentRepairLease<
            StudioWorkspaceCurrentStateDocument,
            string>> DeleteLeases { get; } = [];

        public Task<ElasticsearchProjectionDocumentRepairLease<
            StudioWorkspaceCurrentStateDocument,
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
                    StudioWorkspaceCurrentStateDocument,
                    string> lease,
                CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DeleteLeases.Add(lease);
            return Task.FromResult(
                ElasticsearchProjectionDocumentRepairDeleteDisposition.Deleted);
        }
    }
}
