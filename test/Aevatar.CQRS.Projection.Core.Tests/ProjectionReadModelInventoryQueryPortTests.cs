using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionReadModelInventoryQueryPortTests
{
    [Fact]
    public void Constructor_ThrowsWhenDescriptorsAreNull()
    {
        var act = () => new ProjectionReadModelInventoryQueryPort(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GetInventoryAsync_ReturnsEmptyGroupsWhenNoDescriptorsAreRegistered()
    {
        var sut = new ProjectionReadModelInventoryQueryPort([]);

        var inventory = await sut.GetInventoryAsync();

        inventory.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task GetInventoryAsync_MapsDescriptorAndSnapshotFieldsIntoInventoryItem()
    {
        var updated = new DateTimeOffset(2026, 5, 20, 4, 5, 6, TimeSpan.Zero);
        var descriptor = new FakeDescriptor(
            name: "workflow-execution",
            shape: ProjectionReadModelSinkShape.Document,
            engine: "Elasticsearch",
            actorKind: "WorkflowExecutionGAgent",
            snapshot: new ProjectionReadModelInventorySnapshot(
                Count: 42,
                MaxStateVersion: 7,
                LatestUpdatedAt: updated));
        var sut = new ProjectionReadModelInventoryQueryPort([descriptor]);

        var inventory = await sut.GetInventoryAsync();

        var item = inventory.Groups.Should().ContainSingle().Which.Items.Should().ContainSingle().Subject;
        item.Name.Should().Be("workflow-execution");
        item.Actor.Should().Be("WorkflowExecutionGAgent");
        item.Version.Should().Be(7);
        item.Updated.Should().Be(updated);
        item.Count.Should().Be(42);
    }

    [Fact]
    public async Task GetInventoryAsync_GroupsBySinkShapeAndCarriesGroupShapeAndEngine()
    {
        var docDescriptor = new FakeDescriptor("a-doc", ProjectionReadModelSinkShape.Document, "Elasticsearch", "DocActor");
        var memDescriptor = new FakeDescriptor("a-mem", ProjectionReadModelSinkShape.Memory, "dev/InMemory", "MemActor");
        var sut = new ProjectionReadModelInventoryQueryPort([docDescriptor, memDescriptor]);

        var inventory = await sut.GetInventoryAsync();

        inventory.Groups.Should().HaveCount(2);
        var documentGroup = inventory.Groups.Single(group => group.Shape == ProjectionReadModelSinkShape.Document);
        documentGroup.Engine.Should().Be("Elasticsearch");
        documentGroup.Items.Should().ContainSingle().Which.Name.Should().Be("a-doc");
        var memoryGroup = inventory.Groups.Single(group => group.Shape == ProjectionReadModelSinkShape.Memory);
        memoryGroup.Engine.Should().Be("dev/InMemory");
        memoryGroup.Items.Should().ContainSingle().Which.Name.Should().Be("a-mem");
    }

    [Fact]
    public async Task GetInventoryAsync_OrdersGroupsBySinkShapeRegardlessOfDescriptorOrder()
    {
        var memDescriptor = new FakeDescriptor("mem", ProjectionReadModelSinkShape.Memory, "dev/InMemory", "MemActor");
        var graphDescriptor = new FakeDescriptor("graph", ProjectionReadModelSinkShape.Graph, "Neo4j", "GraphActor");
        var docDescriptor = new FakeDescriptor("doc", ProjectionReadModelSinkShape.Document, "Elasticsearch", "DocActor");
        // Deliberately reversed so the assertion proves ordering rather than incidental insertion order.
        var sut = new ProjectionReadModelInventoryQueryPort([memDescriptor, graphDescriptor, docDescriptor]);

        var inventory = await sut.GetInventoryAsync();

        inventory.Groups.Select(group => group.Shape).Should().ContainInOrder(
            ProjectionReadModelSinkShape.Document,
            ProjectionReadModelSinkShape.Graph,
            ProjectionReadModelSinkShape.Memory);
    }

    [Fact]
    public async Task GetInventoryAsync_SortsItemsWithinGroupByNameOrdinal()
    {
        var charlie = new FakeDescriptor("charlie", ProjectionReadModelSinkShape.Document, "Elasticsearch", "Actor");
        var alpha = new FakeDescriptor("alpha", ProjectionReadModelSinkShape.Document, "Elasticsearch", "Actor");
        var bravo = new FakeDescriptor("bravo", ProjectionReadModelSinkShape.Document, "Elasticsearch", "Actor");
        var sut = new ProjectionReadModelInventoryQueryPort([charlie, alpha, bravo]);

        var inventory = await sut.GetInventoryAsync();

        inventory.Groups.Should().ContainSingle().Which.Items.Select(item => item.Name)
            .Should().ContainInOrder("alpha", "bravo", "charlie");
    }

    [Fact]
    public async Task GetInventoryAsync_KeepsEngineOfFirstDescriptorInSharedShapeGroup()
    {
        var first = new FakeDescriptor("first", ProjectionReadModelSinkShape.Document, "Elasticsearch", "Actor");
        var second = new FakeDescriptor("second", ProjectionReadModelSinkShape.Document, "Other-engine", "Actor");
        var sut = new ProjectionReadModelInventoryQueryPort([first, second]);

        var inventory = await sut.GetInventoryAsync();

        inventory.Groups.Should().ContainSingle().Which.Engine.Should().Be("Elasticsearch");
    }

    [Fact]
    public async Task GetInventoryAsync_DegradesToNullMetricsWhenOneDescriptorCaptureThrows()
    {
        var healthy = new FakeDescriptor(
            name: "healthy",
            shape: ProjectionReadModelSinkShape.Document,
            engine: "Elasticsearch",
            actorKind: "Actor",
            snapshot: new ProjectionReadModelInventorySnapshot(Count: 3, MaxStateVersion: 9, LatestUpdatedAt: null));
        var unhealthy = new ThrowingDescriptor(
            name: "unhealthy",
            shape: ProjectionReadModelSinkShape.Document,
            engine: "Elasticsearch",
            actorKind: "Actor",
            exception: new InvalidOperationException("store unavailable"));
        var sut = new ProjectionReadModelInventoryQueryPort([healthy, unhealthy]);

        var inventory = await sut.GetInventoryAsync();

        var items = inventory.Groups.Should().ContainSingle().Which.Items;
        var healthyItem = items.Single(item => item.Name == "healthy");
        healthyItem.Count.Should().Be(3);
        healthyItem.Version.Should().Be(9);
        var unhealthyItem = items.Single(item => item.Name == "unhealthy");
        unhealthyItem.Count.Should().BeNull();
        unhealthyItem.Version.Should().BeNull();
        unhealthyItem.Updated.Should().BeNull();
        // The degraded item still carries its identity so the panel shows it as "unknown", not absent.
        unhealthyItem.Actor.Should().Be("Actor");
    }

    [Fact]
    public async Task GetInventoryAsync_PropagatesOperationCanceledFromDescriptorCapture()
    {
        var descriptor = new ThrowingDescriptor(
            name: "cancelled",
            shape: ProjectionReadModelSinkShape.Document,
            engine: "Elasticsearch",
            actorKind: "Actor",
            exception: new OperationCanceledException());
        var sut = new ProjectionReadModelInventoryQueryPort([descriptor]);

        var act = () => sut.GetInventoryAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetInventoryAsync_ForwardsCancellationTokenToDescriptorCapture()
    {
        using var cts = new CancellationTokenSource();
        var descriptor = new TokenCapturingDescriptor(
            name: "token",
            shape: ProjectionReadModelSinkShape.Document,
            engine: "Elasticsearch",
            actorKind: "Actor");
        var sut = new ProjectionReadModelInventoryQueryPort([descriptor]);

        await sut.GetInventoryAsync(cts.Token);

        descriptor.ObservedToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task DocumentDescriptor_CaptureAsync_QueriesNewestDocumentWithTotalCount()
    {
        using var cts = new CancellationTokenSource();
        var updated = new DateTimeOffset(2026, 6, 30, 1, 2, 3, TimeSpan.Zero);
        var reader = new RecordingDocumentReader(new TestReadModel
        {
            Id = "read-1",
            ActorId = "actor-1",
            StateVersion = 42,
            LastEventId = "event-42",
            UpdatedAt = updated,
        }, totalCount: 17);
        var descriptor = new ProjectionDocumentReadModelDescriptor<TestReadModel>(
            "test-documents",
            ProjectionReadModelSinkShape.Document,
            "InMemory",
            "test.actor",
            reader);

        var snapshot = await descriptor.CaptureAsync(cts.Token);

        snapshot.Count.Should().Be(17);
        snapshot.MaxStateVersion.Should().Be(42);
        snapshot.LatestUpdatedAt.Should().Be(updated);
        reader.ObservedToken.Should().Be(cts.Token);
        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Take.Should().Be(1);
        reader.LastQuery.IncludeTotalCount.Should().BeTrue();
        reader.LastQuery.Sorts.Select(sort => (sort.FieldPath, sort.Direction)).Should().ContainInOrder(
            (nameof(IProjectionReadModel.UpdatedAt), ProjectionDocumentSortDirection.Desc),
            (nameof(IProjectionReadModel.StateVersion), ProjectionDocumentSortDirection.Desc));
    }

    [Fact]
    public async Task DocumentDescriptor_CaptureAsync_ReturnsNullVersionAndUpdatedAtWhenStoreIsEmpty()
    {
        var descriptor = new ProjectionDocumentReadModelDescriptor<TestReadModel>(
            "test-documents",
            ProjectionReadModelSinkShape.Document,
            "InMemory",
            "test.actor",
            new RecordingDocumentReader(readModel: null, totalCount: 0));

        var snapshot = await descriptor.CaptureAsync();

        snapshot.Count.Should().Be(0);
        snapshot.MaxStateVersion.Should().BeNull();
        snapshot.LatestUpdatedAt.Should().BeNull();
    }

    [Fact]
    public async Task DocumentDescriptor_CaptureAsync_NormalizesDefaultUpdatedAtToNull()
    {
        var descriptor = new ProjectionDocumentReadModelDescriptor<TestReadModel>(
            "test-documents",
            ProjectionReadModelSinkShape.Document,
            "InMemory",
            "test.actor",
            new RecordingDocumentReader(new TestReadModel
            {
                Id = "read-1",
                ActorId = "actor-1",
                StateVersion = 5,
                LastEventId = "event-5",
                UpdatedAt = default,
            }, totalCount: 1));

        var snapshot = await descriptor.CaptureAsync();

        snapshot.MaxStateVersion.Should().Be(5);
        snapshot.LatestUpdatedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void DocumentDescriptor_Constructor_RejectsBlankIdentityFields(string value)
    {
        var reader = new RecordingDocumentReader(readModel: null, totalCount: 0);

        var blankName = () => new ProjectionDocumentReadModelDescriptor<TestReadModel>(
            value,
            ProjectionReadModelSinkShape.Document,
            "InMemory",
            "test.actor",
            reader);
        var blankEngine = () => new ProjectionDocumentReadModelDescriptor<TestReadModel>(
            "test-documents",
            ProjectionReadModelSinkShape.Document,
            value,
            "test.actor",
            reader);
        var blankActorKind = () => new ProjectionDocumentReadModelDescriptor<TestReadModel>(
            "test-documents",
            ProjectionReadModelSinkShape.Document,
            "InMemory",
            value,
            reader);

        blankName.Should().Throw<ArgumentException>().WithParameterName("name");
        blankEngine.Should().Throw<ArgumentException>().WithParameterName("engine");
        blankActorKind.Should().Throw<ArgumentException>().WithParameterName("actorKind");
    }

    private class FakeDescriptor : IProjectionReadModelDescriptor
    {
        private readonly ProjectionReadModelInventorySnapshot _snapshot;

        public FakeDescriptor(
            string name,
            ProjectionReadModelSinkShape shape,
            string engine,
            string actorKind,
            ProjectionReadModelInventorySnapshot? snapshot = null)
        {
            Name = name;
            Shape = shape;
            Engine = engine;
            ActorKind = actorKind;
            _snapshot = snapshot ?? new ProjectionReadModelInventorySnapshot(
                Count: null,
                MaxStateVersion: null,
                LatestUpdatedAt: null);
        }

        public string Name { get; }

        public ProjectionReadModelSinkShape Shape { get; }

        public string Engine { get; }

        public string ActorKind { get; }

        public virtual Task<ProjectionReadModelInventorySnapshot> CaptureAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshot);
        }
    }

    private sealed class ThrowingDescriptor : FakeDescriptor
    {
        private readonly Exception _exception;

        public ThrowingDescriptor(
            string name,
            ProjectionReadModelSinkShape shape,
            string engine,
            string actorKind,
            Exception exception)
            : base(name, shape, engine, actorKind)
        {
            _exception = exception;
        }

        public override Task<ProjectionReadModelInventorySnapshot> CaptureAsync(CancellationToken ct = default) =>
            Task.FromException<ProjectionReadModelInventorySnapshot>(_exception);
    }

    private sealed class TokenCapturingDescriptor : FakeDescriptor
    {
        public TokenCapturingDescriptor(
            string name,
            ProjectionReadModelSinkShape shape,
            string engine,
            string actorKind)
            : base(name, shape, engine, actorKind)
        {
        }

        public CancellationToken ObservedToken { get; private set; }

        public override Task<ProjectionReadModelInventorySnapshot> CaptureAsync(CancellationToken ct = default)
        {
            ObservedToken = ct;
            return base.CaptureAsync(ct);
        }
    }

    private sealed class RecordingDocumentReader(TestReadModel? readModel, long? totalCount)
        : IProjectionDocumentReader<TestReadModel, string>
    {
        public ProjectionDocumentQuery? LastQuery { get; private set; }

        public CancellationToken ObservedToken { get; private set; }

        public Task<TestReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            _ = key;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<TestReadModel?>(null);
        }

        public Task<ProjectionDocumentQueryResult<TestReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            LastQuery = query;
            ObservedToken = ct;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectionDocumentQueryResult<TestReadModel>
            {
                Items = readModel is null ? [] : [readModel],
                TotalCount = totalCount,
            });
        }
    }

    private sealed class TestReadModel : IProjectionReadModel
    {
        public string Id { get; init; } = string.Empty;

        public string ActorId { get; init; } = string.Empty;

        public long StateVersion { get; init; }

        public string LastEventId { get; init; } = string.Empty;

        public DateTimeOffset UpdatedAt { get; init; }
    }
}
