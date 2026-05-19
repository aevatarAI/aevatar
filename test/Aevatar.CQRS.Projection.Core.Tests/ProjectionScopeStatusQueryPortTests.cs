using System.Reflection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionScopeStatusQueryPortTests
{
    [Fact]
    public void Constructor_ThrowsWhenDocumentReaderIsNull()
    {
        var act = () => new ProjectionScopeStatusQueryPort(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_HasNoEventStoreDependency()
    {
        typeof(ProjectionScopeStatusQueryPort)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should()
            .ContainSingle()
            .Which
            .GetParameters()
            .Should()
            .NotContain(parameter => parameter.ParameterType == typeof(IEventStore));
    }

    [Fact]
    public async Task GetLastSuccessfulVersionAsync_ReturnsNullWhenDocumentIsMissing()
    {
        var reader = new InMemoryStatusReader();
        var sut = new ProjectionScopeStatusQueryPort(reader);

        var watermark = await sut.GetLastSuccessfulVersionAsync(CreateScopeKey("missing"));

        watermark.Should().BeNull();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task GetLastSuccessfulVersionAsync_ReturnsNullWhenInactiveOrReleased(
        bool active,
        bool released)
    {
        var scopeKey = CreateScopeKey("root-actor");
        var reader = new InMemoryStatusReader();
        reader.Upsert(new ProjectionScopeStatusDocument
        {
            Id = ProjectionScopeActorId.Build(scopeKey),
            ScopeActorId = ProjectionScopeActorId.Build(scopeKey),
            Active = active,
            Released = released,
            LastSuccessfulVersion = 19,
        });
        var sut = new ProjectionScopeStatusQueryPort(reader);

        var watermark = await sut.GetLastSuccessfulVersionAsync(scopeKey);

        watermark.Should().BeNull();
    }

    [Fact]
    public async Task GetLastSuccessfulVersionAsync_ReturnsWatermarkWhenActiveAndUnreleased()
    {
        var scopeKey = CreateScopeKey("root-actor");
        var reader = new InMemoryStatusReader();
        reader.Upsert(new ProjectionScopeStatusDocument
        {
            Id = ProjectionScopeActorId.Build(scopeKey),
            ScopeActorId = ProjectionScopeActorId.Build(scopeKey),
            Active = true,
            Released = false,
            LastSuccessfulVersion = 23,
        });
        var sut = new ProjectionScopeStatusQueryPort(reader);

        var watermark = await sut.GetLastSuccessfulVersionAsync(scopeKey);

        watermark.Should().Be(23);
    }

    private static ProjectionRuntimeScopeKey CreateScopeKey(string rootActorId) =>
        new(rootActorId, "channel-bot-registration", ProjectionRuntimeMode.DurableMaterialization);

    private sealed class InMemoryStatusReader
        : IProjectionDocumentReader<ProjectionScopeStatusDocument, string>
    {
        private readonly Dictionary<string, ProjectionScopeStatusDocument> _documents = new(StringComparer.Ordinal);

        public void Upsert(ProjectionScopeStatusDocument document) => _documents[document.Id] = document;

        public Task<ProjectionScopeStatusDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_documents.GetValueOrDefault(key));
        }

        public Task<ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            _ = query;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectionDocumentQueryResult<ProjectionScopeStatusDocument>());
        }
    }
}
