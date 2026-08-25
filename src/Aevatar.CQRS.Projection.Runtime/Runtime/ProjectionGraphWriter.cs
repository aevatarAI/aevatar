using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphWriter<TReadModel>
    : IProjectionGraphWriter<TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    private const string ConstructionOperation = "construct_owner_graph";
    private const string CompletedResult = "completed";
    private const string FailedResult = "failed";
    private const string CancelledResult = "cancelled";

    private readonly IProjectionGraphStore _graphStore;
    private readonly IProjectionGraphMaterializer<TReadModel> _materializer;
    private readonly IProjectionGraphOwnerIdentityResolver _ownerIdentityResolver;
    private readonly ProjectionGraphProviderStatus? _providerStatus;
    private readonly ILogger<ProjectionGraphWriter<TReadModel>> _logger;
    private readonly Func<long> _getTimestamp;
    private readonly Func<long, TimeSpan> _getElapsedTime;

    public ProjectionGraphWriter(
        IProjectionGraphStore graphStore,
        IProjectionGraphMaterializer<TReadModel> materializer,
        ILogger<ProjectionGraphWriter<TReadModel>>? logger = null,
        IProjectionGraphOwnerIdentityResolver? ownerIdentityResolver = null,
        ProjectionGraphProviderStatus? providerStatus = null)
        : this(
            graphStore,
            materializer,
            logger,
            Stopwatch.GetTimestamp,
            Stopwatch.GetElapsedTime,
            ownerIdentityResolver,
            providerStatus)
    {
    }

    internal ProjectionGraphWriter(
        IProjectionGraphStore graphStore,
        IProjectionGraphMaterializer<TReadModel> materializer,
        ILogger<ProjectionGraphWriter<TReadModel>>? logger,
        Func<long> getTimestamp,
        Func<long, TimeSpan> getElapsedTime)
        : this(
            graphStore,
            materializer,
            logger,
            getTimestamp,
            getElapsedTime,
            ownerIdentityResolver: null,
            providerStatus: null)
    {
    }

    private ProjectionGraphWriter(
        IProjectionGraphStore graphStore,
        IProjectionGraphMaterializer<TReadModel> materializer,
        ILogger<ProjectionGraphWriter<TReadModel>>? logger,
        Func<long> getTimestamp,
        Func<long, TimeSpan> getElapsedTime,
        IProjectionGraphOwnerIdentityResolver? ownerIdentityResolver,
        ProjectionGraphProviderStatus? providerStatus)
    {
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        _ownerIdentityResolver = ownerIdentityResolver ?? ProjectionGraphOwnerIdentityResolver.Instance;
        _providerStatus = providerStatus;
        _logger = logger ?? NullLogger<ProjectionGraphWriter<TReadModel>>.Instance;
        _getTimestamp = getTimestamp ?? throw new ArgumentNullException(nameof(getTimestamp));
        _getElapsedTime = getElapsedTime ?? throw new ArgumentNullException(nameof(getElapsedTime));
    }

    public Task UpsertAsync(
        TReadModel readModel,
        string projectionKind,
        CancellationToken ct = default)
    {
        var normalizedProjectionKind = NormalizeToken(projectionKind);
        ArgumentNullException.ThrowIfNull(readModel);
        var stateVersion = readModel.StateVersion;
        if (normalizedProjectionKind.Length == 0)
        {
            throw new InvalidOperationException(
                $"Projection kind is required for graph read model '{typeof(TReadModel).FullName}'.");
        }

        ct.ThrowIfCancellationRequested();
        if (_providerStatus is { Enabled: false })
            return Task.CompletedTask;

        var elapsed = TimeSpan.Zero;
        var scope = string.Empty;
        var ownerId = string.Empty;
        IReadOnlyList<ProjectionGraphNode> nodes = [];
        IReadOnlyList<ProjectionGraphEdge> edges = [];
        int? nodeCount = null;
        int? edgeCount = null;
        ProjectionOwnedGraph graph;
        ProjectionGraphMaterialization materialized;

        var startedAt = _getTimestamp();
        try
        {
            try
            {
                materialized = _materializer.Materialize(readModel);
            }
            finally
            {
                elapsed = _getElapsedTime(startedAt);
            }

            scope = NormalizeToken(materialized.Scope);
            if (scope.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Graph scope is required for read model '{typeof(TReadModel).FullName}'.");
            }

            ownerId = _ownerIdentityResolver.Resolve(readModel.GetType(), readModel.Id).Value;
            nodes = NormalizeNodes(materialized.Nodes, scope, ownerId);
            edges = NormalizeEdges(materialized.Edges, scope, ownerId);
            nodeCount = nodes.Count;
            edgeCount = edges.Count;
            graph = new ProjectionOwnedGraph
            {
                ProjectionKind = normalizedProjectionKind,
                StateVersion = stateVersion,
                Scope = scope,
                OwnerId = ownerId,
                Nodes = nodes,
                Edges = edges,
            };

            LogConstruction(
                elapsed,
                normalizedProjectionKind,
                stateVersion,
                scope,
                ownerId,
                nodeCount,
                edgeCount,
                CompletedResult,
                errorType: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogConstruction(
                elapsed,
                normalizedProjectionKind,
                stateVersion,
                scope,
                ownerId,
                nodeCount,
                edgeCount,
                CancelledResult,
                errorType: null);
            throw;
        }
        catch (Exception ex)
        {
            LogConstruction(
                elapsed,
                normalizedProjectionKind,
                stateVersion,
                scope,
                ownerId,
                nodeCount,
                edgeCount,
                FailedResult,
                ex.GetType().Name);
            throw;
        }

        return _graphStore.ReplaceOwnerGraphAsync(graph, ct);
    }

    private void LogConstruction(
        TimeSpan elapsed,
        string projectionKind,
        long? stateVersion,
        string scope,
        string ownerId,
        int? nodeCount,
        int? edgeCount,
        string result,
        string? errorType)
    {
        try
        {
            _logger.Log(
                string.Equals(result, FailedResult, StringComparison.Ordinal)
                    ? LogLevel.Error
                    : LogLevel.Information,
                "Projection graph construction finished: operation={Operation} result={Result} " +
                "elapsedMs={ElapsedMs} projectionKind={ProjectionKind} stateVersion={StateVersion} " +
                "scope={Scope} ownerId={OwnerId} nodeCount={NodeCount} edgeCount={EdgeCount} " +
                "errorType={ErrorType}",
                ConstructionOperation,
                result,
                Math.Max(0, elapsed.TotalMilliseconds),
                projectionKind,
                stateVersion,
                scope,
                ownerId,
                nodeCount,
                edgeCount,
                errorType);
        }
        catch (Exception ex)
        {
            TraceLoggingFailure(ex);
        }
    }

    private static void TraceLoggingFailure(Exception exception)
    {
        try
        {
            Trace.TraceWarning(
                "Projection graph construction log emission failed: {0}",
                exception.GetType().FullName);
        }
        catch (Exception)
        {
            return;
        }
    }

    private static IReadOnlyList<ProjectionGraphNode> NormalizeNodes(
        IReadOnlyList<ProjectionGraphNode> graphNodes,
        string scope,
        string ownerId)
    {
        if (graphNodes.Count == 0)
            return [];

        var nodesById = new Dictionary<string, ProjectionGraphNode>(StringComparer.Ordinal);
        foreach (var sourceNode in graphNodes)
        {
            var nodeId = NormalizeToken(sourceNode.NodeId);
            if (nodeId.Length == 0)
                continue;

            var nodeType = NormalizeToken(sourceNode.NodeType);
            if (nodeType.Length == 0)
                nodeType = "Unknown";

            var properties = new Dictionary<string, string>(sourceNode.Properties, StringComparer.Ordinal)
            {
                [ProjectionGraphManagedPropertyKeys.ManagedMarkerKey] = ProjectionGraphManagedPropertyKeys.ManagedMarkerValue,
                [ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey] = ownerId,
            };

            nodesById[nodeId] = new ProjectionGraphNode
            {
                Scope = scope,
                NodeId = nodeId,
                NodeType = nodeType,
                Properties = properties,
                UpdatedAt = sourceNode.UpdatedAt == default ? DateTimeOffset.UtcNow : sourceNode.UpdatedAt,
            };
        }

        return nodesById.Values.ToList();
    }

    private static IReadOnlyList<ProjectionGraphEdge> NormalizeEdges(
        IReadOnlyList<ProjectionGraphEdge> graphEdges,
        string scope,
        string ownerId)
    {
        if (graphEdges.Count == 0)
            return [];

        var edgesById = new Dictionary<string, ProjectionGraphEdge>(StringComparer.Ordinal);
        foreach (var sourceEdge in graphEdges)
        {
            var edgeId = NormalizeToken(sourceEdge.EdgeId);
            var edgeType = NormalizeToken(sourceEdge.EdgeType);
            var fromNodeId = NormalizeToken(sourceEdge.FromNodeId);
            var toNodeId = NormalizeToken(sourceEdge.ToNodeId);
            if (edgeId.Length == 0 ||
                edgeType.Length == 0 ||
                fromNodeId.Length == 0 ||
                toNodeId.Length == 0)
            {
                continue;
            }

            var properties = new Dictionary<string, string>(sourceEdge.Properties, StringComparer.Ordinal)
            {
                [ProjectionGraphManagedPropertyKeys.ManagedMarkerKey] = ProjectionGraphManagedPropertyKeys.ManagedMarkerValue,
                [ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey] = ownerId,
            };

            edgesById[edgeId] = new ProjectionGraphEdge
            {
                Scope = scope,
                EdgeId = edgeId,
                EdgeType = edgeType,
                FromNodeId = fromNodeId,
                ToNodeId = toNodeId,
                Properties = properties,
                UpdatedAt = sourceEdge.UpdatedAt == default ? DateTimeOffset.UtcNow : sourceEdge.UpdatedAt,
            };
        }

        return edgesById.Values.ToList();
    }

    private static string NormalizeToken(string? token) => token?.Trim() ?? string.Empty;
}
