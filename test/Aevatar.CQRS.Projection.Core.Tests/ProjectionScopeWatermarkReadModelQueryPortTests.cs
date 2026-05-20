using System.Reflection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

// Test-add (test-coverage/cluster-003):
//   Covers refactor-introduced behavior in ProjectionScopeWatermarkReadModel{.Partial,.MetadataProvider,.QueryPort}.cs:13-27.
//   Cluster intent: Watermark queries read a materialized read model instead of replaying event-store state in the query path.
public sealed class ProjectionScopeWatermarkReadModelQueryPortTests
{
    [Fact]
    public void ProjectionScopeWatermarkReadModel_ExposesActorScopedReadModelContract()
    {
        var updatedAt = new DateTimeOffset(2026, 5, 20, 8, 9, 10, TimeSpan.Zero);
        var readModel = new ProjectionScopeWatermarkReadModel
        {
            Id = "projection-scope:root:kind:durable",
        };

        readModel.UpdatedAt.Should().Be(DateTimeOffset.MinValue);

        readModel.UpdatedAt = updatedAt;

        readModel.ActorId.Should().Be(readModel.Id);
        readModel.UpdatedAt.Should().Be(updatedAt);
        readModel.UpdatedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void MetadataProvider_ExposesWatermarkIndexContract()
    {
        var metadata = new ProjectionScopeWatermarkReadModelMetadataProvider().Metadata;

        metadata.IndexName.Should().Be("projection-scope-watermarks");
        metadata.Mappings.Should().ContainKey("dynamic")
            .WhoseValue.Should().Be(true);
        metadata.Settings.Should().BeEmpty();
        metadata.Aliases.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_ThrowsWhenDocumentReaderIsNull()
    {
        var act = () => new ProjectionScopeWatermarkReadModelQueryPort(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_HasNoEventStoreDependency()
    {
        typeof(ProjectionScopeWatermarkReadModelQueryPort)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should()
            .ContainSingle()
            .Which
            .GetParameters()
            .Should()
            .NotContain(parameter => parameter.ParameterType == typeof(IEventStore));
    }

    [Fact]
    public async Task GetLastSuccessfulVersionAsync_UsesScopeActorIdAsDocumentKey()
    {
        var scopeKey = CreateScopeKey("root-actor", "session-42");
        var expectedDocumentKey = ProjectionScopeActorId.Build(scopeKey);
        var reader = new InMemoryWatermarkReader();
        reader.Upsert(new ProjectionScopeWatermarkReadModel
        {
            Id = expectedDocumentKey,
            Active = true,
            Released = false,
            LastSuccessfulVersion = 31,
        });
        var sut = new ProjectionScopeWatermarkReadModelQueryPort(reader);

        var watermark = await sut.GetLastSuccessfulVersionAsync(scopeKey);

        watermark.Should().Be(31);
        reader.LastGetKey.Should().Be(expectedDocumentKey);
        reader.QueryCallCount.Should().Be(0, "watermark lookup must be a direct materialized read, not a query-time rebuild path");
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(false, false, null)]
    [InlineData(true, true, null)]
    public async Task GetLastSuccessfulVersionAsync_ReturnsNullWhenDocumentIsMissingInactiveOrReleased(
        bool? active,
        bool? released,
        long? expectedWatermark)
    {
        var scopeKey = CreateScopeKey("root-actor", "session-42");
        var reader = new InMemoryWatermarkReader();
        if (active.HasValue && released.HasValue)
        {
            reader.Upsert(new ProjectionScopeWatermarkReadModel
            {
                Id = ProjectionScopeActorId.Build(scopeKey),
                Active = active.Value,
                Released = released.Value,
                LastSuccessfulVersion = 19,
            });
        }
        var sut = new ProjectionScopeWatermarkReadModelQueryPort(reader);

        var watermark = await sut.GetLastSuccessfulVersionAsync(scopeKey);

        watermark.Should().Be(expectedWatermark);
    }

    [Fact]
    public async Task GetLastSuccessfulVersionAsync_ReturnsWatermarkWhenActiveAndUnreleased()
    {
        var scopeKey = CreateScopeKey("root-actor", "session-42");
        var reader = new InMemoryWatermarkReader();
        reader.Upsert(new ProjectionScopeWatermarkReadModel
        {
            Id = ProjectionScopeActorId.Build(scopeKey),
            Active = true,
            Released = false,
            LastObservedVersion = 41,
            LastSuccessfulVersion = 37,
        });
        var sut = new ProjectionScopeWatermarkReadModelQueryPort(reader);

        var watermark = await sut.GetLastSuccessfulVersionAsync(scopeKey);

        watermark.Should().Be(37);
    }

    private static ProjectionRuntimeScopeKey CreateScopeKey(string rootActorId, string sessionId) =>
        new(rootActorId, "channel-bot-registration", ProjectionRuntimeMode.DurableMaterialization, sessionId);

    private sealed class InMemoryWatermarkReader
        : IProjectionDocumentReader<ProjectionScopeWatermarkReadModel, string>
    {
        private readonly Dictionary<string, ProjectionScopeWatermarkReadModel> _documents = new(StringComparer.Ordinal);

        public string? LastGetKey { get; private set; }

        public int QueryCallCount { get; private set; }

        public void Upsert(ProjectionScopeWatermarkReadModel document) => _documents[document.Id] = document;

        public Task<ProjectionScopeWatermarkReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastGetKey = key;
            return Task.FromResult(_documents.GetValueOrDefault(key));
        }

        public Task<ProjectionDocumentQueryResult<ProjectionScopeWatermarkReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            _ = query;
            ct.ThrowIfCancellationRequested();
            QueryCallCount++;
            return Task.FromResult(ProjectionDocumentQueryResult<ProjectionScopeWatermarkReadModel>.Empty);
        }
    }
}
