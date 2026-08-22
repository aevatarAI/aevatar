using System.Globalization;
using Google.Protobuf;
using Neo4j.Driver;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.Stores;

public sealed partial class Neo4jProjectionGraphStore
{
    private const long VersionedGraphSchemaVersion = 2;

    public Task<ProjectionGraphDeltaApplyResult> ApplyDeltaAsync(
        ProjectionGraphDelta delta,
        CancellationToken ct = default) =>
        Neo4jProjectionGraphStoreTelemetry.ObserveWriteAsync(
            _logger,
            Neo4jProjectionGraphWriteTelemetryContext.ForApplyDelta(delta),
            ct,
            () => ApplyDeltaCoreAsync(delta, ct),
            static result => ResolveDeltaTelemetryResult(result.Disposition));

    // Bounded, low-cardinality metric tag: the disposition enum name, never route or owner values.
    private static string ResolveDeltaTelemetryResult(ProjectionGraphDeltaApplyDisposition disposition) =>
        disposition switch
        {
            ProjectionGraphDeltaApplyDisposition.Applied => Neo4jProjectionGraphStoreTelemetry.CompletedResult,
            _ => disposition.ToString(),
        };

    private async Task<ProjectionGraphDeltaApplyResult> ApplyDeltaCoreAsync(
        ProjectionGraphDelta delta,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ProjectionGraphDeltaContract.TryNormalize(
                delta,
                out var normalized,
                out var deltaFingerprint,
                out var detail))
        {
            return DeltaResult(
                ProjectionGraphDeltaApplyDisposition.InvalidDelta,
                detail);
        }

        await EnsureSchemaAsync(ct);
        await using var session = CreateSession(AccessMode.Write);
        await using var transaction = await session.BeginTransactionAsync();
        var parameters = BuildRouteParameters(normalized.Route);
        var stateRows = await RunRowsAsync(
            transaction,
            Neo4jProjectionGraphStoreVersionedCypherSupport.BuildLockOwnerStateCypher(
                _versionedOwnerStateLabel),
            parameters,
            ct);
        var state = MapOwnerState(normalized.Route, stateRows.Single());

        var rejection = EvaluateStateAndEventIndependentRules(normalized, state);
        if (rejection != null)
            return await RollbackAsync(transaction, rejection);

        parameters["eventId"] = normalized.Source.EventId;
        var eventRows = await RunRowsAsync(
            transaction,
            Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadEventCypher(_versionedEventLabel),
            parameters,
            ct);
        if (eventRows.Count > 0)
        {
            var storedEvent = MapStoredEvent(normalized.Source.EventId, eventRows.Single());
            var exact = SourceEquals(storedEvent.Source, normalized.Source) &&
                        string.Equals(storedEvent.DeltaFingerprint, deltaFingerprint, StringComparison.Ordinal);
            return await RollbackAsync(
                transaction,
                DeltaResult(
                    exact
                        ? ProjectionGraphDeltaApplyDisposition.ExactDuplicate
                        : ProjectionGraphDeltaApplyDisposition.EventIdConflict,
                    exact
                        ? "The exact graph delta was already applied."
                        : "The event id is already bound to another graph source coordinate or payload.",
                    state?.Source));
        }

        rejection = EvaluateVersionRules(normalized, state);
        if (rejection != null)
            return await RollbackAsync(transaction, rejection);

        // A repair/cutover delta is the complete desired owner graph: every owned element that
        // is absent from it is stale and deleted in this transaction (under the owner-state
        // lock), so the caller never derives deletes from a pre-read snapshot and the delta
        // fingerprint recorded below stays a function of stable inputs.
        var effective = normalized.Mode == ProjectionGraphDeltaMode.RepairOrCutover
            ? await WithStaleOwnerElementDeletesAsync(transaction, normalized, parameters, ct)
            : normalized;
        if (normalized.Mode == ProjectionGraphDeltaMode.RepairOrCutover)
        {
            // Bounded replacement: the effective mutation count (upserts + explicit deletes +
            // generated stale deletes) is checked before any mutation; overflow rolls back with
            // watermark and snapshot untouched.
            var mutationCount = ProjectionGraphDeltaContract.CountMutations(effective);
            if (mutationCount > ProjectionGraphDeltaContract.MaximumRepairOrCutoverMutationCount)
            {
                return await RollbackAsync(
                    transaction,
                    DeltaResult(
                        ProjectionGraphDeltaApplyDisposition.MutationBoundExceeded,
                        $"Repair/cutover delta requires {mutationCount} effective mutations; " +
                        $"the bounded replacement limit is {ProjectionGraphDeltaContract.MaximumRepairOrCutoverMutationCount}.",
                        state?.Source));
            }
        }

        var nodeIds = effective.UpsertNodes.Select(x => x.NodeId)
            .Concat(effective.DeleteNodeIds)
            .Concat(effective.UpsertEdges.SelectMany(x => new[] { x.FromNodeId, x.ToNodeId }))
            .Concat(effective.UpsertPendingEdges.SelectMany(x => new[] { x.FromNodeId, x.ToNodeId }))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        parameters["nodeIds"] = nodeIds;
        var nodeRows = await RunRowsWhenIdentifiersPresentAsync(
            transaction,
            nodeIds,
            Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadTouchedNodesCypher(_nodeLabel),
            parameters,
            ct);
        var nodeOwners = nodeRows.ToDictionary(
            row => ReadString(row, "nodeId"),
            row => new StoredElementOwner(
                ReadString(row, "projectionOwnerId"),
                ReadLong(row, "projectionGraphVersion")),
            StringComparer.Ordinal);
        var nodeCollision = nodeOwners.FirstOrDefault(x =>
            !string.Equals(x.Value.OwnerId, normalized.Route.OwnerId, StringComparison.Ordinal) ||
            x.Value.SchemaVersion != VersionedGraphSchemaVersion);
        if (nodeCollision.Key != null)
        {
            return await RollbackAsync(
                transaction,
                DeltaResult(
                    ProjectionGraphDeltaApplyDisposition.CrossOwnerCollision,
                    $"Node '{nodeCollision.Key}' already exists without matching v2 owner provenance.",
                    state?.Source));
        }

        var edgeIds = effective.UpsertEdges.Select(x => x.EdgeId)
            .Concat(effective.UpsertPendingEdges.Select(x => x.EdgeId))
            .Concat(effective.DeleteEdgeIds)
            .Concat(effective.DeletePendingEdgeIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        parameters["edgeIds"] = edgeIds;
        var edgeIdentityRows = await RunRowsWhenIdentifiersPresentAsync(
            transaction,
            edgeIds,
            Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadTouchedEdgeIdentitiesCypher(
                _versionedEdgeIdentityLabel),
            parameters,
            ct);
        var edgeIdentityOwners = edgeIdentityRows.ToDictionary(
            row => ReadString(row, "edgeId"),
            row => new StoredElementOwner(
                ReadString(row, "projectionOwnerId"),
                ReadLong(row, "projectionGraphVersion")),
            StringComparer.Ordinal);
        var edgeIdentityCollision = edgeIdentityOwners.FirstOrDefault(x =>
            !string.Equals(x.Value.OwnerId, normalized.Route.OwnerId, StringComparison.Ordinal) ||
            x.Value.SchemaVersion != VersionedGraphSchemaVersion);
        if (edgeIdentityCollision.Key != null)
        {
            return await RollbackAsync(
                transaction,
                DeltaResult(
                    ProjectionGraphDeltaApplyDisposition.CrossOwnerCollision,
                    $"Edge '{edgeIdentityCollision.Key}' is owned by another graph owner.",
                    state?.Source));
        }

        var relationshipRows = await RunRowsWhenIdentifiersPresentAsync(
            transaction,
            edgeIds,
            Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadTouchedRelationshipsCypher(_edgeType),
            parameters,
            ct);
        var relationshipCollision = relationshipRows.FirstOrDefault(row =>
        {
            var edgeId = ReadString(row, "edgeId");
            return !edgeIdentityOwners.ContainsKey(edgeId) ||
                   !string.Equals(
                       ReadString(row, "projectionOwnerId"),
                       normalized.Route.OwnerId,
                       StringComparison.Ordinal) ||
                   ReadLong(row, "projectionGraphVersion") != VersionedGraphSchemaVersion;
        });
        if (relationshipCollision != null)
        {
            return await RollbackAsync(
                transaction,
                DeltaResult(
                    ProjectionGraphDeltaApplyDisposition.CrossOwnerCollision,
                    $"Edge '{ReadString(relationshipCollision, "edgeId")}' already exists without matching v2 owner provenance.",
                    state?.Source));
        }

        var hasForeignIncidentRelationship = await HasForeignIncidentRelationshipAsync(
            transaction,
            effective,
            parameters,
            ct);
        if (hasForeignIncidentRelationship)
        {
            return await RollbackAsync(
                transaction,
                DeltaResult(
                    ProjectionGraphDeltaApplyDisposition.CrossOwnerCollision,
                    "A deleted node is referenced by another graph owner.",
                    state?.Source));
        }

        var resultingNodeIds = nodeOwners.Keys.ToHashSet(StringComparer.Ordinal);
        resultingNodeIds.ExceptWith(effective.DeleteNodeIds);
        resultingNodeIds.UnionWith(effective.UpsertNodes.Select(x => x.NodeId));
        var missingEndpoint = effective.UpsertEdges
            .SelectMany(edge => new[]
            {
                (edge.EdgeId, Endpoint: edge.FromNodeId, Role: "source"),
                (edge.EdgeId, Endpoint: edge.ToNodeId, Role: "target"),
            })
            .FirstOrDefault(x => !resultingNodeIds.Contains(x.Endpoint));
        if (missingEndpoint.EdgeId != null)
        {
            return await RollbackAsync(
                transaction,
                DeltaResult(
                    ProjectionGraphDeltaApplyDisposition.MissingEndpoint,
                    $"Live edge '{missingEndpoint.EdgeId}' is missing {missingEndpoint.Role} node '{missingEndpoint.Endpoint}'.",
                    state?.Source));
        }

        var mutationRejection = await ApplyNeo4jMutationsAsync(
            transaction,
            effective,
            parameters,
            ct);
        if (mutationRejection != null)
        {
            return await RollbackAsync(
                transaction,
                DeltaResult(
                    mutationRejection.Disposition,
                    mutationRejection.Detail,
                    state?.Source));
        }
        parameters["projectionKind"] = normalized.Route.ProjectionKind;
        parameters["logicalScope"] = normalized.Route.LogicalScope;
        parameters["contractId"] = normalized.Route.ContractId;
        parameters["contractVersion"] = normalized.Route.ContractVersion;
        parameters["routeEpoch"] = normalized.Route.RouteEpoch;
        parameters["sourceActorId"] = normalized.Source.ActorId;
        parameters["sourceVersion"] = normalized.Source.StateVersion;
        parameters["eventId"] = normalized.Source.EventId;
        parameters["deltaFingerprint"] = deltaFingerprint;
        await RunAndConsumeAsync(
            transaction,
            Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCommitWatermarkCypher(
                _versionedOwnerStateLabel,
                _versionedEventLabel),
            parameters,
            ct);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync();
        return DeltaResult(
            ProjectionGraphDeltaApplyDisposition.Applied,
            "Graph delta applied.",
            normalized.Source);
    }

    public async Task<ProjectionGraphOwnerSnapshotReadResult> ReadOwnerSnapshotAsync(
        ProjectionGraphRouteFingerprint route,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ProjectionGraphDeltaContract.TryNormalizeRoute(route, out var normalized, out var detail))
        {
            return new ProjectionGraphOwnerSnapshotReadResult
            {
                Disposition = ProjectionGraphOwnerSnapshotReadDisposition.InvalidRoute,
                Detail = detail,
            };
        }

        await EnsureSchemaAsync(ct);
        var rows = await ExecuteReadAsync(
            Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadOwnerSnapshotCypher(
                _versionedOwnerStateLabel,
                _nodeLabel,
                _edgeType,
                _versionedEdgeIdentityLabel),
            BuildRouteParameters(normalized),
            ct);
        if (rows.Count == 0)
        {
            return new ProjectionGraphOwnerSnapshotReadResult
            {
                Disposition = ProjectionGraphOwnerSnapshotReadDisposition.NotFound,
            };
        }

        var row = rows.Single();
        var storedRoute = MapStoredRoute(normalized.PhysicalNamespace, normalized.OwnerId, row);
        if (storedRoute.RouteEpoch != normalized.RouteEpoch)
        {
            return new ProjectionGraphOwnerSnapshotReadResult
            {
                Disposition = ProjectionGraphOwnerSnapshotReadDisposition.RouteEpochMismatch,
                Detail = "Stored graph route epoch does not match the requested route epoch.",
            };
        }

        if (!ProjectionGraphDeltaContract.RouteEquals(storedRoute, normalized))
        {
            return new ProjectionGraphOwnerSnapshotReadResult
            {
                Disposition = ProjectionGraphOwnerSnapshotReadDisposition.RouteFingerprintMismatch,
                Detail = "Stored graph route fingerprint does not match the requested route.",
            };
        }

        var snapshot = new ProjectionGraphOwnerSnapshot
        {
            Route = storedRoute,
            Source = new ProjectionGraphSourceCoordinate
            {
                ActorId = ReadString(row, "sourceActorId"),
                StateVersion = ReadLong(row, "sourceVersion"),
                EventId = ReadString(row, "sourceEventId"),
            },
        };
        snapshot.Nodes.AddRange(ReadMapList(row, "nodes")
            .Select(MapSnapshotNode)
            .Where(x => x != null)
            .Select(x => x!)
            .OrderBy(x => x.NodeId, StringComparer.Ordinal));
        snapshot.Edges.AddRange(ReadMapList(row, "edges")
            .Select(MapSnapshotEdge)
            .Where(x => x != null)
            .Select(x => x!)
            .OrderBy(x => x.EdgeId, StringComparer.Ordinal));
        snapshot.PendingEdges.AddRange(ReadMapList(row, "pendingEdges")
            .Select(MapSnapshotEdge)
            .Where(x => x != null)
            .Select(x => x!)
            .OrderBy(x => x.EdgeId, StringComparer.Ordinal));
        return new ProjectionGraphOwnerSnapshotReadResult
        {
            Disposition = ProjectionGraphOwnerSnapshotReadDisposition.Found,
            Snapshot = snapshot,
        };
    }

    private ProjectionGraphDeltaApplyResult? EvaluateStateAndEventIndependentRules(
        ProjectionGraphDelta delta,
        StoredOwnerState? state)
    {
        if (state == null)
            return null;
        if (state.Route.RouteEpoch != delta.Route.RouteEpoch)
        {
            return DeltaResult(
                ProjectionGraphDeltaApplyDisposition.RouteEpochMismatch,
                "Stored graph route epoch does not match the delta route epoch.",
                state.Source);
        }

        if (!ProjectionGraphDeltaContract.RouteEquals(state.Route, delta.Route))
        {
            return DeltaResult(
                ProjectionGraphDeltaApplyDisposition.RouteFingerprintMismatch,
                "Stored graph route fingerprint does not match the delta route.",
                state.Source);
        }

        return null;
    }

    private static ProjectionGraphDeltaApplyResult? EvaluateVersionRules(
        ProjectionGraphDelta delta,
        StoredOwnerState? state)
    {
        if (state != null &&
            !string.Equals(state.Source.ActorId, delta.Source.ActorId, StringComparison.Ordinal))
        {
            return DeltaResult(
                ProjectionGraphDeltaApplyDisposition.SourceActorMismatch,
                "The graph owner watermark belongs to another source actor.",
                state.Source);
        }

        if (state != null && delta.Source.StateVersion < state.Source.StateVersion)
        {
            return DeltaResult(
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
                return DeltaResult(
                    ProjectionGraphDeltaApplyDisposition.Stale,
                    "A committed maintenance replacement fences the ordinary graph delta at this version.",
                    state.Source);
            }

            if (maintenancePrecedence?.Disposition != ProjectionWriteDisposition.Applied ||
                delta.Mode != ProjectionGraphDeltaMode.RepairOrCutover)
            {
                return DeltaResult(
                    ProjectionGraphDeltaApplyDisposition.SameVersionConflict,
                    "The graph delta conflicts with the committed payload at the same source version.",
                    state.Source);
            }
        }

        var currentVersion = state?.Source.StateVersion ?? 0;
        if (delta.Mode == ProjectionGraphDeltaMode.Normal &&
            delta.Source.StateVersion != currentVersion + 1)
        {
            return DeltaResult(
                ProjectionGraphDeltaApplyDisposition.Gap,
                "Normal graph deltas must advance the owner watermark by exactly one version.",
                state?.Source);
        }

        return null;
    }

    private async Task<ProjectionGraphDelta> WithStaleOwnerElementDeletesAsync(
        IAsyncTransaction transaction,
        ProjectionGraphDelta delta,
        Dictionary<string, object?> parameters,
        CancellationToken ct)
    {
        var rows = await RunRowsAsync(
            transaction,
            Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadOwnedElementIdsCypher(
                _nodeLabel,
                _versionedEdgeIdentityLabel),
            parameters,
            ct);
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
        var staleNodeIds = new SortedSet<string>(StringComparer.Ordinal);
        var staleEdgeIds = new SortedSet<string>(StringComparer.Ordinal);
        var stalePendingEdgeIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var kind = ReadString(row, "kind");
            var identifier = ReadString(row, "id");
            if (identifier.Length == 0)
                continue;
            switch (kind)
            {
                case "node" when !desiredNodeIds.Contains(identifier) && !explicitNodeDeletes.Contains(identifier):
                    staleNodeIds.Add(identifier);
                    break;
                case "edge" when !desiredEdgeIds.Contains(identifier) && !explicitEdgeDeletes.Contains(identifier):
                    staleEdgeIds.Add(identifier);
                    break;
                case "pending" when !desiredEdgeIds.Contains(identifier) && !explicitEdgeDeletes.Contains(identifier):
                    stalePendingEdgeIds.Add(identifier);
                    break;
            }
        }

        if (staleNodeIds.Count == 0 && staleEdgeIds.Count == 0 && stalePendingEdgeIds.Count == 0)
            return delta;

        var replacement = delta.Clone();
        replacement.DeleteNodeIds.Add(staleNodeIds);
        replacement.DeleteEdgeIds.Add(staleEdgeIds);
        replacement.DeletePendingEdgeIds.Add(stalePendingEdgeIds.Where(x => !staleEdgeIds.Contains(x)));
        return replacement;
    }

    private async Task<MutationRejection?> ApplyNeo4jMutationsAsync(
        IAsyncTransaction transaction,
        ProjectionGraphDelta delta,
        Dictionary<string, object?> parameters,
        CancellationToken ct)
    {
        if (delta.DeleteNodeIds.Count > 0)
        {
            parameters["nodeIds"] = delta.DeleteNodeIds.ToArray();
            await RunAndConsumeAsync(
                transaction,
                Neo4jProjectionGraphStoreVersionedCypherSupport.BuildDeleteNodesCypher(
                    _nodeLabel,
                    _edgeType,
                    _versionedEdgeIdentityLabel),
                parameters,
                ct);
        }

        var deletedEdgeIds = delta.DeleteEdgeIds
            .Concat(delta.DeletePendingEdgeIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (deletedEdgeIds.Length > 0)
        {
            parameters["edgeIds"] = deletedEdgeIds;
            await RunAndConsumeAsync(
                transaction,
                Neo4jProjectionGraphStoreVersionedCypherSupport.BuildDeleteEdgesCypher(
                    _edgeType,
                    _versionedEdgeIdentityLabel),
                parameters,
                ct);
        }

        if (delta.UpsertNodes.Count > 0)
        {
            parameters["nodes"] = delta.UpsertNodes.Select(ToNodeParameters).ToArray();
            var appliedCount = await RunCountAsync(
                transaction,
                Neo4jProjectionGraphStoreVersionedCypherSupport.BuildUpsertNodesCypher(_nodeLabel),
                parameters,
                ct);
            if (appliedCount != delta.UpsertNodes.Count)
            {
                return new MutationRejection(
                    ProjectionGraphDeltaApplyDisposition.CrossOwnerCollision,
                    "A graph node was claimed concurrently.");
            }
        }

        var edgeItems = delta.UpsertEdges
            .Select(edge => ToEdgeParameters(edge, "live"))
            .Concat(delta.UpsertPendingEdges.Select(edge => ToEdgeParameters(edge, "pending")))
            .ToArray();
        if (edgeItems.Length > 0)
        {
            parameters["edges"] = edgeItems;
            var appliedCount = await RunCountAsync(
                transaction,
                Neo4jProjectionGraphStoreVersionedCypherSupport.BuildUpsertEdgeIdentitiesCypher(
                    _versionedEdgeIdentityLabel),
                parameters,
                ct);
            if (appliedCount != edgeItems.Length)
            {
                return new MutationRejection(
                    ProjectionGraphDeltaApplyDisposition.CrossOwnerCollision,
                    "A graph edge was claimed concurrently.");
            }

            parameters["edgeIds"] = edgeItems.Select(x => x["edgeId"]).ToArray();
            await RunAndConsumeAsync(
                transaction,
                Neo4jProjectionGraphStoreVersionedCypherSupport.BuildDeleteRelationshipsForRewireCypher(_edgeType),
                parameters,
                ct);
        }

        if (delta.UpsertEdges.Count > 0)
        {
            parameters["edges"] = delta.UpsertEdges.Select(edge => ToEdgeParameters(edge, "live")).ToArray();
            var appliedCount = await RunCountAsync(
                transaction,
                Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreateLiveEdgesCypher(
                    _nodeLabel,
                    _edgeType),
                parameters,
                ct);
            if (appliedCount != delta.UpsertEdges.Count)
            {
                return new MutationRejection(
                    ProjectionGraphDeltaApplyDisposition.MissingEndpoint,
                    "Validated graph edge endpoints disappeared during the transaction.");
            }
        }

        var promotableNodeIds = delta.UpsertNodes.Select(x => x.NodeId).ToArray();
        var promotableEdgeIds = delta.UpsertPendingEdges.Select(x => x.EdgeId).ToArray();
        if (promotableNodeIds.Length > 0 || promotableEdgeIds.Length > 0)
        {
            parameters.Remove("edges");
            var promotableRows = new List<IReadOnlyDictionary<string, object>>();
            if (promotableEdgeIds.Length > 0)
            {
                parameters["promotableEdgeIds"] = promotableEdgeIds;
                promotableRows.AddRange(await RunRowsAsync(
                    transaction,
                    Neo4jProjectionGraphStoreVersionedCypherSupport.BuildSelectPromotablePendingEdgesByIdCypher(
                        _versionedEdgeIdentityLabel),
                    parameters,
                    ct));
            }

            if (promotableNodeIds.Length > 0)
            {
                parameters["promotableNodeIds"] = promotableNodeIds;
                promotableRows.AddRange(await RunRowsAsync(
                    transaction,
                    Neo4jProjectionGraphStoreVersionedCypherSupport.BuildSelectPromotablePendingEdgesByFromNodeCypher(
                        _versionedEdgeIdentityLabel),
                    parameters,
                    ct));
                promotableRows.AddRange(await RunRowsAsync(
                    transaction,
                    Neo4jProjectionGraphStoreVersionedCypherSupport.BuildSelectPromotablePendingEdgesByToNodeCypher(
                        _versionedEdgeIdentityLabel),
                    parameters,
                    ct));
            }

            var promotable = promotableRows
                .GroupBy(row => ReadString(row, "edgeId"), StringComparer.Ordinal)
                .Select(static group => group.First())
                .Select(row => new Dictionary<string, object?>
                {
                    ["edgeId"] = ReadString(row, "edgeId"),
                    ["fromNodeId"] = ReadString(row, "fromNodeId"),
                    ["toNodeId"] = ReadString(row, "toNodeId"),
                })
                .ToArray();
            if (promotable.Length > 0)
            {
                parameters["promotable"] = promotable;
                // Only identities whose both endpoints exist as this owner's nodes are promoted;
                // the rest simply stay pending until their target node arrives.
                await RunAndConsumeAsync(
                    transaction,
                    Neo4jProjectionGraphStoreVersionedCypherSupport.BuildPromotePendingEdgesCypher(
                        _nodeLabel,
                        _edgeType,
                        _versionedEdgeIdentityLabel),
                    parameters,
                    ct);
            }
        }
        return null;
    }

    private async Task<bool> HasForeignIncidentRelationshipAsync(
        IAsyncTransaction transaction,
        ProjectionGraphDelta delta,
        Dictionary<string, object?> parameters,
        CancellationToken ct)
    {
        if (delta.DeleteNodeIds.Count == 0)
            return false;

        parameters["nodeIds"] = delta.DeleteNodeIds.ToArray();
        var rows = await RunRowsAsync(
            transaction,
            Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadForeignIncidentRelationshipsCypher(
                _nodeLabel,
                _edgeType),
            parameters,
            ct);
        if (rows.Count > 0)
            return true;

        var pendingRows = await RunRowsAsync(
            transaction,
            Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadForeignIncidentEdgeIdentitiesCypher(
                _versionedEdgeIdentityLabel),
            parameters,
            ct);
        return pendingRows.Count > 0;
    }

    private Dictionary<string, object?> ToNodeParameters(ProjectionGraphNodeMutation node) =>
        new(StringComparer.Ordinal)
        {
            ["nodeId"] = node.NodeId,
            ["nodeType"] = node.NodeType,
            ["mutationPayload"] = node.ToByteArray(),
            ["updatedAtEpochMs"] = node.UpdatedAtEpochMs,
            ["projectionGraphVersion"] = VersionedGraphSchemaVersion,
        };

    private Dictionary<string, object?> ToEdgeParameters(
        ProjectionGraphEdgeMutation edge,
        string status) =>
        new(StringComparer.Ordinal)
        {
            ["edgeId"] = edge.EdgeId,
            ["fromNodeId"] = edge.FromNodeId,
            ["toNodeId"] = edge.ToNodeId,
            ["relationType"] = edge.EdgeType,
            ["mutationPayload"] = edge.ToByteArray(),
            ["updatedAtEpochMs"] = edge.UpdatedAtEpochMs,
            ["status"] = status,
            ["projectionGraphVersion"] = VersionedGraphSchemaVersion,
        };

    private static Dictionary<string, object?> BuildRouteParameters(ProjectionGraphRouteFingerprint route) =>
        new(StringComparer.Ordinal)
        {
            ["physicalNamespace"] = route.PhysicalNamespace,
            ["ownerId"] = route.OwnerId,
        };

    private static StoredOwnerState? MapOwnerState(
        ProjectionGraphRouteFingerprint requestedRoute,
        IReadOnlyDictionary<string, object> row)
    {
        if (!ReadBool(row, "initialized"))
            return null;

        return new StoredOwnerState(
            MapStoredRoute(requestedRoute.PhysicalNamespace, requestedRoute.OwnerId, row),
            new ProjectionGraphSourceCoordinate
            {
                ActorId = ReadString(row, "sourceActorId"),
                StateVersion = ReadLong(row, "sourceVersion"),
                EventId = ReadString(row, "sourceEventId"),
            });
    }

    private static ProjectionGraphRouteFingerprint MapStoredRoute(
        string physicalNamespace,
        string ownerId,
        IReadOnlyDictionary<string, object> row) =>
        new()
        {
            ProjectionKind = ReadString(row, "projectionKind"),
            LogicalScope = ReadString(row, "logicalScope"),
            OwnerId = ownerId,
            PhysicalNamespace = physicalNamespace,
            RouteEpoch = ReadLong(row, "routeEpoch"),
            ContractId = ReadString(row, "contractId"),
            ContractVersion = ReadLong(row, "contractVersion"),
        };

    private static StoredEvent MapStoredEvent(
        string eventId,
        IReadOnlyDictionary<string, object> row) =>
        new(
            new ProjectionGraphSourceCoordinate
            {
                ActorId = ReadString(row, "sourceActorId"),
                StateVersion = ReadLong(row, "sourceVersion"),
                EventId = eventId,
            },
            ReadString(row, "deltaFingerprint"));

    private ProjectionGraphNodeMutation? MapSnapshotNode(IReadOnlyDictionary<string, object> row)
    {
        var nodeId = ReadString(row, "nodeId");
        var payload = ReadBytes(row, "mutationPayload");
        if (nodeId.Length == 0 || payload.Length == 0)
            return null;
        var node = ProjectionGraphNodeMutation.Parser.ParseFrom(payload);
        return string.Equals(node.NodeId, nodeId, StringComparison.Ordinal) ? node : null;
    }

    private ProjectionGraphEdgeMutation? MapSnapshotEdge(IReadOnlyDictionary<string, object> row)
    {
        var edgeId = ReadString(row, "edgeId");
        var payload = ReadBytes(row, "mutationPayload");
        if (edgeId.Length == 0 || payload.Length == 0)
            return null;
        var edge = ProjectionGraphEdgeMutation.Parser.ParseFrom(payload);
        return string.Equals(edge.EdgeId, edgeId, StringComparison.Ordinal) ? edge : null;
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> RunRowsAsync(
        IAsyncTransaction transaction,
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cursor = await transaction.RunAsync(cypher, parameters);
        var rows = await cursor.ToListAsync(record =>
            (IReadOnlyDictionary<string, object>)record.Values.ToDictionary(
                x => x.Key,
                x => x.Value,
                StringComparer.Ordinal));
        ct.ThrowIfCancellationRequested();
        return rows;
    }

    private static Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> RunRowsWhenIdentifiersPresentAsync(
        IAsyncTransaction transaction,
        IReadOnlyCollection<string> identifiers,
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        return identifiers.Count == 0
            ? Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object>>>([])
            : RunRowsAsync(transaction, cypher, parameters, ct);
    }

    private static async Task RunAndConsumeAsync(
        IAsyncTransaction transaction,
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cursor = await transaction.RunAsync(cypher, parameters);
        await cursor.ConsumeAsync();
        ct.ThrowIfCancellationRequested();
    }

    private static async Task<long> RunCountAsync(
        IAsyncTransaction transaction,
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        var rows = await RunRowsAsync(transaction, cypher, parameters, ct);
        return rows.Count == 0 ? 0 : ReadLong(rows.Single(), "appliedCount");
    }

    private static async Task<ProjectionGraphDeltaApplyResult> RollbackAsync(
        IAsyncTransaction transaction,
        ProjectionGraphDeltaApplyResult result)
    {
        await transaction.RollbackAsync();
        return result;
    }

    private static ProjectionGraphDeltaApplyResult DeltaResult(
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

    private static bool SourceEquals(
        ProjectionGraphSourceCoordinate left,
        ProjectionGraphSourceCoordinate right) =>
        string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal) &&
        left.StateVersion == right.StateVersion &&
        string.Equals(left.EventId, right.EventId, StringComparison.Ordinal);

    private static bool ReadBool(IReadOnlyDictionary<string, object> row, string key) =>
        row.TryGetValue(key, out var value) && Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private static long ReadLong(IReadOnlyDictionary<string, object> row, string key) =>
        row.TryGetValue(key, out var value)
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : 0;

    private static byte[] ReadBytes(IReadOnlyDictionary<string, object> row, string key)
    {
        if (!row.TryGetValue(key, out var value))
            return [];
        return value switch
        {
            byte[] bytes => bytes,
            ReadOnlyMemory<byte> memory => memory.ToArray(),
            IEnumerable<byte> bytes => bytes.ToArray(),
            _ => [],
        };
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object>> ReadMapList(
        IReadOnlyDictionary<string, object> row,
        string key)
    {
        if (!row.TryGetValue(key, out var value) || value is not IEnumerable<object> items)
            return [];

        return items
            .Where(x => x != null)
            .Select(ToReadOnlyMap)
            .Where(x => x != null)
            .Select(x => x!)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, object>? ToReadOnlyMap(object value)
    {
        if (value is IReadOnlyDictionary<string, object> readOnly)
            return readOnly;
        if (value is IDictionary<string, object> dictionary)
        {
            return new Dictionary<string, object>(dictionary, StringComparer.Ordinal);
        }

        return null;
    }

    private sealed record StoredOwnerState(
        ProjectionGraphRouteFingerprint Route,
        ProjectionGraphSourceCoordinate Source);

    private sealed record StoredEvent(
        ProjectionGraphSourceCoordinate Source,
        string DeltaFingerprint);

    private sealed record StoredElementOwner(string OwnerId, long SchemaVersion);

    private sealed record MutationRejection(
        ProjectionGraphDeltaApplyDisposition Disposition,
        string Detail);
}
