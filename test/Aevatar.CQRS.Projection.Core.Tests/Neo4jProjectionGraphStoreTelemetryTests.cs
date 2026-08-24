using System.Diagnostics.Metrics;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.Stores;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class Neo4jProjectionGraphStoreTelemetryTests
{
    [Fact]
    public async Task ObserveWriteAsync_WhenWriteCompletes_EmitsOneLogAndLowCardinalityMetrics()
    {
        var logger = new RecordingLogger();
        var measurements = new List<MetricMeasurement>();
        using var listener = Listen(measurements);
        var context = Context();

        await Neo4jProjectionGraphStoreTelemetry.ObserveWriteAsync(
            logger,
            context,
            CancellationToken.None,
            () => Task.CompletedTask);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Exception.Should().BeNull();
        entry.Properties["Provider"].Should().Be("Neo4j");
        entry.Properties["Operation"].Should().Be(Neo4jProjectionGraphStoreTelemetry.ReplaceOwnerGraphOperation);
        entry.Properties["ProjectionKind"].Should().Be("workflow-execution-materialization");
        entry.Properties["StateVersion"].Should().Be(42L);
        entry.Properties["Scope"].Should().Be("workflow-run");
        entry.Properties["OwnerId"].Should().Be("run:alpha");
        entry.Properties["NodeCount"].Should().Be(7);
        entry.Properties["EdgeCount"].Should().Be(6);
        entry.Properties["Result"].Should().Be(Neo4jProjectionGraphStoreTelemetry.CompletedResult);
        Convert.ToDouble(entry.Properties["ElapsedMs"]).Should().BeGreaterThanOrEqualTo(0);

        measurements.Select(x => x.Instrument).Should().BeEquivalentTo(
            Neo4jProjectionGraphStoreTelemetry.DurationInstrumentName,
            Neo4jProjectionGraphStoreTelemetry.TotalInstrumentName);
        measurements.Should().OnlyContain(measurement =>
            measurement.Tags.Count == 3 &&
            Equals(measurement.Tags[Neo4jProjectionGraphStoreTelemetry.ProviderTag], "Neo4j") &&
            Equals(
                measurement.Tags[Neo4jProjectionGraphStoreTelemetry.OperationTag],
                Neo4jProjectionGraphStoreTelemetry.ReplaceOwnerGraphOperation) &&
            Equals(
                measurement.Tags[Neo4jProjectionGraphStoreTelemetry.ResultTag],
                Neo4jProjectionGraphStoreTelemetry.CompletedResult));
        measurements.SelectMany(x => x.Tags.Keys).Should().OnlyContain(key =>
            key == Neo4jProjectionGraphStoreTelemetry.ProviderTag ||
            key == Neo4jProjectionGraphStoreTelemetry.OperationTag ||
            key == Neo4jProjectionGraphStoreTelemetry.ResultTag);
    }

    [Fact]
    public async Task ObserveWriteAsync_WhenWriteFails_PreservesFailureAndEmitsOneFailedLog()
    {
        var logger = new RecordingLogger();
        var expected = new InvalidOperationException("driver failed");

        Func<Task> act = () => Neo4jProjectionGraphStoreTelemetry.ObserveWriteAsync(
            logger,
            Context(),
            CancellationToken.None,
            () => Task.FromException(expected));

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().BeSameAs(expected);
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeNull("driver exceptions may contain Cypher or connection details");
        entry.Properties["Result"].Should().Be(Neo4jProjectionGraphStoreTelemetry.FailedResult);
        entry.Properties["ErrorType"].Should().Be(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task ObserveWriteAsync_WhenCallerCancels_EmitsOneCancelledLog()
    {
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var expected = new OperationCanceledException(cts.Token);

        Func<Task> act = () => Neo4jProjectionGraphStoreTelemetry.ObserveWriteAsync(
            logger,
            Context(),
            cts.Token,
            () => Task.FromException(expected));

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.Should().BeSameAs(expected);
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Exception.Should().BeNull();
        entry.Properties["Result"].Should().Be(Neo4jProjectionGraphStoreTelemetry.CancelledResult);
        entry.Properties["ErrorType"].Should().Be(nameof(OperationCanceledException));
    }

    [Fact]
    public async Task ObserveWriteAsync_WhenNonCallerCancellationOccurs_ClassifiesItAsFailure()
    {
        var logger = new RecordingLogger();
        var expected = new OperationCanceledException("driver timeout");

        Func<Task> act = () => Neo4jProjectionGraphStoreTelemetry.ObserveWriteAsync(
            logger,
            Context(),
            CancellationToken.None,
            () => Task.FromException(expected));

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.Should().BeSameAs(expected);
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Properties["Result"].Should().Be(Neo4jProjectionGraphStoreTelemetry.FailedResult);
    }

    [Fact]
    public async Task ObserveWriteAsync_WhenResultResolverThrows_RecordsCompletedAndReturnsResult()
    {
        var logger = new RecordingLogger();

        var value = await Neo4jProjectionGraphStoreTelemetry.ObserveWriteAsync(
            logger,
            Context(),
            CancellationToken.None,
            () => Task.FromResult(7),
            _ => throw new InvalidOperationException("resolver failed"));

        value.Should().Be(7, "the write succeeded; a telemetry resolver failure must not fail it");
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Properties["Result"].Should().Be(Neo4jProjectionGraphStoreTelemetry.CompletedResult);
    }

    [Fact]
    public async Task ObserveWriteAsync_WhenTelemetryThrows_DoesNotFailSuccessfulWrite()
    {
        var counterCallbacks = 0;
        var histogramCallbacks = 0;
        var writeInvocations = 0;
        var logger = new ThrowingLogger();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == Neo4jProjectionGraphStoreTelemetry.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            Interlocked.Increment(ref counterCallbacks);
            throw new InvalidOperationException("counter failed");
        });
        listener.SetMeasurementEventCallback<double>((_, _, _, _) =>
        {
            Interlocked.Increment(ref histogramCallbacks);
            throw new InvalidOperationException("histogram failed");
        });
        listener.Start();

        Func<Task> act = () => Neo4jProjectionGraphStoreTelemetry.ObserveWriteAsync(
            logger,
            Context(),
            CancellationToken.None,
            () =>
            {
                Interlocked.Increment(ref writeInvocations);
                return Task.CompletedTask;
            });

        await act.Should().NotThrowAsync();
        writeInvocations.Should().Be(1);
        histogramCallbacks.Should().Be(1);
        counterCallbacks.Should().Be(1);
        logger.Exceptions.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task ObserveWriteAsync_WhenWriteAndTelemetryThrow_PreservesOriginalWriteFailure()
    {
        var counterCallbacks = 0;
        var histogramCallbacks = 0;
        var logger = new ThrowingLogger();
        var expected = new InvalidOperationException("provider query with sensitive details");
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == Neo4jProjectionGraphStoreTelemetry.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            Interlocked.Increment(ref counterCallbacks);
            throw new InvalidOperationException("counter failed");
        });
        listener.SetMeasurementEventCallback<double>((_, _, _, _) =>
        {
            Interlocked.Increment(ref histogramCallbacks);
            throw new InvalidOperationException("histogram failed");
        });
        listener.Start();

        Func<Task> act = () => Neo4jProjectionGraphStoreTelemetry.ObserveWriteAsync(
            logger,
            Context(),
            CancellationToken.None,
            () => Task.FromException(expected));

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().BeSameAs(expected);
        histogramCallbacks.Should().Be(1);
        counterCallbacks.Should().Be(1);
        logger.Exceptions.Should().ContainSingle().Which.Should().BeNull(
            "provider exceptions must not be attached to telemetry logs");
    }

    [Fact]
    public async Task ReplaceOwnerGraphAsync_WhenProjectionKindIsBlank_RejectsProvenanceAndLogsFailure()
    {
        var logger = new RecordingLogger();
        await using var store = new Neo4jProjectionGraphStore(
            new Neo4jProjectionGraphStoreOptions
            {
                AutoCreateSchema = false,
            },
            logger);
        var graph = new ProjectionOwnedGraph
        {
            ProjectionKind = " ",
            StateVersion = 43,
            Scope = "workflow-run",
            OwnerId = "run:alpha",
        };

        Func<Task> act = () => store.ReplaceOwnerGraphAsync(graph);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*projectionKind*");
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeNull();
        entry.Properties["ProjectionKind"].Should().BeNull();
        entry.Properties["StateVersion"].Should().Be(43L);
        entry.Properties["Result"].Should().Be(Neo4jProjectionGraphStoreTelemetry.FailedResult);
    }

    [Fact]
    public void ContextFactories_KeepDirectCrudCorrelationNullableAndNormalizeIdentifiers()
    {
        var edge = new ProjectionGraphEdge
        {
            Scope = " scope-alpha ",
            EdgeId = " edge-alpha ",
            FromNodeId = " from-alpha ",
            ToNodeId = " to-alpha ",
            EdgeType = "LINK",
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey] = " owner-alpha ",
            },
        };

        var context = Neo4jProjectionGraphWriteTelemetryContext.ForUpsertEdge(edge);

        context.ProjectionKind.Should().BeNull();
        context.StateVersion.Should().BeNull();
        context.Scope.Should().Be("scope-alpha");
        context.OwnerId.Should().Be("owner-alpha");
        context.EdgeId.Should().Be("edge-alpha");
        context.FromNodeId.Should().Be("from-alpha");
        context.ToNodeId.Should().Be("to-alpha");
        context.NodeCount.Should().BeNull();
        context.EdgeCount.Should().Be(1);
    }

    [Fact]
    public void ContextFactories_ReportOnlyCountsKnownBeforeTheWrite()
    {
        var graphContext = Neo4jProjectionGraphWriteTelemetryContext.ForReplaceOwnerGraph(
            new ProjectionOwnedGraph
            {
                ProjectionKind = "projection-alpha",
                StateVersion = 42,
                Nodes = [new ProjectionGraphNode(), new ProjectionGraphNode()],
                Edges = [new ProjectionGraphEdge()],
            });
        var nodeContext = Neo4jProjectionGraphWriteTelemetryContext.ForUpsertNode(
            new ProjectionGraphNode());
        var edgeContext = Neo4jProjectionGraphWriteTelemetryContext.ForUpsertEdge(
            new ProjectionGraphEdge());

        graphContext.NodeCount.Should().Be(2);
        graphContext.EdgeCount.Should().Be(1);
        nodeContext.NodeCount.Should().Be(1);
        nodeContext.EdgeCount.Should().BeNull();
        edgeContext.NodeCount.Should().BeNull();
        edgeContext.EdgeCount.Should().Be(1);
    }

    [Fact]
    public void ContextFactories_WhenPayloadIsMissing_LeaveBothCountsUnknown()
    {
        var graphContext = Neo4jProjectionGraphWriteTelemetryContext.ForReplaceOwnerGraph(null);
        var nodeContext = Neo4jProjectionGraphWriteTelemetryContext.ForUpsertNode(null);
        var edgeContext = Neo4jProjectionGraphWriteTelemetryContext.ForUpsertEdge(null);

        graphContext.NodeCount.Should().BeNull();
        graphContext.EdgeCount.Should().BeNull();
        nodeContext.NodeCount.Should().BeNull();
        nodeContext.EdgeCount.Should().BeNull();
        edgeContext.NodeCount.Should().BeNull();
        edgeContext.EdgeCount.Should().BeNull();
    }

    [Theory]
    [InlineData("", "node-alpha")]
    [InlineData("scope-alpha", "")]
    [InlineData("scope-alpha", "entity-alpha")]
    public void DeleteContext_AlwaysLeavesAffectedCountsUnknown(
        string scope,
        string identifier)
    {
        var nodeContext = Neo4jProjectionGraphWriteTelemetryContext.ForDeleteNode(scope, identifier);
        var edgeContext = Neo4jProjectionGraphWriteTelemetryContext.ForDeleteEdge(scope, identifier);

        nodeContext.NodeCount.Should().BeNull();
        nodeContext.EdgeCount.Should().BeNull();
        edgeContext.NodeCount.Should().BeNull();
        edgeContext.EdgeCount.Should().BeNull();
    }

    private static Neo4jProjectionGraphWriteTelemetryContext Context() =>
        new(
            Neo4jProjectionGraphStoreTelemetry.ReplaceOwnerGraphOperation,
            "workflow-execution-materialization",
            42,
            "workflow-run",
            "run:alpha",
            null,
            null,
            null,
            null,
            7,
            6);

    private static MeterListener Listen(List<MetricMeasurement> measurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == Neo4jProjectionGraphStoreTelemetry.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new MetricMeasurement(instrument.Name, value, CopyTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new MetricMeasurement(instrument.Name, value, CopyTags(tags))));
        listener.Start();
        return listener;
    }

    private static IReadOnlyDictionary<string, object?> CopyTags(
        ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        tags.ToArray().ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    private sealed record MetricMeasurement(
        string Instrument,
        double Value,
        IReadOnlyDictionary<string, object?> Tags);

    private sealed record LogEntry(
        LogLevel Level,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class RecordingLogger : ILogger<Neo4jProjectionGraphStore>
    {
        public List<LogEntry> Entries { get; } = [];

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
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            Entries.Add(new LogEntry(logLevel, exception, properties));
        }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public List<Exception?> Exceptions { get; } = [];

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
            Exceptions.Add(exception);
            throw new InvalidOperationException("logger failed");
        }
    }
}
