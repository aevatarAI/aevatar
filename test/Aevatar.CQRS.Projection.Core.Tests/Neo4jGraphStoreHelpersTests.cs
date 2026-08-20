using System.Reflection;
using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class Neo4jGraphStoreHelpersTests
{
    private static readonly Assembly Neo4jAssembly = typeof(Neo4jProjectionServiceCollectionExtensions).Assembly;

    [Fact]
    public void Neo4jProjectionGraphStoreOptions_ShouldExposeExpectedDefaults()
    {
        var options = new Neo4jProjectionGraphStoreOptions();

        options.Uri.Should().Be("bolt://localhost:7687");
        options.Username.Should().Be("neo4j");
        options.Password.Should().Be("");
        options.Database.Should().Be("");
        options.RequestTimeoutMs.Should().Be(5000);
        options.AutoCreateSchema.Should().BeTrue();
        options.NodeLabel.Should().Be("ProjectionGraphNode");
        options.EdgeType.Should().Be("PROJECTION_REL");
        options.MaxTraversalDepth.Should().Be(4);
    }

    [Fact]
    public void CypherSupport_ShouldBuildExpectedCypherForAllDirections()
    {
        var type = GetStoreType("Neo4jProjectionGraphStoreCypherSupport");

        var upsertNode = (string)Invoke(type, "BuildUpsertNodeCypher", "NodeLabel")!;
        var upsertEdge = (string)Invoke(type, "BuildUpsertEdgeCypher", "NodeLabel", "EDGE_TYPE")!;
        var deleteNode = (string)Invoke(type, "BuildDeleteNodeCypher", "NodeLabel")!;
        var deleteEdge = (string)Invoke(type, "BuildDeleteEdgeCypher", "EDGE_TYPE")!;
        var listNodes = (string)Invoke(type, "BuildListNodesByOwnerCypher", "NodeLabel")!;
        var listEdges = (string)Invoke(type, "BuildListEdgesByOwnerCypher", "EDGE_TYPE")!;
        var neighborsOut = (string)Invoke(type, "BuildNeighborCypher", "NodeLabel", "EDGE_TYPE", ProjectionGraphDirection.Outbound)!;
        var neighborsIn = (string)Invoke(type, "BuildNeighborCypher", "NodeLabel", "EDGE_TYPE", ProjectionGraphDirection.Inbound)!;
        var neighborsBoth = (string)Invoke(type, "BuildNeighborCypher", "NodeLabel", "EDGE_TYPE", ProjectionGraphDirection.Both)!;
        var subgraphOut = (string)Invoke(type, "BuildSubgraphEdgesCypher", "NodeLabel", "EDGE_TYPE", ProjectionGraphDirection.Outbound, 3)!;
        var subgraphIn = (string)Invoke(type, "BuildSubgraphEdgesCypher", "NodeLabel", "EDGE_TYPE", ProjectionGraphDirection.Inbound, 3)!;
        var subgraphBoth = (string)Invoke(type, "BuildSubgraphEdgesCypher", "NodeLabel", "EDGE_TYPE", ProjectionGraphDirection.Both, 3)!;
        var getNodes = (string)Invoke(type, "BuildGetNodesByIdsCypher", "NodeLabel")!;
        var createConstraint = (string)Invoke(type, "BuildCreateNodeConstraintCypher", "NodeLabel", "constraint_name")!;
        var createNodeOwnerIndex = (string)Invoke(
            type,
            "BuildCreateNodeOwnerIndexCypher",
            "NodeLabel",
            "node_owner_index")!;
        var createRelationshipOwnerIndex = (string)Invoke(
            type,
            "BuildCreateRelationshipOwnerIndexCypher",
            "EDGE_TYPE",
            "relationship_owner_index")!;
        var awaitIndex = (string)Invoke(type, "BuildAwaitIndexCypher")!;
        var showIndexes = (string)Invoke(type, "BuildShowIndexesCypher")!;
        var showIndexByName = (string)Invoke(type, "BuildShowIndexByNameCypher")!;

        upsertNode.Should().Contain("MERGE (n:NodeLabel");
        upsertNode.Should().Contain("projectionOwnerId");
        upsertEdge.Should().Contain("MERGE (from:NodeLabel");
        upsertEdge.Should().Contain("MERGE (from)-[r:EDGE_TYPE");
        deleteNode.Should().Contain("WHERE NOT (n)-[]-()");
        deleteEdge.Should().Contain("MATCH ()-[r:EDGE_TYPE");
        listNodes.Should().Contain("n.projectionOwnerId = $ownerId");
        listEdges.Should().Contain("r.projectionOwnerId = $ownerId");
        neighborsOut.Should().Contain("(root:NodeLabel {scope: $scope, nodeId: $rootNodeId})-[r:EDGE_TYPE]->");
        neighborsIn.Should().Contain("(root:NodeLabel {scope: $scope, nodeId: $rootNodeId})<-[r:EDGE_TYPE]-");
        neighborsBoth.Should().Contain("(root:NodeLabel {scope: $scope, nodeId: $rootNodeId})-[r:EDGE_TYPE]-");
        subgraphOut.Should().Contain("(root)-[:EDGE_TYPE*1..3]->()");
        subgraphIn.Should().Contain("(root)<-[:EDGE_TYPE*1..3]-()");
        subgraphBoth.Should().Contain("(root)-[:EDGE_TYPE*1..3]-()");
        getNodes.Should().Contain("WHERE n.nodeId IN $nodeIds");
        createConstraint.Should().Contain("CREATE CONSTRAINT constraint_name IF NOT EXISTS");
        createNodeOwnerIndex.Should().Be(
            "CREATE RANGE INDEX node_owner_index IF NOT EXISTS " +
            "FOR (n:NodeLabel) ON (n.scope, n.projectionOwnerId)");
        createRelationshipOwnerIndex.Should().Be(
            "CREATE RANGE INDEX relationship_owner_index IF NOT EXISTS " +
            "FOR ()-[r:EDGE_TYPE]-() ON (r.scope, r.projectionOwnerId)");
        awaitIndex.Should().Be("CALL db.awaitIndex($indexName, $timeoutSeconds)");
        showIndexes.Should().Contain(
            "YIELD name, type, entityType, labelsOrTypes, properties, state, failureMessage");
        showIndexByName.Should().Contain("WHERE name = $indexName");
    }

    [Fact]
    public void CypherSupport_ReplaceOwnerGraph_ShouldCollapseDeletedEdgesBeforeWritingReplacement()
    {
        var type = GetStoreType("Neo4jProjectionGraphStoreCypherSupport");

        var cypher = (string)Invoke(
            type,
            "BuildReplaceOwnerGraphCypher",
            "NodeLabel",
            "EDGE_TYPE")!;

        const string expectedBarrier =
            "DELETE old " +
            "WITH count(old) AS deletedEdgeCount " +
            "WITH $scope AS scope, $ownerId AS ownerId, $nodes AS nodes, $edges AS edges, $targetNodeIds AS targetNodeIds " +
            "FOREACH (node IN nodes";
        cypher.Should().Contain(expectedBarrier);
        cypher.Should().NotContain("DISTINCT $nodes");
        cypher.Should().NotContain("DISTINCT nodes");
        cypher.Should().Contain("WITH edges, targetNodeIds, scope, ownerId FOREACH (edge IN edges");
        cypher.Should().Contain("WITH targetNodeIds, scope, ownerId MATCH (n:NodeLabel");
    }

    [Fact]
    public void NormalizationSupport_ShouldNormalizeValuesAndManagedMarkers()
    {
        var type = GetStoreType("Neo4jProjectionGraphStoreNormalizationSupport");

        var defaultTimestamp = (long)Invoke(type, "NormalizeTimestamp", default(DateTimeOffset))!;
        var concreteTimestamp = (long)Invoke(type, "NormalizeTimestamp", new DateTimeOffset(2026, 2, 26, 0, 0, 0, TimeSpan.Zero))!;
        var fromNegative = (DateTimeOffset)Invoke(type, "FromUnixTimeMilliseconds", -7L)!;
        var token = (string)Invoke(type, "NormalizeToken", "  abc  ")!;
        var nullToken = (string)Invoke(type, "NormalizeToken", (string?)null)!;
        var edgeTypes = (string[])Invoke(type, "NormalizeEdgeTypes", (IReadOnlyList<string>)[" LINK ", "", "USES", "LINK"])!;
        var managed = (bool)Invoke(type, "ResolveProjectionManaged", (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProjectionGraphManagedPropertyKeys.ManagedMarkerKey] = " true ",
        })!;
        var notManaged = (bool)Invoke(type, "ResolveProjectionManaged", (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProjectionGraphManagedPropertyKeys.ManagedMarkerKey] = " false ",
        })!;
        var owner = (string)Invoke(type, "ResolveProjectionOwnerId", (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey] = " owner-1 ",
        })!;
        var missingOwner = (string)Invoke(type, "ResolveProjectionOwnerId", (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal))!;
        var labelFallback = (string)Invoke(type, "NormalizeLabel", "  ", "Fallback_1")!;
        var labelDigits = (string)Invoke(type, "NormalizeLabel", " 123 bad-label ", "fallback")!;
        var schemaFallback = (string)Invoke(type, "NormalizeSchemaName", "")!;
        var schemaDigits = (string)Invoke(type, "NormalizeSchemaName", "123-ABC")!;

        defaultTimestamp.Should().BeGreaterThan(0);
        concreteTimestamp.Should().Be(new DateTimeOffset(2026, 2, 26, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        fromNegative.Should().Be(DateTimeOffset.UnixEpoch);
        token.Should().Be("abc");
        nullToken.Should().Be("");
        edgeTypes.Should().Equal("LINK", "USES");
        managed.Should().BeTrue();
        notManaged.Should().BeFalse();
        owner.Should().Be("owner-1");
        missingOwner.Should().Be("");
        labelFallback.Should().Be("Fallback_1");
        labelDigits.Should().Be("N_123_bad_label");
        schemaFallback.Should().Be("projection_graph_schema");
        schemaDigits.Should().Be("s_123_abc");
    }

    [Fact]
    public async Task SchemaWaitSupport_WhenCancelledBetweenCycles_ShouldStopBeforeAnotherWait()
    {
        var type = GetStoreType("Neo4jProjectionGraphStoreSchemaWaitSupport");
        using var cts = new CancellationTokenSource();
        var cycles = 0;
        var observedCycleSeconds = 0L;
        Func<long, CancellationToken, Task<bool>> tryWaitCycle = (cycleSeconds, _) =>
        {
            cycles++;
            observedCycleSeconds = cycleSeconds;
            cts.Cancel();
            return Task.FromResult(false);
        };

        var waitTask = (Task)Invoke(
            type,
            "WaitAsync",
            tryWaitCycle,
            TimeSpan.FromMinutes(5),
            5L,
            TimeProvider.System,
            cts.Token)!;

        Func<Task> act = () => waitTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
        cycles.Should().Be(1);
        observedCycleSeconds.Should().Be(5);
    }

    [Fact]
    public void SchemaWaitSupport_ShouldRetryOnlyExplicitAwaitTimeouts()
    {
        var type = GetStoreType("Neo4jProjectionGraphStore");
        var procedureCode = "Neo.ClientError.Procedure.ProcedureCallFailed";
        var timeout = new ClientException(
            procedureCode,
            "Index 'owner_idx' did not come online within 5 seconds.");
        var missing = new ClientException(
            procedureCode,
            "There is no index with the name 'owner_idx'.");
        var failed = new ClientException(
            "Neo.ClientError.Schema.IndexNotFound",
            "Index 'owner_idx' did not come online within 5 seconds.");

        var retryTimeout = (bool)Invoke(type, "IsIndexAwaitTimeout", timeout)!;
        var retryMissing = (bool)Invoke(type, "IsIndexAwaitTimeout", missing)!;
        var retryFailed = (bool)Invoke(type, "IsIndexAwaitTimeout", failed)!;

        retryTimeout.Should().BeTrue();
        retryMissing.Should().BeFalse();
        retryFailed.Should().BeFalse();
    }

    [Fact]
    public void PropertyCodec_ShouldSerializeDeserializeAndHandleInvalidPayload()
    {
        var type = GetStoreType("Neo4jProjectionGraphStorePropertyCodec");
        var jsonOptions = new JsonSerializerOptions();
        var logger = NullLogger.Instance;

        var emptyJson = (string)Invoke(type, "SerializeProperties", (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal), jsonOptions)!;
        var fullJson = (string)Invoke(type, "SerializeProperties", (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["k"] = "v",
        }, jsonOptions)!;
        var parsed = (Dictionary<string, string>)Invoke(type, "DeserializeProperties", fullJson, jsonOptions, logger, "neo4j-provider")!;
        var blankPayload = (Dictionary<string, string>)Invoke(type, "DeserializeProperties", " ", jsonOptions, logger, "neo4j-provider")!;
        var invalidPayload = (Dictionary<string, string>)Invoke(type, "DeserializeProperties", "{not-json}", jsonOptions, logger, "neo4j-provider")!;

        emptyJson.Should().Be("{}");
        fullJson.Should().Contain("\"k\":\"v\"");
        parsed.Should().ContainKey("k").WhoseValue.Should().Be("v");
        blankPayload.Should().BeEmpty();
        invalidPayload.Should().BeEmpty();
    }

    [Fact]
    public void RowMapper_ShouldMapNodesAndEdgesAndHandleMissingFields()
    {
        var type = GetStoreType("Neo4jProjectionGraphStoreRowMapper");
        Func<string, Dictionary<string, string>> deserialize = payload => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["payload"] = payload,
        };

        var edgeRow = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["edgeId"] = " edge-1 ",
            ["fromNodeId"] = " from-1 ",
            ["toNodeId"] = " to-1 ",
            ["relationType"] = " LINK ",
            ["propertiesJson"] = "{\"a\":\"b\"}",
            ["updatedAtEpochMs"] = 1700000000000L,
        };
        var nodeRow = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["nodeId"] = " node-1 ",
            ["nodeType"] = " Actor ",
            ["propertiesJson"] = "{\"x\":\"y\"}",
            ["updatedAtEpochMs"] = 1700000000000L,
        };
        var edge = (ProjectionGraphEdge?)Invoke(type, "MapEdge", "scope-1", edgeRow, deserialize);
        var node = (ProjectionGraphNode?)Invoke(type, "MapNode", "scope-1", nodeRow, deserialize);

        edge.Should().NotBeNull();
        edge!.Scope.Should().Be("scope-1");
        edge.EdgeId.Should().Be("edge-1");
        edge.FromNodeId.Should().Be("from-1");
        edge.ToNodeId.Should().Be("to-1");
        edge.EdgeType.Should().Be("LINK");
        edge.Properties.Should().ContainKey("payload").WhoseValue.Should().Be("{\"a\":\"b\"}");
        edge.UpdatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000L));

        node.Should().NotBeNull();
        node!.Scope.Should().Be("scope-1");
        node.NodeId.Should().Be("node-1");
        node.NodeType.Should().Be("Actor");
        node.Properties.Should().ContainKey("payload").WhoseValue.Should().Be("{\"x\":\"y\"}");
        node.UpdatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000L));

        var edgeWithoutRelationType = (ProjectionGraphEdge?)Invoke(type, "MapEdge", "scope-1", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["edgeId"] = "edge-2",
            ["fromNodeId"] = "from-2",
            ["toNodeId"] = "to-2",
        }, deserialize);
        edgeWithoutRelationType.Should().NotBeNull();
        edgeWithoutRelationType!.EdgeType.Should().Be("Unknown");
        edgeWithoutRelationType.Properties.Should().ContainKey("payload").WhoseValue.Should().Be("{}");
        edgeWithoutRelationType.UpdatedAt.Should().Be(DateTimeOffset.UnixEpoch);

        var nodeWithoutNodeType = (ProjectionGraphNode?)Invoke(type, "MapNode", "scope-1", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["nodeId"] = "node-2",
        }, deserialize);
        nodeWithoutNodeType.Should().NotBeNull();
        nodeWithoutNodeType!.NodeType.Should().Be("Unknown");
        nodeWithoutNodeType.Properties.Should().ContainKey("payload").WhoseValue.Should().Be("{}");
        nodeWithoutNodeType.UpdatedAt.Should().Be(DateTimeOffset.UnixEpoch);

        var missingEdgeId = (ProjectionGraphEdge?)Invoke(type, "MapEdge", "scope-1", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["fromNodeId"] = "from",
            ["toNodeId"] = "to",
        }, deserialize);
        var emptyNodeId = (ProjectionGraphNode?)Invoke(type, "MapNode", "scope-1", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["nodeId"] = "   ",
        }, deserialize);

        missingEdgeId.Should().BeNull();
        emptyNodeId.Should().BeNull();
    }

    private static Type GetStoreType(string typeName)
    {
        return Neo4jAssembly.GetType($"Aevatar.CQRS.Projection.Providers.Neo4j.Stores.{typeName}", throwOnError: true)!;
    }

    private static object? Invoke(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull($"method {methodName} should exist on {type.FullName}");
        return method!.Invoke(null, args);
    }
}
