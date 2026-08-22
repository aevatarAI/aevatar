namespace Aevatar.CQRS.Projection.Providers.InMemory.Stores;

public sealed partial class InMemoryProjectionGraphStore
{
    private readonly Dictionary<VersionedOwnerKey, VersionedOwnerState> _versionedOwners = [];
    private readonly Dictionary<VersionedElementKey, VersionedNodeEntry> _versionedNodes = [];
    private readonly Dictionary<VersionedElementKey, VersionedEdgeEntry> _versionedEdges = [];
    private readonly Dictionary<VersionedElementKey, VersionedEdgeEntry> _versionedPendingEdges = [];
    private readonly Dictionary<VersionedEventKey, VersionedEventEntry> _versionedEvents = [];

    public Task<ProjectionGraphDeltaApplyResult> ApplyDeltaAsync(
        ProjectionGraphDelta delta,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ProjectionGraphDeltaContract.TryNormalize(
                delta,
                out var normalized,
                out var deltaFingerprint,
                out var detail))
        {
            return Task.FromResult(Result(
                ProjectionGraphDeltaApplyDisposition.InvalidDelta,
                detail));
        }

        ProjectionGraphDeltaApplyResult result;
        lock (_gate)
        {
            result = ApplyNormalizedDelta(normalized, deltaFingerprint);
        }

        return Task.FromResult(result);
    }

    public Task<ProjectionGraphOwnerSnapshotReadResult> ReadOwnerSnapshotAsync(
        ProjectionGraphRouteFingerprint route,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ProjectionGraphDeltaContract.TryNormalizeRoute(route, out var normalized, out var detail))
        {
            return Task.FromResult(new ProjectionGraphOwnerSnapshotReadResult
            {
                Disposition = ProjectionGraphOwnerSnapshotReadDisposition.InvalidRoute,
                Detail = detail,
            });
        }

        ProjectionGraphOwnerSnapshotReadResult result;
        lock (_gate)
        {
            var ownerKey = OwnerKey(normalized);
            if (!_versionedOwners.TryGetValue(ownerKey, out var state))
            {
                result = new ProjectionGraphOwnerSnapshotReadResult
                {
                    Disposition = ProjectionGraphOwnerSnapshotReadDisposition.NotFound,
                };
            }
            else if (state.Route.RouteEpoch != normalized.RouteEpoch)
            {
                result = new ProjectionGraphOwnerSnapshotReadResult
                {
                    Disposition = ProjectionGraphOwnerSnapshotReadDisposition.RouteEpochMismatch,
                    Detail = "Stored graph route epoch does not match the requested route epoch.",
                };
            }
            else if (!ProjectionGraphDeltaContract.RouteEquals(state.Route, normalized))
            {
                result = new ProjectionGraphOwnerSnapshotReadResult
                {
                    Disposition = ProjectionGraphOwnerSnapshotReadDisposition.RouteFingerprintMismatch,
                    Detail = "Stored graph route fingerprint does not match the requested route.",
                };
            }
            else
            {
                var snapshot = new ProjectionGraphOwnerSnapshot
                {
                    Route = state.Route.Clone(),
                    Source = state.Source.Clone(),
                };
                snapshot.Nodes.AddRange(_versionedNodes.Values
                    .Where(x => BelongsTo(x.OwnerId, normalized.OwnerId))
                    .Where(x => BelongsTo(x.PhysicalNamespace, normalized.PhysicalNamespace))
                    .Select(x => x.Node.Clone())
                    .OrderBy(x => x.NodeId, StringComparer.Ordinal));
                snapshot.Edges.AddRange(_versionedEdges.Values
                    .Where(x => BelongsTo(x.OwnerId, normalized.OwnerId))
                    .Where(x => BelongsTo(x.PhysicalNamespace, normalized.PhysicalNamespace))
                    .Select(x => x.Edge.Clone())
                    .OrderBy(x => x.EdgeId, StringComparer.Ordinal));
                snapshot.PendingEdges.AddRange(_versionedPendingEdges.Values
                    .Where(x => BelongsTo(x.OwnerId, normalized.OwnerId))
                    .Where(x => BelongsTo(x.PhysicalNamespace, normalized.PhysicalNamespace))
                    .Select(x => x.Edge.Clone())
                    .OrderBy(x => x.EdgeId, StringComparer.Ordinal));
                result = new ProjectionGraphOwnerSnapshotReadResult
                {
                    Disposition = ProjectionGraphOwnerSnapshotReadDisposition.Found,
                    Snapshot = snapshot,
                };
            }
        }

        return Task.FromResult(result);
    }

    private ProjectionGraphDeltaApplyResult ApplyNormalizedDelta(
        ProjectionGraphDelta delta,
        string deltaFingerprint)
    {
        var ownerKey = OwnerKey(delta.Route);
        _versionedOwners.TryGetValue(ownerKey, out var state);
        if (state != null && state.Route.RouteEpoch != delta.Route.RouteEpoch)
        {
            return Result(
                ProjectionGraphDeltaApplyDisposition.RouteEpochMismatch,
                "Stored graph route epoch does not match the delta route epoch.",
                state.Source);
        }

        if (state != null && !ProjectionGraphDeltaContract.RouteEquals(state.Route, delta.Route))
        {
            return Result(
                ProjectionGraphDeltaApplyDisposition.RouteFingerprintMismatch,
                "Stored graph route fingerprint does not match the delta route.",
                state.Source);
        }

        var eventKey = new VersionedEventKey(
            delta.Route.PhysicalNamespace,
            delta.Route.OwnerId,
            delta.Source.EventId);
        if (_versionedEvents.TryGetValue(eventKey, out var existingEvent))
        {
            var exact = SourceEquals(existingEvent.Source, delta.Source) &&
                        string.Equals(existingEvent.DeltaFingerprint, deltaFingerprint, StringComparison.Ordinal);
            return Result(
                exact
                    ? ProjectionGraphDeltaApplyDisposition.ExactDuplicate
                    : ProjectionGraphDeltaApplyDisposition.EventIdConflict,
                exact
                    ? "The exact graph delta was already applied."
                    : "The event id is already bound to another graph source coordinate or payload.",
                state?.Source);
        }

        if (state != null &&
            !string.Equals(state.Source.ActorId, delta.Source.ActorId, StringComparison.Ordinal))
        {
            return Result(
                ProjectionGraphDeltaApplyDisposition.SourceActorMismatch,
                "The graph owner watermark belongs to another source actor.",
                state.Source);
        }

        if (state != null && delta.Source.StateVersion < state.Source.StateVersion)
        {
            return Result(
                ProjectionGraphDeltaApplyDisposition.Stale,
                "The graph delta source version is older than the owner watermark.",
                state.Source);
        }

        if (state != null && delta.Source.StateVersion == state.Source.StateVersion)
        {
            var maintenancePrecedence = ProjectionWriteResultEvaluator
                .EvaluateSameVersionMaintenancePrecedence(
                    state.Source.EventId,
                    delta.Source.EventId);
            if (maintenancePrecedence?.Disposition == ProjectionWriteDisposition.Stale)
            {
                return Result(
                    ProjectionGraphDeltaApplyDisposition.Stale,
                    "A committed maintenance replacement fences the ordinary graph delta at this version.",
                    state.Source);
            }

            if (maintenancePrecedence?.Disposition == ProjectionWriteDisposition.Applied &&
                delta.Mode == ProjectionGraphDeltaMode.RepairOrCutover)
            {
                // Continue into the bounded full-replacement path below.
            }
            else
            {
                return Result(
                    ProjectionGraphDeltaApplyDisposition.SameVersionConflict,
                    "The graph delta conflicts with the committed payload at the same source version.",
                    state.Source);
            }
        }

        var currentVersion = state?.Source.StateVersion ?? 0;
        if (delta.Mode == ProjectionGraphDeltaMode.Normal &&
            delta.Source.StateVersion != currentVersion + 1)
        {
            return Result(
                ProjectionGraphDeltaApplyDisposition.Gap,
                "Normal graph deltas must advance the owner watermark by exactly one version.",
                state?.Source);
        }

        // A repair/cutover delta is the complete desired owner graph: every owned element that
        // is absent from it is stale and deleted here, so the caller never derives deletes from
        // a pre-read snapshot and the delta fingerprint stays a function of stable inputs. The
        // effective mutation count (upserts + explicit deletes + generated stale deletes) is
        // bounded before any mutation; overflow is rejected atomically.
        if (delta.Mode == ProjectionGraphDeltaMode.RepairOrCutover)
        {
            delta = WithStaleOwnerElementDeletes(delta);
            var mutationCount = ProjectionGraphDeltaContract.CountMutations(delta);
            if (mutationCount > ProjectionGraphDeltaContract.MaximumRepairOrCutoverMutationCount)
            {
                return Result(
                    ProjectionGraphDeltaApplyDisposition.MutationBoundExceeded,
                    $"Repair/cutover delta requires {mutationCount} effective mutations; " +
                    $"the bounded replacement limit is {ProjectionGraphDeltaContract.MaximumRepairOrCutoverMutationCount}.",
                    state?.Source);
            }
        }

        var collisionDetail = FindCrossOwnerCollision(delta);
        if (collisionDetail.Length > 0)
        {
            return Result(
                ProjectionGraphDeltaApplyDisposition.CrossOwnerCollision,
                collisionDetail,
                state?.Source);
        }

        var missingEndpoint = FindMissingLiveEndpoint(delta);
        if (missingEndpoint.Length > 0)
        {
            return Result(
                ProjectionGraphDeltaApplyDisposition.MissingEndpoint,
                missingEndpoint,
                state?.Source);
        }

        ApplyMutations(delta);
        var nextState = new VersionedOwnerState(
            delta.Route.Clone(),
            delta.Source.Clone(),
            deltaFingerprint);
        _versionedOwners[ownerKey] = nextState;
        _versionedEvents[eventKey] = new VersionedEventEntry(
            delta.Source.Clone(),
            deltaFingerprint);
        return Result(
            ProjectionGraphDeltaApplyDisposition.Applied,
            "Graph delta applied.",
            delta.Source);
    }

    private ProjectionGraphDelta WithStaleOwnerElementDeletes(ProjectionGraphDelta delta)
    {
        var physicalNamespace = delta.Route.PhysicalNamespace;
        var ownerId = delta.Route.OwnerId;
        // Node ids and edge ids are separate identity spaces (the same token may name a node
        // and an edge); each category is reconciled only against its own desired/explicit sets.
        var desiredNodeIds = delta.UpsertNodes.Select(x => x.NodeId).ToHashSet(StringComparer.Ordinal);
        var desiredEdgeIds = delta.UpsertEdges.Select(x => x.EdgeId)
            .Concat(delta.UpsertPendingEdges.Select(x => x.EdgeId))
            .ToHashSet(StringComparer.Ordinal);
        var explicitNodeDeletes = delta.DeleteNodeIds.ToHashSet(StringComparer.Ordinal);
        var explicitEdgeDeletes = delta.DeleteEdgeIds
            .Concat(delta.DeletePendingEdgeIds)
            .ToHashSet(StringComparer.Ordinal);
        var staleNodeIds = _versionedNodes.Values
            .Where(x => BelongsTo(x.PhysicalNamespace, physicalNamespace) && BelongsTo(x.OwnerId, ownerId))
            .Select(x => x.Node.NodeId)
            .Where(nodeId => !desiredNodeIds.Contains(nodeId) && !explicitNodeDeletes.Contains(nodeId))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var staleEdgeIds = _versionedEdges.Values
            .Where(x => BelongsTo(x.PhysicalNamespace, physicalNamespace) && BelongsTo(x.OwnerId, ownerId))
            .Select(x => x.Edge.EdgeId)
            .Where(edgeId => !desiredEdgeIds.Contains(edgeId) && !explicitEdgeDeletes.Contains(edgeId))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var stalePendingEdgeIds = _versionedPendingEdges.Values
            .Where(x => BelongsTo(x.PhysicalNamespace, physicalNamespace) && BelongsTo(x.OwnerId, ownerId))
            .Select(x => x.Edge.EdgeId)
            .Where(edgeId => !desiredEdgeIds.Contains(edgeId) && !explicitEdgeDeletes.Contains(edgeId))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (staleNodeIds.Length == 0 && staleEdgeIds.Length == 0 && stalePendingEdgeIds.Length == 0)
            return delta;

        var replacement = delta.Clone();
        replacement.DeleteNodeIds.Add(staleNodeIds);
        replacement.DeleteEdgeIds.Add(staleEdgeIds);
        replacement.DeletePendingEdgeIds.Add(stalePendingEdgeIds.Where(x => !staleEdgeIds.Contains(x)));
        return replacement;
    }

    private string FindCrossOwnerCollision(ProjectionGraphDelta delta)
    {
        var ownerId = delta.Route.OwnerId;
        var physicalNamespace = delta.Route.PhysicalNamespace;
        var touchedNodeIds = delta.UpsertNodes.Select(x => x.NodeId)
            .Concat(delta.DeleteNodeIds)
            .Concat(delta.UpsertEdges.SelectMany(x => new[] { x.FromNodeId, x.ToNodeId }))
            .Concat(delta.UpsertPendingEdges.SelectMany(x => new[] { x.FromNodeId, x.ToNodeId }))
            .Distinct(StringComparer.Ordinal);
        foreach (var nodeId in touchedNodeIds)
        {
            var key = new VersionedElementKey(physicalNamespace, nodeId);
            if (_versionedNodes.TryGetValue(key, out var node) && !BelongsTo(node.OwnerId, ownerId))
                return $"Node '{nodeId}' is owned by another graph owner.";

            var legacyKey = BuildNodeKey(physicalNamespace, nodeId);
            if (_nodes.ContainsKey(legacyKey) && !_versionedNodes.ContainsKey(key))
                return $"Node '{nodeId}' already exists without v2 owner provenance.";
        }

        var touchedEdges = delta.UpsertEdges.Select(x => x.EdgeId)
            .Concat(delta.UpsertPendingEdges.Select(x => x.EdgeId))
            .Concat(delta.DeleteEdgeIds)
            .Concat(delta.DeletePendingEdgeIds)
            .Distinct(StringComparer.Ordinal);
        foreach (var edgeId in touchedEdges)
        {
            var key = new VersionedElementKey(physicalNamespace, edgeId);
            if (_versionedEdges.TryGetValue(key, out var edge) && !BelongsTo(edge.OwnerId, ownerId))
                return $"Edge '{edgeId}' is owned by another graph owner.";
            if (_versionedPendingEdges.TryGetValue(key, out var pending) && !BelongsTo(pending.OwnerId, ownerId))
                return $"Pending edge '{edgeId}' is owned by another graph owner.";

            var legacyKey = BuildEdgeKey(physicalNamespace, edgeId);
            if (_edges.ContainsKey(legacyKey) && !_versionedEdges.ContainsKey(key))
                return $"Edge '{edgeId}' already exists without v2 owner provenance.";
        }

        foreach (var nodeId in delta.DeleteNodeIds)
        {
            var foreignIncident = _versionedEdges.Values.Any(x =>
                                      BelongsTo(x.PhysicalNamespace, physicalNamespace) &&
                                      !BelongsTo(x.OwnerId, ownerId) &&
                                      IsIncident(x.Edge, nodeId)) ||
                                  _versionedPendingEdges.Values.Any(x =>
                                      BelongsTo(x.PhysicalNamespace, physicalNamespace) &&
                                      !BelongsTo(x.OwnerId, ownerId) &&
                                      IsIncident(x.Edge, nodeId));
            if (foreignIncident)
                return $"Node '{nodeId}' is referenced by another graph owner.";

            var unversionedIncident = _edges.Values.Any(x =>
                BelongsTo(x.Scope, physicalNamespace) &&
                IsIncident(x, nodeId) &&
                !HasMatchingVersionedProvenance(physicalNamespace, ownerId, x));
            if (unversionedIncident)
                return $"Node '{nodeId}' is referenced by an edge without v2 owner provenance.";
        }

        return string.Empty;
    }

    private string FindMissingLiveEndpoint(ProjectionGraphDelta delta)
    {
        foreach (var edge in delta.UpsertEdges)
        {
            if (!WillContainOwnedNode(delta, edge.FromNodeId))
                return $"Live edge '{edge.EdgeId}' is missing source node '{edge.FromNodeId}'.";
            if (!WillContainOwnedNode(delta, edge.ToNodeId))
                return $"Live edge '{edge.EdgeId}' is missing target node '{edge.ToNodeId}'.";
        }

        return string.Empty;
    }

    private bool WillContainOwnedNode(ProjectionGraphDelta delta, string nodeId)
    {
        if (delta.UpsertNodes.Any(x => string.Equals(x.NodeId, nodeId, StringComparison.Ordinal)))
            return true;
        if (delta.DeleteNodeIds.Contains(nodeId))
            return false;

        return _versionedNodes.TryGetValue(
                   new VersionedElementKey(delta.Route.PhysicalNamespace, nodeId),
                   out var existing) &&
               BelongsTo(existing.OwnerId, delta.Route.OwnerId);
    }

    private void ApplyMutations(ProjectionGraphDelta delta)
    {
        var physicalNamespace = delta.Route.PhysicalNamespace;
        var ownerId = delta.Route.OwnerId;
        foreach (var nodeId in delta.DeleteNodeIds)
        {
            var nodeKey = new VersionedElementKey(physicalNamespace, nodeId);
            _versionedNodes.Remove(nodeKey);
            _nodes.Remove(BuildNodeKey(physicalNamespace, nodeId));

            var incidentEdgeIds = _versionedEdges
                .Where(x => BelongsTo(x.Value.PhysicalNamespace, physicalNamespace))
                .Where(x => BelongsTo(x.Value.OwnerId, ownerId))
                .Where(x => IsIncident(x.Value.Edge, nodeId))
                .Select(x => x.Key.Identifier)
                .ToArray();
            var incidentPendingEdgeIds = _versionedPendingEdges
                .Where(x => BelongsTo(x.Value.PhysicalNamespace, physicalNamespace))
                .Where(x => BelongsTo(x.Value.OwnerId, ownerId))
                .Where(x => IsIncident(x.Value.Edge, nodeId))
                .Select(x => x.Key.Identifier)
                .ToArray();
            foreach (var edgeId in incidentEdgeIds.Concat(incidentPendingEdgeIds).Distinct(StringComparer.Ordinal))
                RemoveVersionedEdge(physicalNamespace, edgeId);
        }

        foreach (var edgeId in delta.DeleteEdgeIds.Concat(delta.DeletePendingEdgeIds))
            RemoveVersionedEdge(physicalNamespace, edgeId);

        foreach (var node in delta.UpsertNodes)
        {
            var key = new VersionedElementKey(physicalNamespace, node.NodeId);
            _versionedNodes[key] = new VersionedNodeEntry(
                physicalNamespace,
                ownerId,
                node.Clone());
            _nodes[BuildNodeKey(physicalNamespace, node.NodeId)] = ToLegacyNode(
                physicalNamespace,
                ownerId,
                node);
        }

        foreach (var edge in delta.UpsertEdges)
            StoreLiveEdge(physicalNamespace, ownerId, edge);

        foreach (var pendingEdge in delta.UpsertPendingEdges)
        {
            if (ContainsOwnedNode(physicalNamespace, ownerId, pendingEdge.FromNodeId) &&
                ContainsOwnedNode(physicalNamespace, ownerId, pendingEdge.ToNodeId))
            {
                StoreLiveEdge(physicalNamespace, ownerId, pendingEdge);
            }
            else
            {
                var key = new VersionedElementKey(physicalNamespace, pendingEdge.EdgeId);
                _versionedEdges.Remove(key);
                _edges.Remove(BuildEdgeKey(physicalNamespace, pendingEdge.EdgeId));
                _versionedPendingEdges[key] = new VersionedEdgeEntry(
                    physicalNamespace,
                    ownerId,
                    pendingEdge.Clone());
            }
        }

        if (delta.UpsertNodes.Count > 0)
        {
            var upsertedNodeIds = delta.UpsertNodes
                .Select(x => x.NodeId)
                .ToHashSet(StringComparer.Ordinal);
            var readyPendingEdges = _versionedPendingEdges.Values
                .Where(x => BelongsTo(x.PhysicalNamespace, physicalNamespace))
                .Where(x => BelongsTo(x.OwnerId, ownerId))
                .Where(x =>
                    upsertedNodeIds.Contains(x.Edge.FromNodeId) ||
                    upsertedNodeIds.Contains(x.Edge.ToNodeId))
                .Where(x => ContainsOwnedNode(physicalNamespace, ownerId, x.Edge.FromNodeId))
                .Where(x => ContainsOwnedNode(physicalNamespace, ownerId, x.Edge.ToNodeId))
                .Select(x => x.Edge.Clone())
                .ToArray();
            foreach (var readyPendingEdge in readyPendingEdges)
                StoreLiveEdge(physicalNamespace, ownerId, readyPendingEdge);
        }
    }

    private void StoreLiveEdge(
        string physicalNamespace,
        string ownerId,
        ProjectionGraphEdgeMutation edge)
    {
        var key = new VersionedElementKey(physicalNamespace, edge.EdgeId);
        _versionedPendingEdges.Remove(key);
        _versionedEdges[key] = new VersionedEdgeEntry(
            physicalNamespace,
            ownerId,
            edge.Clone());
        _edges[BuildEdgeKey(physicalNamespace, edge.EdgeId)] = ToLegacyEdge(
            physicalNamespace,
            ownerId,
            edge);
    }

    private void RemoveVersionedEdge(string physicalNamespace, string edgeId)
    {
        var key = new VersionedElementKey(physicalNamespace, edgeId);
        _versionedEdges.Remove(key);
        _versionedPendingEdges.Remove(key);
        _edges.Remove(BuildEdgeKey(physicalNamespace, edgeId));
    }

    private bool ContainsOwnedNode(string physicalNamespace, string ownerId, string nodeId)
    {
        return _versionedNodes.TryGetValue(
                   new VersionedElementKey(physicalNamespace, nodeId),
                   out var node) &&
               BelongsTo(node.OwnerId, ownerId);
    }

    private bool HasMatchingVersionedProvenance(
        string physicalNamespace,
        string ownerId,
        ProjectionGraphEdge edge)
    {
        return _versionedEdges.TryGetValue(
                   new VersionedElementKey(physicalNamespace, edge.EdgeId),
                   out var versioned) &&
               BelongsTo(versioned.OwnerId, ownerId) &&
               string.Equals(versioned.Edge.FromNodeId, edge.FromNodeId, StringComparison.Ordinal) &&
               string.Equals(versioned.Edge.ToNodeId, edge.ToNodeId, StringComparison.Ordinal);
    }

    private static ProjectionGraphNode ToLegacyNode(
        string physicalNamespace,
        string ownerId,
        ProjectionGraphNodeMutation node)
    {
        var properties = node.Properties.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        properties[ProjectionGraphManagedPropertyKeys.ManagedMarkerKey] =
            ProjectionGraphManagedPropertyKeys.ManagedMarkerValue;
        properties[ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey] = ownerId;
        return new ProjectionGraphNode
        {
            Scope = physicalNamespace,
            NodeId = node.NodeId,
            NodeType = node.NodeType,
            Properties = properties,
            UpdatedAt = FromEpochMilliseconds(node.UpdatedAtEpochMs),
        };
    }

    private static ProjectionGraphEdge ToLegacyEdge(
        string physicalNamespace,
        string ownerId,
        ProjectionGraphEdgeMutation edge)
    {
        var properties = edge.Properties.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        properties[ProjectionGraphManagedPropertyKeys.ManagedMarkerKey] =
            ProjectionGraphManagedPropertyKeys.ManagedMarkerValue;
        properties[ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey] = ownerId;
        return new ProjectionGraphEdge
        {
            Scope = physicalNamespace,
            EdgeId = edge.EdgeId,
            FromNodeId = edge.FromNodeId,
            ToNodeId = edge.ToNodeId,
            EdgeType = edge.EdgeType,
            Properties = properties,
            UpdatedAt = FromEpochMilliseconds(edge.UpdatedAtEpochMs),
        };
    }

    private static DateTimeOffset FromEpochMilliseconds(long value)
    {
        return value <= 0 ? DateTimeOffset.UnixEpoch : DateTimeOffset.FromUnixTimeMilliseconds(value);
    }

    private static ProjectionGraphDeltaApplyResult Result(
        ProjectionGraphDeltaApplyDisposition disposition,
        string detail,
        ProjectionGraphSourceCoordinate? source = null)
    {
        var result = new ProjectionGraphDeltaApplyResult
        {
            Disposition = disposition,
            Detail = detail,
        };
        if (source != null)
            result.CurrentSource = source.Clone();
        return result;
    }

    private static VersionedOwnerKey OwnerKey(ProjectionGraphRouteFingerprint route) =>
        new(route.PhysicalNamespace, route.OwnerId);

    private static bool SourceEquals(
        ProjectionGraphSourceCoordinate left,
        ProjectionGraphSourceCoordinate right)
    {
        return string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal) &&
               left.StateVersion == right.StateVersion &&
               string.Equals(left.EventId, right.EventId, StringComparison.Ordinal);
    }

    private static bool BelongsTo(string value, string expected) =>
        string.Equals(value, expected, StringComparison.Ordinal);

    private static bool IsIncident(ProjectionGraphEdgeMutation edge, string nodeId) =>
        string.Equals(edge.FromNodeId, nodeId, StringComparison.Ordinal) ||
        string.Equals(edge.ToNodeId, nodeId, StringComparison.Ordinal);

    private static bool IsIncident(ProjectionGraphEdge edge, string nodeId) =>
        string.Equals(edge.FromNodeId, nodeId, StringComparison.Ordinal) ||
        string.Equals(edge.ToNodeId, nodeId, StringComparison.Ordinal);

    private readonly record struct VersionedOwnerKey(string PhysicalNamespace, string OwnerId);

    private readonly record struct VersionedElementKey(string PhysicalNamespace, string Identifier);

    private readonly record struct VersionedEventKey(
        string PhysicalNamespace,
        string OwnerId,
        string EventId);

    private sealed record VersionedOwnerState(
        ProjectionGraphRouteFingerprint Route,
        ProjectionGraphSourceCoordinate Source,
        string DeltaFingerprint);

    private sealed record VersionedNodeEntry(
        string PhysicalNamespace,
        string OwnerId,
        ProjectionGraphNodeMutation Node);

    private sealed record VersionedEdgeEntry(
        string PhysicalNamespace,
        string OwnerId,
        ProjectionGraphEdgeMutation Edge);

    private sealed record VersionedEventEntry(
        ProjectionGraphSourceCoordinate Source,
        string DeltaFingerprint);
}
