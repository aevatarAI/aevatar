using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.Stores;

internal sealed record Neo4jProjectionGraphWriteTelemetryContext(
    string Operation,
    string? ProjectionKind,
    long? StateVersion,
    string? Scope,
    string? OwnerId,
    string? NodeId,
    string? EdgeId,
    string? FromNodeId,
    string? ToNodeId,
    int? NodeCount,
    int? EdgeCount)
{
    internal static Neo4jProjectionGraphWriteTelemetryContext ForReplaceOwnerGraph(ProjectionOwnedGraph? graph) =>
        new(
            Neo4jProjectionGraphStoreTelemetry.ReplaceOwnerGraphOperation,
            NormalizeOptional(graph?.ProjectionKind),
            graph?.StateVersion,
            NormalizeOptional(graph?.Scope),
            NormalizeOptional(graph?.OwnerId),
            null,
            null,
            null,
            null,
            graph?.Nodes?.Count,
            graph?.Edges?.Count);

    internal static Neo4jProjectionGraphWriteTelemetryContext ForApplyDelta(ProjectionGraphDelta? delta) =>
        new(
            Neo4jProjectionGraphStoreTelemetry.ApplyDeltaOperation,
            NormalizeOptional(delta?.Route?.ProjectionKind),
            delta?.Source?.StateVersion,
            NormalizeOptional(delta?.Route?.PhysicalNamespace),
            NormalizeOptional(delta?.Route?.OwnerId),
            null,
            null,
            null,
            null,
            delta == null ? null : delta.UpsertNodes.Count + delta.DeleteNodeIds.Count,
            delta == null
                ? null
                : delta.UpsertEdges.Count + delta.UpsertPendingEdges.Count +
                  delta.DeleteEdgeIds.Count + delta.DeletePendingEdgeIds.Count);

    internal static Neo4jProjectionGraphWriteTelemetryContext ForUpsertNode(ProjectionGraphNode? node) =>
        new(
            Neo4jProjectionGraphStoreTelemetry.UpsertNodeOperation,
            null,
            null,
            NormalizeOptional(node?.Scope),
            ResolveOwnerId(node?.Properties),
            NormalizeOptional(node?.NodeId),
            null,
            null,
            null,
            node == null ? null : 1,
            null);

    internal static Neo4jProjectionGraphWriteTelemetryContext ForUpsertEdge(ProjectionGraphEdge? edge) =>
        new(
            Neo4jProjectionGraphStoreTelemetry.UpsertEdgeOperation,
            null,
            null,
            NormalizeOptional(edge?.Scope),
            ResolveOwnerId(edge?.Properties),
            null,
            NormalizeOptional(edge?.EdgeId),
            NormalizeOptional(edge?.FromNodeId),
            NormalizeOptional(edge?.ToNodeId),
            null,
            edge == null ? null : 1);

    internal static Neo4jProjectionGraphWriteTelemetryContext ForDeleteNode(string? scope, string? nodeId)
    {
        var normalizedScope = NormalizeOptional(scope);
        var normalizedNodeId = NormalizeOptional(nodeId);
        return new Neo4jProjectionGraphWriteTelemetryContext(
            Neo4jProjectionGraphStoreTelemetry.DeleteNodeOperation,
            null,
            null,
            normalizedScope,
            null,
            normalizedNodeId,
            null,
            null,
            null,
            null,
            null);
    }

    internal static Neo4jProjectionGraphWriteTelemetryContext ForDeleteEdge(string? scope, string? edgeId)
    {
        var normalizedScope = NormalizeOptional(scope);
        var normalizedEdgeId = NormalizeOptional(edgeId);
        return new Neo4jProjectionGraphWriteTelemetryContext(
            Neo4jProjectionGraphStoreTelemetry.DeleteEdgeOperation,
            null,
            null,
            normalizedScope,
            null,
            null,
            normalizedEdgeId,
            null,
            null,
            null,
            null);
    }

    private static string? ResolveOwnerId(IReadOnlyDictionary<string, string>? properties)
    {
        if (properties == null ||
            !properties.TryGetValue(ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey, out var ownerId))
        {
            return null;
        }

        return NormalizeOptional(ownerId);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class Neo4jProjectionGraphStoreTelemetry
{
    internal const string MeterName = "Aevatar.CQRS.Projection.Providers.Neo4j";
    internal const string DurationInstrumentName = "aevatar.projection.neo4j.write.duration";
    internal const string TotalInstrumentName = "aevatar.projection.neo4j.write.total";
    internal const string ProviderTag = "provider";
    internal const string OperationTag = "operation";
    internal const string ResultTag = "result";

    internal const string ReplaceOwnerGraphOperation = "replace_owner_graph";
    internal const string UpsertNodeOperation = "upsert_node";
    internal const string UpsertEdgeOperation = "upsert_edge";
    internal const string DeleteNodeOperation = "delete_node";
    internal const string DeleteEdgeOperation = "delete_edge";
    internal const string ApplyDeltaOperation = "apply_delta";

    internal const string CompletedResult = "completed";
    internal const string FailedResult = "failed";
    internal const string CancelledResult = "cancelled";

    private const string ProviderName = "Neo4j";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Histogram<double> WriteDuration = Meter.CreateHistogram<double>(
        DurationInstrumentName,
        unit: "ms",
        description: "Elapsed time for Neo4j projection graph-store write operations.");
    private static readonly Counter<long> WriteTotal = Meter.CreateCounter<long>(
        TotalInstrumentName,
        description: "Neo4j projection graph-store write operation terminal results.");

    internal static async Task<TResult> ObserveWriteAsync<TResult>(
        ILogger logger,
        Neo4jProjectionGraphWriteTelemetryContext context,
        CancellationToken callerCancellationToken,
        Func<Task<TResult>> write,
        Func<TResult, string> resolveResult)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(resolveResult);

        var startedAtTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var result = await write();
            // The write succeeded; a throwing result resolver is a telemetry concern and
            // must never turn this committed write into an observed (or propagated) failure.
            RecordTerminal(logger, context, startedAtTimestamp, ResolveResultSafe(resolveResult, result), null);
            return result;
        }
        catch (OperationCanceledException ex) when (callerCancellationToken.IsCancellationRequested)
        {
            RecordTerminal(logger, context, startedAtTimestamp, CancelledResult, ex);
            throw;
        }
        catch (Exception ex)
        {
            RecordTerminal(logger, context, startedAtTimestamp, FailedResult, ex);
            throw;
        }
    }

    internal static async Task ObserveWriteAsync(
        ILogger logger,
        Neo4jProjectionGraphWriteTelemetryContext context,
        CancellationToken callerCancellationToken,
        Func<Task> write)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(write);

        var startedAtTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await write();
            RecordTerminal(logger, context, startedAtTimestamp, CompletedResult, null);
        }
        catch (OperationCanceledException ex) when (callerCancellationToken.IsCancellationRequested)
        {
            RecordTerminal(logger, context, startedAtTimestamp, CancelledResult, ex);
            throw;
        }
        catch (Exception ex)
        {
            RecordTerminal(logger, context, startedAtTimestamp, FailedResult, ex);
            throw;
        }
    }

    private static void RecordTerminal(
        ILogger logger,
        Neo4jProjectionGraphWriteTelemetryContext context,
        long startedAtTimestamp,
        string result,
        Exception? exception)
    {
        var elapsedMs = Math.Max(0, Stopwatch.GetElapsedTime(startedAtTimestamp).TotalMilliseconds);
        var tags = new TagList
        {
            { ProviderTag, ProviderName },
            { OperationTag, context.Operation },
            { ResultTag, result },
        };

        SafeObserve(() => WriteDuration.Record(elapsedMs, tags));
        SafeObserve(() => WriteTotal.Add(1, tags));
        SafeObserve(() => LogTerminal(logger, context, elapsedMs, result, exception));
    }

    private static void LogTerminal(
        ILogger logger,
        Neo4jProjectionGraphWriteTelemetryContext context,
        double elapsedMs,
        string result,
        Exception? exception)
    {
        const string message =
            "Neo4j projection graph-store write reached a terminal result. provider={Provider} operation={Operation} projectionKind={ProjectionKind} stateVersion={StateVersion} scope={Scope} ownerId={OwnerId} nodeId={NodeId} edgeId={EdgeId} fromNodeId={FromNodeId} toNodeId={ToNodeId} nodeCount={NodeCount} edgeCount={EdgeCount} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}";

        var arguments = new object?[]
        {
            ProviderName,
            context.Operation,
            context.ProjectionKind,
            context.StateVersion,
            context.Scope,
            context.OwnerId,
            context.NodeId,
            context.EdgeId,
            context.FromNodeId,
            context.ToNodeId,
            context.NodeCount,
            context.EdgeCount,
            elapsedMs,
            result,
            exception?.GetType().Name,
        };

        if (exception == null || string.Equals(result, CancelledResult, StringComparison.Ordinal))
        {
            logger.LogInformation(message, arguments);
            return;
        }

        logger.LogError(message, arguments);
    }

    private static string ResolveResultSafe<TResult>(Func<TResult, string> resolveResult, TResult result)
    {
        try
        {
            return resolveResult(result);
        }
        catch (Exception ex)
        {
            TraceObservationFailure(ex);
            return CompletedResult;
        }
    }

    private static void SafeObserve(Action observation)
    {
        try
        {
            observation();
        }
        catch (Exception ex)
        {
            TraceObservationFailure(ex);
        }
    }

    private static void TraceObservationFailure(Exception exception)
    {
        try
        {
            Trace.TraceWarning(
                "Neo4j projection graph-store telemetry emission failed. errorType={0}",
                exception.GetType().Name);
        }
        catch (Exception)
        {
            return;
        }
    }
}
