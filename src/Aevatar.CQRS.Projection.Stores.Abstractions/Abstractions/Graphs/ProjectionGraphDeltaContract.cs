using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public static class ProjectionGraphDeltaContract
{
    /// <summary>
    /// Bounded replacement limit for a repair/cutover delta: the store counts the effective
    /// mutations (upserts + explicit deletes + the stale-element deletes it generates itself)
    /// inside the apply transaction and rejects the delta atomically when the total exceeds
    /// this. Producers use the same constant to bound the desired graph they build.
    /// </summary>
    public const int MaximumRepairOrCutoverMutationCount = 20_000;

    private static readonly long MaximumEpochMilliseconds =
        DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>
    /// Total mutation count of a delta as the store applies it (after stale deletes were added).
    /// </summary>
    public static int CountMutations(ProjectionGraphDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        return delta.UpsertNodes.Count +
               delta.DeleteNodeIds.Count +
               delta.UpsertEdges.Count +
               delta.DeleteEdgeIds.Count +
               delta.UpsertPendingEdges.Count +
               delta.DeletePendingEdgeIds.Count;
    }

    public static bool TryNormalize(
        ProjectionGraphDelta? delta,
        out ProjectionGraphDelta normalized,
        out string deltaFingerprint,
        out string detail)
    {
        normalized = new ProjectionGraphDelta();
        deltaFingerprint = string.Empty;
        detail = string.Empty;
        if (delta == null)
        {
            detail = "Graph delta is required.";
            return false;
        }

        if (!TryNormalizeRoute(delta.Route, out var route, out detail) ||
            !TryNormalizeSource(delta.Source, out var source, out detail))
        {
            return false;
        }

        if (delta.Mode is not ProjectionGraphDeltaMode.Normal and
            not ProjectionGraphDeltaMode.RepairOrCutover)
        {
            detail = "Graph delta mode must be normal or repair/cutover.";
            return false;
        }

        normalized.Route = route;
        normalized.Source = source;
        normalized.Mode = delta.Mode;

        if (!TryNormalizeNodes(delta.UpsertNodes, normalized.UpsertNodes, out detail) ||
            !TryNormalizeIdentifiers(delta.DeleteNodeIds, normalized.DeleteNodeIds, "deleted node", out detail) ||
            !TryNormalizeEdges(delta.UpsertEdges, normalized.UpsertEdges, "upsert edge", out detail) ||
            !TryNormalizeIdentifiers(delta.DeleteEdgeIds, normalized.DeleteEdgeIds, "deleted edge", out detail) ||
            !TryNormalizeEdges(
                delta.UpsertPendingEdges,
                normalized.UpsertPendingEdges,
                "pending edge",
                out detail) ||
            !TryNormalizeIdentifiers(
                delta.DeletePendingEdgeIds,
                normalized.DeletePendingEdgeIds,
                "deleted pending edge",
                out detail))
        {
            return false;
        }

        var nodeUpserts = normalized.UpsertNodes.Select(x => x.NodeId).ToHashSet(StringComparer.Ordinal);
        if (nodeUpserts.Overlaps(normalized.DeleteNodeIds))
        {
            detail = "A graph delta cannot upsert and delete the same node.";
            return false;
        }

        var edgeUpserts = normalized.UpsertEdges.Select(x => x.EdgeId).ToHashSet(StringComparer.Ordinal);
        var pendingUpserts = normalized.UpsertPendingEdges.Select(x => x.EdgeId).ToHashSet(StringComparer.Ordinal);
        var edgeDeletes = normalized.DeleteEdgeIds.ToHashSet(StringComparer.Ordinal);
        var pendingDeletes = normalized.DeletePendingEdgeIds.ToHashSet(StringComparer.Ordinal);
        if (edgeUpserts.Overlaps(pendingUpserts) ||
            edgeUpserts.Overlaps(edgeDeletes) ||
            edgeUpserts.Overlaps(pendingDeletes) ||
            pendingUpserts.Overlaps(edgeDeletes) ||
            pendingUpserts.Overlaps(pendingDeletes) ||
            edgeDeletes.Overlaps(pendingDeletes))
        {
            detail = "A graph delta cannot apply multiple mutations to the same edge identity.";
            return false;
        }

        deltaFingerprint = ComputeFingerprint(normalized);
        return true;
    }

    public static bool TryNormalizeRoute(
        ProjectionGraphRouteFingerprint? route,
        out ProjectionGraphRouteFingerprint normalized,
        out string detail)
    {
        normalized = new ProjectionGraphRouteFingerprint();
        detail = string.Empty;
        if (route == null)
        {
            detail = "Graph route fingerprint is required.";
            return false;
        }

        normalized.ProjectionKind = NormalizeToken(route.ProjectionKind);
        normalized.LogicalScope = NormalizeToken(route.LogicalScope);
        normalized.OwnerId = NormalizeToken(route.OwnerId);
        normalized.PhysicalNamespace = NormalizeToken(route.PhysicalNamespace);
        normalized.RouteEpoch = route.RouteEpoch;
        normalized.ContractId = NormalizeToken(route.ContractId);
        normalized.ContractVersion = route.ContractVersion;
        if (normalized.ProjectionKind.Length == 0 ||
            normalized.LogicalScope.Length == 0 ||
            normalized.OwnerId.Length == 0 ||
            normalized.PhysicalNamespace.Length == 0 ||
            normalized.RouteEpoch <= 0 ||
            normalized.ContractId.Length == 0 ||
            normalized.ContractVersion <= 0)
        {
            detail = "Graph route requires projection kind, logical scope, owner id, physical namespace, " +
                     "positive route epoch, contract id and positive contract version.";
            return false;
        }

        return true;
    }

    public static bool RouteEquals(
        ProjectionGraphRouteFingerprint left,
        ProjectionGraphRouteFingerprint right)
    {
        return string.Equals(left.ProjectionKind, right.ProjectionKind, StringComparison.Ordinal) &&
               string.Equals(left.LogicalScope, right.LogicalScope, StringComparison.Ordinal) &&
               string.Equals(left.OwnerId, right.OwnerId, StringComparison.Ordinal) &&
               string.Equals(left.PhysicalNamespace, right.PhysicalNamespace, StringComparison.Ordinal) &&
               left.RouteEpoch == right.RouteEpoch &&
               string.Equals(left.ContractId, right.ContractId, StringComparison.Ordinal) &&
               left.ContractVersion == right.ContractVersion;
    }

    public static ProjectionGraphNodeMutation CloneNode(ProjectionGraphNodeMutation source) =>
        source.Clone();

    public static ProjectionGraphEdgeMutation CloneEdge(ProjectionGraphEdgeMutation source) =>
        source.Clone();

    private static bool TryNormalizeSource(
        ProjectionGraphSourceCoordinate? source,
        out ProjectionGraphSourceCoordinate normalized,
        out string detail)
    {
        normalized = new ProjectionGraphSourceCoordinate();
        detail = string.Empty;
        if (source == null)
        {
            detail = "Graph source coordinate is required.";
            return false;
        }

        normalized.ActorId = NormalizeToken(source.ActorId);
        normalized.StateVersion = source.StateVersion;
        normalized.EventId = NormalizeToken(source.EventId);
        if (normalized.ActorId.Length == 0 ||
            normalized.StateVersion <= 0 ||
            normalized.EventId.Length == 0)
        {
            detail = "Graph source coordinate requires actor id, positive state version and event id.";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeNodes(
        IEnumerable<ProjectionGraphNodeMutation> source,
        ICollection<ProjectionGraphNodeMutation> target,
        out string detail)
    {
        detail = string.Empty;
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            if (item == null)
            {
                detail = "Graph node mutation cannot be null.";
                return false;
            }

            var nodeId = NormalizeToken(item.NodeId);
            if (nodeId.Length == 0 || !identifiers.Add(nodeId))
            {
                detail = "Graph node mutations require unique non-empty node ids.";
                return false;
            }

            var normalized = new ProjectionGraphNodeMutation
            {
                NodeId = nodeId,
                NodeType = NormalizeToken(item.NodeType),
                UpdatedAtEpochMs = NormalizeEpochMilliseconds(item.UpdatedAtEpochMs),
            };
            if (normalized.NodeType.Length == 0)
                normalized.NodeType = "Unknown";
            if (!TryAddNormalizedProperties(item.Properties, normalized.Properties))
            {
                detail = $"Graph node '{nodeId}' has blank or colliding normalized property keys.";
                return false;
            }
            target.Add(normalized);
        }

        return true;
    }

    private static bool TryNormalizeEdges(
        IEnumerable<ProjectionGraphEdgeMutation> source,
        ICollection<ProjectionGraphEdgeMutation> target,
        string description,
        out string detail)
    {
        detail = string.Empty;
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            if (item == null)
            {
                detail = $"Graph {description} mutation cannot be null.";
                return false;
            }

            var edgeId = NormalizeToken(item.EdgeId);
            var fromNodeId = NormalizeToken(item.FromNodeId);
            var toNodeId = NormalizeToken(item.ToNodeId);
            var edgeType = NormalizeToken(item.EdgeType);
            if (edgeId.Length == 0 ||
                fromNodeId.Length == 0 ||
                toNodeId.Length == 0 ||
                edgeType.Length == 0 ||
                !identifiers.Add(edgeId))
            {
                detail = $"Graph {description} mutations require unique edge ids and non-empty endpoints/type.";
                return false;
            }

            var normalized = new ProjectionGraphEdgeMutation
            {
                EdgeId = edgeId,
                FromNodeId = fromNodeId,
                ToNodeId = toNodeId,
                EdgeType = edgeType,
                UpdatedAtEpochMs = NormalizeEpochMilliseconds(item.UpdatedAtEpochMs),
            };
            if (!TryAddNormalizedProperties(item.Properties, normalized.Properties))
            {
                detail = $"Graph edge '{edgeId}' has blank or colliding normalized property keys.";
                return false;
            }
            target.Add(normalized);
        }

        return true;
    }

    private static bool TryNormalizeIdentifiers(
        IEnumerable<string> source,
        ICollection<string> target,
        string description,
        out string detail)
    {
        detail = string.Empty;
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            var identifier = NormalizeToken(item);
            if (identifier.Length == 0 || !identifiers.Add(identifier))
            {
                detail = $"Graph {description} mutations require unique non-empty ids.";
                return false;
            }

            target.Add(identifier);
        }

        return true;
    }

    private static bool TryAddNormalizedProperties(
        IEnumerable<KeyValuePair<string, string>> source,
        IDictionary<string, string> target)
    {
        foreach (var pair in source
                     .Select(x => new KeyValuePair<string, string>(NormalizeToken(x.Key), x.Value ?? string.Empty))
                     .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (pair.Key.Length == 0 || target.ContainsKey(pair.Key))
                return false;
            target[pair.Key] = pair.Value;
        }

        return true;
    }

    private static string ComputeFingerprint(ProjectionGraphDelta delta)
    {
        var canonical = new StringBuilder();
        Append(canonical, delta.Route.ProjectionKind);
        Append(canonical, delta.Route.LogicalScope);
        Append(canonical, delta.Route.OwnerId);
        Append(canonical, delta.Route.PhysicalNamespace);
        Append(canonical, delta.Route.RouteEpoch);
        Append(canonical, delta.Route.ContractId);
        Append(canonical, delta.Route.ContractVersion);
        Append(canonical, delta.Source.ActorId);
        Append(canonical, delta.Source.StateVersion);
        Append(canonical, delta.Source.EventId);
        Append(canonical, (int)delta.Mode);
        AppendSection(canonical, "upsert_nodes", delta.UpsertNodes.Count);
        foreach (var node in delta.UpsertNodes.OrderBy(x => x.NodeId, StringComparer.Ordinal))
        {
            Append(canonical, node.NodeId);
            Append(canonical, node.NodeType);
            Append(canonical, node.UpdatedAtEpochMs);
            AppendProperties(canonical, node.Properties);
        }

        AppendSection(canonical, "delete_nodes", delta.DeleteNodeIds.Count);
        foreach (var nodeId in delta.DeleteNodeIds.OrderBy(x => x, StringComparer.Ordinal))
            Append(canonical, nodeId);
        AppendSection(canonical, "upsert_edges", delta.UpsertEdges.Count);
        foreach (var edge in delta.UpsertEdges.OrderBy(x => x.EdgeId, StringComparer.Ordinal))
            AppendEdge(canonical, edge);
        AppendSection(canonical, "delete_edges", delta.DeleteEdgeIds.Count);
        foreach (var edgeId in delta.DeleteEdgeIds.OrderBy(x => x, StringComparer.Ordinal))
            Append(canonical, edgeId);
        AppendSection(canonical, "upsert_pending_edges", delta.UpsertPendingEdges.Count);
        foreach (var edge in delta.UpsertPendingEdges.OrderBy(x => x.EdgeId, StringComparer.Ordinal))
            AppendEdge(canonical, edge);
        AppendSection(canonical, "delete_pending_edges", delta.DeletePendingEdgeIds.Count);
        foreach (var edgeId in delta.DeletePendingEdgeIds.OrderBy(x => x, StringComparer.Ordinal))
            Append(canonical, edgeId);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendEdge(StringBuilder target, ProjectionGraphEdgeMutation edge)
    {
        Append(target, edge.EdgeId);
        Append(target, edge.FromNodeId);
        Append(target, edge.ToNodeId);
        Append(target, edge.EdgeType);
        Append(target, edge.UpdatedAtEpochMs);
        AppendProperties(target, edge.Properties);
    }

    private static void AppendProperties(
        StringBuilder target,
        IEnumerable<KeyValuePair<string, string>> properties)
    {
        var ordered = properties.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
        AppendSection(target, "properties", ordered.Length);
        foreach (var property in ordered)
        {
            Append(target, property.Key);
            Append(target, property.Value);
        }
    }

    private static void Append(StringBuilder target, string value)
    {
        target.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(value);
        target.Append('|');
    }

    private static void Append(StringBuilder target, long value) =>
        Append(target, value.ToString(CultureInfo.InvariantCulture));

    private static void AppendSection(StringBuilder target, string name, int count)
    {
        Append(target, name);
        Append(target, count);
    }

    private static string NormalizeToken(string? value) => value?.Trim() ?? string.Empty;

    private static long NormalizeEpochMilliseconds(long value) =>
        Math.Clamp(value, 0, MaximumEpochMilliseconds);
}
