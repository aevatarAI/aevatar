using System.Reflection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task Source_DoesNotReintroduceEventStoreReplay()
    {
        var source = await File.ReadAllTextAsync(FindRepoFile(
            "src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStatusQueryPort.cs"));

        source.Should().NotContain("IEventStore");
        source.Should().NotContain("GetEventsAsync");
        source.Should().NotContain("EventStoreProjectionScopeWatermarkQueryPort");
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

    [Fact]
    public async Task GetLastSuccessfulVersionAsync_ReturnsWatermarkMaterializedFromProjectionScopeCommittedState()
    {
        var services = new ServiceCollection();
        services.AddProjectionReadModelRuntime();
        services.AddInMemoryDocumentProjectionStore<ProjectionScopeStatusDocument, string>(
            static doc => doc.Id,
            static key => key);
        services.AddSingleton<IProjectionClock>(new FixedProjectionClock(
            new DateTimeOffset(2026, 5, 20, 1, 2, 3, TimeSpan.Zero)));
        services.AddSingleton<ProjectionScopeStatusProjector>();
        services.AddSingleton<IProjectionScopeWatermarkQueryPort, ProjectionScopeStatusQueryPort>();

        await using var provider = services.BuildServiceProvider();
        var projector = provider.GetRequiredService<ProjectionScopeStatusProjector>();
        var queryPort = provider.GetRequiredService<IProjectionScopeWatermarkQueryPort>();
        var scopeKey = CreateScopeKey("root-actor");
        var scopeActorId = ProjectionScopeActorId.Build(scopeKey);

        await projector.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = scopeActorId },
            CreateProjectionScopeCommittedEnvelope(
                scopeKey,
                lastObservedVersion: 29,
                lastSuccessfulVersion: 29,
                stateVersion: 4));

        var watermark = await queryPort.GetLastSuccessfulVersionAsync(scopeKey);

        watermark.Should().Be(29);
    }

    private static ProjectionRuntimeScopeKey CreateScopeKey(string rootActorId) =>
        new(rootActorId, "channel-bot-registration", ProjectionRuntimeMode.DurableMaterialization);

    private static EventEnvelope CreateProjectionScopeCommittedEnvelope(
        ProjectionRuntimeScopeKey scopeKey,
        long lastObservedVersion,
        long lastSuccessfulVersion,
        long stateVersion)
    {
        var scopeActorId = ProjectionScopeActorId.Build(scopeKey);
        var timestamp = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 5, 20, 4, 5, 6, TimeSpan.Zero));
        return new EventEnvelope
        {
            Id = $"committed-{stateVersion}",
            Timestamp = timestamp,
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = scopeActorId,
                    EventId = $"projection-scope-watermark-{stateVersion}",
                    Version = stateVersion,
                    Timestamp = timestamp,
                    EventType = ProjectionScopeWatermarkAdvancedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new ProjectionScopeWatermarkAdvancedEvent
                    {
                        LastSuccessfulVersion = lastSuccessfulVersion,
                        OccurredAtUtc = timestamp,
                    }),
                },
                StateRoot = Any.Pack(new ProjectionScopeState
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                    Mode = ProjectionScopeMode.DurableMaterialization,
                    Active = true,
                    ObservationAttached = true,
                    HighestSeenVersion = lastObservedVersion,
                    LastSuccessfulVersion = lastSuccessfulVersion,
                }),
            }),
        };
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath} from {AppContext.BaseDirectory}");
    }

    private sealed class FixedProjectionClock : IProjectionClock
    {
        public FixedProjectionClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

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
