using System.Reflection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionGraphWriterTests
{
    [Fact]
    public async Task UpsertAsync_WhenGraphProviderIsDisabled_ShouldSkipMaterializationAndStoreWrite()
    {
        var store = new RecordingGraphStore();
        var materializer = new CountingGraphMaterializer();
        var writer = new ProjectionGraphWriter<TestGraphReadModel>(
            store,
            materializer,
            providerStatus: new ProjectionGraphProviderStatus("Disabled", Enabled: false));

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-disabled",
            StateVersion = 1,
            GraphScope = "scope-disabled",
        }, "test-graph");

        materializer.InvocationCount.Should().Be(0);
        store.LastReplacement.Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_ShouldReplaceOwnerGraphAndRemoveDisconnectedStaleEdges()
    {
        var store = new InMemoryProjectionGraphStore();
        var writer = CreateWriter(store);

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("root"), Node("left"), Node("orphan-a"), Node("orphan-b")],
            GraphEdges = [Edge("edge-root", "root", "left"), Edge("edge-orphan", "orphan-a", "orphan-b")],
        }, "test-graph");

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("root"), Node("left")],
            GraphEdges = [Edge("edge-root", "root", "left")],
        }, "test-graph");

        var rootNeighbors = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "root",
            Direction = ProjectionGraphDirection.Both,
            Take = 20,
        });
        var orphanNeighbors = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "orphan-a",
            Direction = ProjectionGraphDirection.Both,
            Take = 20,
        });

        rootNeighbors.Select(x => x.EdgeId).Should().ContainSingle("edge-root");
        orphanNeighbors.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldPreserveEdgesOwnedByAnotherReadModel()
    {
        var store = new InMemoryProjectionGraphStore();
        var writer = CreateWriter(store);

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("a"), Node("b")],
            GraphEdges = [Edge("edge-owner-1", "a", "b")],
        }, "test-graph");
        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-2",
            GraphScope = "scope-1",
            GraphNodes = [Node("c"), Node("d")],
            GraphEdges = [Edge("edge-owner-2", "c", "d")],
        }, "test-graph");
        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("a"), Node("b")],
            GraphEdges = [],
        }, "test-graph");

        var owner1Edges = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "a",
            Direction = ProjectionGraphDirection.Both,
            Take = 20,
        });
        var owner2Edges = await store.GetNeighborsAsync(new ProjectionGraphQuery
        {
            Scope = "scope-1",
            RootNodeId = "c",
            Direction = ProjectionGraphDirection.Both,
            Take = 20,
        });

        owner1Edges.Should().BeEmpty();
        owner2Edges.Select(x => x.EdgeId).Should().ContainSingle("edge-owner-2");
    }

    [Fact]
    public async Task UpsertAsync_WithEmptyGraphCollections_ShouldCleanupManagedResources()
    {
        var store = new InMemoryProjectionGraphStore();
        var writer = CreateWriter(store);

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("root"), Node("leaf")],
            GraphEdges = [Edge("edge-root-leaf", "root", "leaf")],
        }, "test-graph");
        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [],
            GraphEdges = [],
        }, "test-graph");

        var ownerId = BuildOwnerId("owner-1");
        (await store.ListNodesByOwnerAsync("scope-1", ownerId, take: 100)).Should().BeEmpty();
        (await store.ListEdgesByOwnerAsync("scope-1", ownerId, take: 100)).Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldKeepManagedNodeWhenForeignEdgeStillReferencesIt()
    {
        var store = new InMemoryProjectionGraphStore();
        var writer = CreateWriter(store);

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("root"), Node("stale")],
            GraphEdges = [],
        }, "test-graph");

        await store.UpsertEdgeAsync(new ProjectionGraphEdge
        {
            Scope = "scope-1",
            EdgeId = "external-edge",
            EdgeType = "LINK",
            FromNodeId = "stale",
            ToNodeId = "external-node",
            Properties = new Dictionary<string, string>(StringComparer.Ordinal),
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("root")],
            GraphEdges = [],
        }, "test-graph");

        var ownerNodes = await store.ListNodesByOwnerAsync("scope-1", BuildOwnerId("owner-1"), take: 100);
        ownerNodes.Select(x => x.NodeId).Should().Contain("stale");
    }

    [Fact]
    public async Task UpsertAsync_ShouldNormalizeInvalidNodesAndEdges()
    {
        var store = new InMemoryProjectionGraphStore();
        var writer = CreateWriter(store);

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-2",
            GraphScope = "scope-1",
            GraphNodes =
            [
                new ProjectionGraphNode
                {
                    Scope = "scope-1",
                    NodeId = "valid-node",
                    NodeType = " ",
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                    UpdatedAt = default,
                },
                new ProjectionGraphNode
                {
                    Scope = "scope-1",
                    NodeId = " ",
                    NodeType = "Actor",
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
            GraphEdges =
            [
                new ProjectionGraphEdge
                {
                    Scope = "scope-1",
                    EdgeId = "valid-edge",
                    EdgeType = "LINK",
                    FromNodeId = "valid-node",
                    ToNodeId = "target-node",
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                    UpdatedAt = default,
                },
                new ProjectionGraphEdge
                {
                    Scope = "scope-1",
                    EdgeId = "",
                    EdgeType = "LINK",
                    FromNodeId = "valid-node",
                    ToNodeId = "target-node",
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
        }, "test-graph");

        var ownerId = BuildOwnerId("owner-2");
        var nodes = await store.ListNodesByOwnerAsync("scope-1", ownerId, take: 100);
        var edges = await store.ListEdgesByOwnerAsync("scope-1", ownerId, take: 100);

        nodes.Select(x => x.NodeId).Should().Equal("valid-node");
        nodes[0].NodeType.Should().Be("Unknown");
        nodes[0].UpdatedAt.Should().NotBe(default);
        edges.Select(x => x.EdgeId).Should().Equal("valid-edge");
        edges[0].UpdatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task UpsertAsync_ShouldCarryProvenanceAndLogCompletedConstruction()
    {
        var store = new RecordingGraphStore();
        var logger = new RecordingLogger<ProjectionGraphWriter<TestGraphReadModel>>();
        var writer = new ProjectionGraphWriter<TestGraphReadModel>(
            store,
            new TestGraphMaterializer(),
            logger);

        await writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-telemetry",
            StateVersion = 42,
            GraphScope = "scope-telemetry",
            GraphNodes = [Node("root"), Node("leaf")],
            GraphEdges = [Edge("edge-root-leaf", "root", "leaf")],
        }, " graph-telemetry ");

        store.LastReplacement.Should().NotBeNull();
        var graph = store.LastReplacement!;
        graph.ProjectionKind.Should().Be("graph-telemetry");
        graph.StateVersion.Should().Be(42);
        graph.Nodes.Should().HaveCount(2);
        graph.Edges.Should().ContainSingle();

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Properties["Operation"].Should().Be("construct_owner_graph");
        entry.Properties["Result"].Should().Be("completed");
        entry.Properties["ProjectionKind"].Should().Be("graph-telemetry");
        entry.Properties["StateVersion"].Should().Be(42L);
        entry.Properties["Scope"].Should().Be("scope-telemetry");
        entry.Properties["OwnerId"].Should().Be(BuildOwnerId("owner-telemetry"));
        entry.Properties["NodeCount"].Should().Be(2);
        entry.Properties["EdgeCount"].Should().Be(1);
        entry.Properties["ErrorType"].Should().BeNull();
        entry.Properties["ElapsedMs"].Should().BeOfType<double>()
            .Which.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task UpsertAsync_ShouldTimeOnlyMaterializerAndKeepProvenance()
    {
        var steps = new List<string>();
        var store = new RecordingGraphStore();
        var logger = new RecordingLogger<ProjectionGraphWriter<TestGraphReadModel>>
        {
            OnLog = () => steps.Add("log"),
        };
        var writer = CreateTimedWriter(
            store,
            new TestGraphMaterializer(() => steps.Add("materialize")),
            logger,
            () =>
            {
                steps.Add("timestamp");
                return 1234;
            },
            startedAt =>
            {
                startedAt.Should().Be(1234);
                steps.Add("elapsed");
                return TimeSpan.FromMilliseconds(17);
            });
        var readModel = new TestGraphReadModel
        {
            Id = "owner-timed",
            StateVersion = 52,
            OnIdRead = () => steps.Add("owner-id"),
            OnStateVersionRead = () => steps.Add("state-version"),
            GraphScope = "scope-timed",
            GraphNodes = new RecordingReadOnlyList<ProjectionGraphNode>(
                [Node("root")],
                () => steps.Add("normalize-nodes")),
            GraphEdges = [],
        };

        await writer.UpsertAsync(readModel, " graph-timed ");

        steps.Should().ContainInOrder(
            "state-version",
            "timestamp",
            "materialize",
            "elapsed",
            "owner-id",
            "normalize-nodes",
            "log");
        steps.Count(x => x == "timestamp").Should().Be(1);
        steps.Count(x => x == "elapsed").Should().Be(1);
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Properties["ElapsedMs"].Should().Be(17d);
        store.LastReplacement.Should().BeEquivalentTo(new
        {
            ProjectionKind = "graph-timed",
            StateVersion = 52L,
            Scope = "scope-timed",
            OwnerId = BuildOwnerId("owner-timed"),
        });
    }

    [Fact]
    public async Task UpsertAsync_WhenConstructionFailsAfterMaterialization_ShouldLogFailure()
    {
        var failedLogger = new RecordingLogger<ProjectionGraphWriter<TestGraphReadModel>>();
        var failedWriter = new ProjectionGraphWriter<TestGraphReadModel>(
            new RecordingGraphStore(),
            new TestGraphMaterializer(),
            failedLogger);
        Func<Task> fail = () => failedWriter.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-failed",
            StateVersion = 43,
            GraphScope = " ",
            GraphNodes = [Node("root")],
        }, "graph-failed");

        await fail.Should().ThrowAsync<InvalidOperationException>();
        var failedEntry = failedLogger.Entries.Should().ContainSingle().Subject;
        failedEntry.Properties["Result"].Should().Be("failed");
        failedEntry.Properties["ProjectionKind"].Should().Be("graph-failed");
        failedEntry.Properties["StateVersion"].Should().Be(43L);
        failedEntry.Level.Should().Be(LogLevel.Error);
        failedEntry.Properties["NodeCount"].Should().BeNull();
        failedEntry.Properties["EdgeCount"].Should().BeNull();
        failedEntry.Properties["ErrorType"].Should().Be(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task UpsertAsync_WhenPreCancelled_ShouldNotEnterOrLogConstruction()
    {
        var logger = new RecordingLogger<ProjectionGraphWriter<TestGraphReadModel>>();
        var materializer = new CountingGraphMaterializer();
        var writer = CreateTimedWriter(
            new RecordingGraphStore(),
            materializer,
            logger,
            () => throw new InvalidOperationException("timestamp must not be read"),
            _ => throw new InvalidOperationException("elapsed must not be read"));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Func<Task> act = () => writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-cancelled",
            StateVersion = 44,
            GraphScope = "scope-cancelled",
        }, "graph-cancelled", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        materializer.InvocationCount.Should().Be(0);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_WhenMaterializerThrows_ShouldLogCapturedElapsed()
    {
        var logger = new RecordingLogger<ProjectionGraphWriter<TestGraphReadModel>>();
        var writer = CreateTimedWriter(
            new RecordingGraphStore(),
            new FailingGraphMaterializer(),
            logger,
            () => 201,
            startedAt =>
            {
                startedAt.Should().Be(201);
                return TimeSpan.FromMilliseconds(23);
            });
        Func<Task> act = () => writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-materializer-failed",
            StateVersion = 45,
            GraphScope = "scope-materializer-failed",
        }, "graph-materializer-failed");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("materializer failed");
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Properties["Result"].Should().Be("failed");
        entry.Properties["ElapsedMs"].Should().Be(23d);
        entry.Properties["ErrorType"].Should().Be(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task UpsertAsync_WhenCallerIsCancelledInsideMaterializer_ShouldLogCapturedElapsed()
    {
        using var cts = new CancellationTokenSource();
        var logger = new RecordingLogger<ProjectionGraphWriter<TestGraphReadModel>>();
        var writer = CreateTimedWriter(
            new RecordingGraphStore(),
            new CancellingGraphMaterializer(cts),
            logger,
            () => 301,
            startedAt =>
            {
                startedAt.Should().Be(301);
                return TimeSpan.FromMilliseconds(29);
            });
        Func<Task> act = () => writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-materializer-cancelled",
            StateVersion = 46,
            GraphScope = "scope-materializer-cancelled",
        }, "graph-materializer-cancelled", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Properties["Result"].Should().Be("cancelled");
        entry.Properties["ElapsedMs"].Should().Be(29d);
        entry.Properties["ErrorType"].Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_WhenMaterializerThrowsUnrelatedCancellation_ShouldLogFailure()
    {
        var logger = new RecordingLogger<ProjectionGraphWriter<TestGraphReadModel>>();
        var writer = new ProjectionGraphWriter<TestGraphReadModel>(
            new RecordingGraphStore(),
            new UnrelatedCancellationGraphMaterializer(),
            logger);
        Func<Task> act = () => writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-unrelated-cancellation",
            StateVersion = 46,
            GraphScope = "scope-unrelated-cancellation",
        }, "graph-unrelated-cancellation");

        await act.Should().ThrowAsync<OperationCanceledException>();
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Properties["Result"].Should().Be("failed");
        entry.Properties["StateVersion"].Should().Be(46L);
        entry.Properties["NodeCount"].Should().BeNull();
        entry.Properties["EdgeCount"].Should().BeNull();
        entry.Properties["ErrorType"].Should().Be(nameof(OperationCanceledException));
    }

    [Fact]
    public async Task UpsertAsync_WhenLoggerThrows_ShouldStillReplaceOwnerGraph()
    {
        var store = new RecordingGraphStore();
        var logger = new RecordingLogger<ProjectionGraphWriter<TestGraphReadModel>>
        {
            ThrowOnLog = true,
        };
        var writer = new ProjectionGraphWriter<TestGraphReadModel>(
            store,
            new TestGraphMaterializer(),
            logger);

        Func<Task> act = () => writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-safe-logging",
            StateVersion = 45,
            GraphScope = "scope-safe-logging",
        }, "graph-safe-logging");

        await act.Should().NotThrowAsync();
        store.LastReplacement.Should().NotBeNull();
    }

    [Fact]
    public async Task UpsertAsync_WhenStoreThrowsSynchronously_ShouldNotLogConstructionFailure()
    {
        var store = new RecordingGraphStore
        {
            SynchronousException = new InvalidOperationException("store failed"),
        };
        var logger = new RecordingLogger<ProjectionGraphWriter<TestGraphReadModel>>();
        var writer = new ProjectionGraphWriter<TestGraphReadModel>(
            store,
            new TestGraphMaterializer(),
            logger);
        Func<Task> act = () => writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-store-failure",
            StateVersion = 47,
            GraphScope = "scope-store-failure",
        }, "graph-store-failure");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("store failed");
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Properties["Result"].Should().Be("completed");
        entry.Properties["ErrorType"].Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_WhenReadModelIdOrGraphScopeIsMissing_ShouldThrow()
    {
        var store = new InMemoryProjectionGraphStore();
        var writer = CreateWriter(store);

        Func<Task> emptyId = () => writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "",
            GraphScope = "scope-1",
            GraphNodes = [Node("root")],
        }, "test-graph");
        Func<Task> emptyScope = () => writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = " ",
            GraphNodes = [Node("root")],
        }, "test-graph");
        Func<Task> emptyProjectionKind = () => writer.UpsertAsync(new TestGraphReadModel
        {
            Id = "owner-1",
            GraphScope = "scope-1",
            GraphNodes = [Node("root")],
        }, " ");

        await emptyId.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires a non-empty Id*");
        await emptyScope.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Graph scope is required*");
        await emptyProjectionKind.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Projection kind is required*");
    }

    private static string BuildOwnerId(string id) => $"{typeof(TestGraphReadModel).FullName}:{id}";

    private static ProjectionGraphWriter<TestGraphReadModel> CreateWriter(IProjectionGraphStore store) =>
        new(
            store,
            new TestGraphMaterializer(),
            NullLogger<ProjectionGraphWriter<TestGraphReadModel>>.Instance);

    private static ProjectionGraphWriter<TestGraphReadModel> CreateTimedWriter(
        IProjectionGraphStore store,
        IProjectionGraphMaterializer<TestGraphReadModel> materializer,
        ILogger<ProjectionGraphWriter<TestGraphReadModel>> logger,
        Func<long> getTimestamp,
        Func<long, TimeSpan> getElapsedTime)
    {
        var constructor = typeof(ProjectionGraphWriter<TestGraphReadModel>).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(IProjectionGraphStore),
                typeof(IProjectionGraphMaterializer<TestGraphReadModel>),
                typeof(ILogger<ProjectionGraphWriter<TestGraphReadModel>>),
                typeof(Func<long>),
                typeof(Func<long, TimeSpan>),
            ],
            modifiers: null);

        constructor.Should().NotBeNull();
        return (ProjectionGraphWriter<TestGraphReadModel>)constructor!.Invoke(
            [store, materializer, logger, getTimestamp, getElapsedTime]);
    }

    private sealed class RecordingGraphStore : IProjectionGraphStore
    {
        public ProjectionOwnedGraph? LastReplacement { get; private set; }

        public Exception? SynchronousException { get; init; }

        public Task ReplaceOwnerGraphAsync(ProjectionOwnedGraph graph, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (SynchronousException != null)
                throw SynchronousException;

            LastReplacement = graph;
            return Task.CompletedTask;
        }

        public Task UpsertNodeAsync(ProjectionGraphNode node, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpsertEdgeAsync(ProjectionGraphEdge edge, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteNodeAsync(string scope, string nodeId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectionGraphNode>> ListNodesByOwnerAsync(
            string scope,
            string ownerId,
            int skip = 0,
            int take = 5000,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectionGraphEdge>> ListEdgesByOwnerAsync(
            string scope,
            string ownerId,
            int skip = 0,
            int take = 5000,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectionGraphEdge>> GetNeighborsAsync(
            ProjectionGraphQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ProjectionGraphSubgraph> GetSubgraphAsync(
            ProjectionGraphQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public bool ThrowOnLog { get; init; }

        public Action? OnLog { get; init; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            OnLog?.Invoke();
            if (ThrowOnLog)
                throw new InvalidOperationException("logger failed");

            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            Entries.Add(new LogEntry(logLevel, properties));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        IReadOnlyDictionary<string, object?> Properties);

    private static ProjectionGraphNode Node(string nodeId)
    {
        return new ProjectionGraphNode
        {
            Scope = "scope-1",
            NodeId = nodeId,
            NodeType = "Actor",
            Properties = new Dictionary<string, string>(StringComparer.Ordinal),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static ProjectionGraphEdge Edge(string edgeId, string fromNodeId, string toNodeId)
    {
        return new ProjectionGraphEdge
        {
            Scope = "scope-1",
            EdgeId = edgeId,
            EdgeType = "LINK",
            FromNodeId = fromNodeId,
            ToNodeId = toNodeId,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private sealed class TestGraphReadModel : IProjectionReadModel
    {
        private string _id = "";
        private long _stateVersion;

        public string Id
        {
            get
            {
                OnIdRead?.Invoke();
                return _id;
            }
            init => _id = value;
        }

        public string ActorId => Id;

        public long StateVersion
        {
            get
            {
                OnStateVersionRead?.Invoke();
                return _stateVersion;
            }
            init => _stateVersion = value;
        }

        public Action? OnIdRead { get; init; }

        public Action? OnStateVersionRead { get; init; }

        public string LastEventId { get; init; } = "";

        public DateTimeOffset UpdatedAt { get; init; }

        public string GraphScope { get; init; } = "";

        public IReadOnlyList<ProjectionGraphNode> GraphNodes { get; init; } = [];

        public IReadOnlyList<ProjectionGraphEdge> GraphEdges { get; init; } = [];
    }

    private sealed class TestGraphMaterializer(Action? onMaterialize = null)
        : IProjectionGraphMaterializer<TestGraphReadModel>
    {
        public ProjectionGraphMaterialization Materialize(TestGraphReadModel readModel)
        {
            onMaterialize?.Invoke();
            return new ProjectionGraphMaterialization
            {
                Scope = readModel.GraphScope,
                Nodes = readModel.GraphNodes,
                Edges = readModel.GraphEdges,
            };
        }
    }

    private sealed class CountingGraphMaterializer : IProjectionGraphMaterializer<TestGraphReadModel>
    {
        public int InvocationCount { get; private set; }

        public ProjectionGraphMaterialization Materialize(TestGraphReadModel readModel)
        {
            InvocationCount++;
            return new ProjectionGraphMaterialization
            {
                Scope = readModel.GraphScope,
                Nodes = readModel.GraphNodes,
                Edges = readModel.GraphEdges,
            };
        }
    }

    private sealed class FailingGraphMaterializer : IProjectionGraphMaterializer<TestGraphReadModel>
    {
        public ProjectionGraphMaterialization Materialize(TestGraphReadModel readModel) =>
            throw new InvalidOperationException("materializer failed");
    }

    private sealed class CancellingGraphMaterializer(CancellationTokenSource cts)
        : IProjectionGraphMaterializer<TestGraphReadModel>
    {
        public ProjectionGraphMaterialization Materialize(TestGraphReadModel readModel)
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }
    }

    private sealed class RecordingReadOnlyList<T>(IReadOnlyList<T> items, Action onCount)
        : IReadOnlyList<T>
    {
        public int Count
        {
            get
            {
                onCount();
                return items.Count;
            }
        }

        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator() => items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class UnrelatedCancellationGraphMaterializer : IProjectionGraphMaterializer<TestGraphReadModel>
    {
        public ProjectionGraphMaterialization Materialize(TestGraphReadModel readModel) =>
            throw new OperationCanceledException("materializer cancellation without caller cancellation");
    }
}
