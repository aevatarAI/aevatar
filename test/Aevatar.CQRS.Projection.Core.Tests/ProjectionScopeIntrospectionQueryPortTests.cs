using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionScopeIntrospectionQueryPortTests
{
    [Fact]
    public void Constructor_DependsOnlyOnTheMaterializedDocumentReader()
    {
        typeof(ProjectionScopeIntrospectionQueryPort)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().ContainSingle().Which.GetParameters()
            .Should().ContainSingle().Which.ParameterType
            .Should().Be(typeof(IProjectionDocumentReader<ProjectionScopeStatusDocument, string>));
    }

    [Fact]
    public async Task GetAsync_ReturnsNullWhenTheMaterializedDocumentIsMissing()
    {
        var sut = new ProjectionScopeIntrospectionQueryPort(new RecordingReader());

        var result = await sut.GetAsync("scope-missing");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_MapsTheMaterializedScopeStatusHonestly()
    {
        var updatedAt = new DateTimeOffset(2026, 7, 30, 1, 2, 3, TimeSpan.Zero);
        var document = new ProjectionScopeStatusDocument
        {
            Id = "scope-alpha", ScopeActorId = "scope-alpha", RootActorId = "root-alpha",
            ProjectionKind = "search-index", SessionId = "session-alpha",
            Mode = ProjectionScopeMode.SessionObservation, Active = true, ObservationAttached = true,
            Released = false, StateVersion = 44, ReceivedEnvelopeTotal = 43,
            AttemptedEnvelopeTotal = 42, SuccessfulMaterializationTotal = 40,
            FailedAttemptTotal = 3, RetryExhaustedTotal = 1, RetryExhaustedFailureCount = 1,
            UnresolvedFailureCount = 2,
            FailureDiagnosticDroppedTotal = 5, UpdatedAt = updatedAt,
        };
        document.SourceVersions.Add(new ProjectionSourceVersionStatus
        {
            SourceActorId = "actor-alpha",
            HighestSeenVersion = 41,
            LastSuccessfulVersion = 40,
            VersionGap = 1,
        });
        var sut = new ProjectionScopeIntrospectionQueryPort(new RecordingReader(document));

        var result = await sut.GetAsync("scope-alpha");

        result.Should().BeEquivalentTo(new ProjectionScopeIntrospectionSnapshot(
            ScopeActorId: "scope-alpha", RootActorId: "root-alpha", ProjectionKind: "search-index",
            SessionId: "session-alpha", Mode: ProjectionRuntimeMode.SessionObservation, Active: true,
            ObservationAttached: true, Released: false, StateVersion: 44, ReceivedEnvelopeTotal: 43,
            AttemptedEnvelopeTotal: 42, SuccessfulMaterializationTotal: 40, FailedAttemptTotal: 3,
            RetryExhaustedTotal: 1, RetryExhaustedFailureCount: 1, UnresolvedFailureCount: 2,
            OldestUnresolvedFailureAt: null,
            FailureDiagnosticDroppedTotal: 5,
            SourceVersions: [new ProjectionSourceVersionSnapshot("actor-alpha", 41, 40, 1)],
            UpdatedAt: updatedAt));
    }

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(0, 20)]
    [InlineData(1, 1)]
    [InlineData(51, 50)]
    public async Task ListRecentEnvelopesAsync_BoundsTakeAndReturnsNewestFirst(int take, int expectedCount)
    {
        var document = new ProjectionScopeStatusDocument { Id = "scope-alpha", ScopeActorId = "scope-alpha" };
        for (var version = 1; version <= 50; version++)
        {
            document.RecentObservedEnvelopes.Add(new ProjectionObservedEnvelopeMetadata
            {
                EventId = $"evt-{version}", TypeUrl = $"type-{version}", StateVersion = version,
                TimestampUtc = Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero).AddSeconds(version)),
            });
        }
        var sut = new ProjectionScopeIntrospectionQueryPort(new RecordingReader(document));

        var result = await sut.ListRecentEnvelopesAsync("scope-alpha", take);

        result.Should().HaveCount(expectedCount);
        result[0].EventId.Should().Be("evt-50");
        result.Should().OnlyContain(item => item.GetType().GetProperty("Payload") == null);
    }

    [Fact]
    public async Task ListRecentEnvelopesAsync_ReturnsEmptyWhenTheDocumentIsMissing()
    {
        var sut = new ProjectionScopeIntrospectionQueryPort(new RecordingReader());

        var result = await sut.ListRecentEnvelopesAsync("scope-missing", 20);

        result.Should().BeEmpty();
    }

    private sealed class RecordingReader(params ProjectionScopeStatusDocument[] documents)
        : IProjectionDocumentReader<ProjectionScopeStatusDocument, string>
    {
        private readonly Dictionary<string, ProjectionScopeStatusDocument> _documents =
            documents.ToDictionary(document => document.Id, StringComparer.Ordinal);

        public Task<ProjectionScopeStatusDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_documents.GetValueOrDefault(key));
        }

        public Task<ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>> QueryAsync(
            ProjectionDocumentQuery query, CancellationToken ct = default) =>
            throw new InvalidOperationException("Introspection must read exactly one materialized document.");
    }
}
