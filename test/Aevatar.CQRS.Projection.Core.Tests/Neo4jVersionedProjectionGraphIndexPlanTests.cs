using System.Text;
using Aevatar.CQRS.Projection.Providers.Neo4j.Stores;
using FluentAssertions;
using Neo4j.Driver;
using Neo4jValueExtensions = Neo4j.Driver.ValueExtensions;

namespace Aevatar.CQRS.Projection.Core.Tests;

/// <summary>
/// The repair/cutover stale-element read (<see cref="Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadOwnedElementIdsCypher"/>)
/// matches edge identities by (physicalNamespace, projectionOwnerId) alone. The store must create
/// an ONLINE range index with exactly that key and the planner must seek it for the edge-identity
/// branch instead of scanning the label or prefix-scanning a pending index.
/// </summary>
[Trait("Category", "ProviderIntegration")]
[Trait("Feature", "ProjectionProviders")]
public sealed class Neo4jVersionedProjectionGraphIndexPlanTests
{
    [Neo4jIntegrationFact]
    public async Task EnsureSchema_ShouldCreateOnlineEdgeIdentityOwnerIndexThatTheOwnedElementIdsReadSeeks()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var nodeLabel = $"ProjectionGraphPlan{suffix}";
        var edgeType = $"PROJECTION_PLAN_{suffix}";
        var edgeIdentityLabel = VersionedProjectionGraphStoreConformanceTests.EdgeIdentityLabel(nodeLabel);
        var indexName = VersionedProjectionGraphStoreConformanceTests.EdgeIdentityOwnerIndexName(nodeLabel);
        var options = VersionedProjectionGraphStoreConformanceTests.CreateNeo4jOptions(nodeLabel, edgeType);
        await using var driver = GraphDatabase.Driver(
            options.Uri,
            AuthTokens.Basic(options.Username, options.Password));
        var store = new Neo4jProjectionGraphStore(options);
        var route = new ProjectionGraphRouteFingerprint
        {
            ProjectionKind = "workflow-run-insight-graph",
            LogicalScope = "workflow-insights",
            OwnerId = $"owner-{suffix}",
            PhysicalNamespace = $"plan-{suffix}",
            RouteEpoch = 1,
            ContractId = "workflow-run-insight-graph-v2",
            ContractVersion = 2,
        };

        try
        {
            // Any store call runs EnsureSchemaAsync; a live and a pending edge also give the
            // edge-identity label rows of both statuses.
            var delta = new ProjectionGraphDelta
            {
                Route = route.Clone(),
                Source = new ProjectionGraphSourceCoordinate
                {
                    ActorId = $"actor-{suffix}",
                    StateVersion = 1,
                    EventId = "event-1",
                },
                Mode = ProjectionGraphDeltaMode.Normal,
            };
            delta.UpsertNodes.Add(new ProjectionGraphNodeMutation { NodeId = "root", NodeType = "Run" });
            delta.UpsertNodes.Add(new ProjectionGraphNodeMutation { NodeId = "next", NodeType = "Step" });
            delta.UpsertEdges.Add(new ProjectionGraphEdgeMutation
            {
                EdgeId = "root->next",
                FromNodeId = "root",
                ToNodeId = "next",
                EdgeType = "NEXT",
            });
            delta.UpsertPendingEdges.Add(new ProjectionGraphEdgeMutation
            {
                EdgeId = "next->late",
                FromNodeId = "next",
                ToNodeId = "late",
                EdgeType = "NEXT",
            });
            (await store.ApplyDeltaAsync(delta)).Disposition.Should()
                .Be(ProjectionGraphDeltaApplyDisposition.Applied);

            var index = await ReadIndexAsync(driver, options.Database, indexName);
            index.Should().NotBeNull($"EnsureSchemaAsync must create index '{indexName}'");
            index!.Type.Should().Be("RANGE");
            index.EntityType.Should().Be("NODE");
            index.State.Should().Be("ONLINE");
            index.LabelsOrTypes.Should().Equal(edgeIdentityLabel);
            index.Properties.Should().Equal("physicalNamespace", "projectionOwnerId");

            var plan = await ExplainAsync(
                driver,
                options.Database,
                Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadOwnedElementIdsCypher(
                    nodeLabel,
                    edgeIdentityLabel),
                new Dictionary<string, object?>
                {
                    ["physicalNamespace"] = route.PhysicalNamespace,
                    ["ownerId"] = route.OwnerId,
                });
            var operators = Flatten(plan).ToArray();
            var describedPlan = string.Join(" | ", operators.Select(Describe));

            var edgeIdentityOperators = operators
                .Where(item => Describe(item).Contains(edgeIdentityLabel, StringComparison.Ordinal))
                .ToArray();
            edgeIdentityOperators.Should().NotBeEmpty(
                "the plan must contain the edge-identity branch; the plan was {0}",
                describedPlan);
            edgeIdentityOperators.Should().Contain(
                item => item.OperatorType.Contains("NodeIndexSeek", StringComparison.Ordinal) &&
                        UsesEdgeIdentityOwnerIndex(item, indexName, edgeIdentityLabel),
                "the edge-identity branch must seek {0}; the plan was {1}",
                indexName,
                describedPlan);
            edgeIdentityOperators.Should().NotContain(
                item => item.OperatorType.Contains("NodeByLabelScan", StringComparison.Ordinal),
                "the edge-identity branch must not scan the label; the plan was {0}",
                describedPlan);
        }
        finally
        {
            await store.DisposeAsync();
            await VersionedProjectionGraphStoreConformanceTests.CleanupNeo4jAsync(
                driver,
                options.Database,
                nodeLabel,
                edgeType);
        }
    }

    /// <summary>
    /// Neo4j 5 describes an index seek in <c>Arguments["Details"]</c> as
    /// <c>RANGE INDEX identity:Label(physicalNamespace, projectionOwnerId) WHERE …</c>; older
    /// planners name the index instead. Either identifies the (namespace, owner) index: the
    /// pending indexes carry four properties, so their prefix seek renders a longer property list.
    /// </summary>
    private static bool UsesEdgeIdentityOwnerIndex(IPlan plan, string indexName, string edgeIdentityLabel)
    {
        var arguments = DescribeArguments(plan);
        return arguments.Contains(indexName, StringComparison.OrdinalIgnoreCase) ||
               arguments.Contains(
                   $"{edgeIdentityLabel}(physicalNamespace, projectionOwnerId)",
                   StringComparison.Ordinal);
    }

    private static async Task<IndexDescription?> ReadIndexAsync(IDriver driver, string database, string indexName)
    {
        await using var session = CreateSession(driver, database, AccessMode.Read);
        var cursor = await session.RunAsync(
            "SHOW INDEXES YIELD name, type, entityType, labelsOrTypes, properties, state " +
            "WHERE name = $indexName " +
            "RETURN name, type, entityType, labelsOrTypes, properties, state",
            new Dictionary<string, object?> { ["indexName"] = indexName });
        var rows = await cursor.ToListAsync(record => new IndexDescription(
            Neo4jValueExtensions.As<string>(record["name"]),
            Neo4jValueExtensions.As<string>(record["type"]),
            Neo4jValueExtensions.As<string>(record["entityType"]),
            Neo4jValueExtensions.As<List<string>>(record["labelsOrTypes"]),
            Neo4jValueExtensions.As<List<string>>(record["properties"]),
            Neo4jValueExtensions.As<string>(record["state"])));
        return rows.SingleOrDefault();
    }

    private static async Task<IPlan> ExplainAsync(
        IDriver driver,
        string database,
        string cypher,
        Dictionary<string, object?> parameters)
    {
        await using var session = CreateSession(driver, database, AccessMode.Read);
        var cursor = await session.RunAsync("EXPLAIN " + cypher, parameters);
        var summary = await cursor.ConsumeAsync();
        summary.HasPlan.Should().BeTrue("EXPLAIN must return the planner's plan");
        return summary.Plan;
    }

    private static IEnumerable<IPlan> Flatten(IPlan root)
    {
        yield return root;
        foreach (var child in root.Children)
        {
            foreach (var descendant in Flatten(child))
                yield return descendant;
        }
    }

    private static string Describe(IPlan plan) =>
        $"{plan.OperatorType}[{string.Join(", ", plan.Identifiers)}]{{{DescribeArguments(plan)}}}";

    private static string DescribeArguments(IPlan plan)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in plan.Arguments.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (builder.Length > 0)
                builder.Append("; ");
            builder.Append(key).Append('=').Append(DescribeValue(value));
        }

        return builder.ToString();
    }

    private static string DescribeValue(object? value) =>
        value switch
        {
            null => "null",
            string text => text,
            IDictionary<string, object> map => "{" + string.Join(", ", map.Select(x => $"{x.Key}={DescribeValue(x.Value)}")) + "}",
            IEnumerable<object> items => "[" + string.Join(", ", items.Select(DescribeValue)) + "]",
            _ => value.ToString() ?? string.Empty,
        };

    private static IAsyncSession CreateSession(IDriver driver, string database, AccessMode accessMode)
    {
        return driver.AsyncSession(options =>
        {
            options.WithDefaultAccessMode(accessMode);
            if (database.Length > 0)
                options.WithDatabase(database);
        });
    }

    private sealed record IndexDescription(
        string Name,
        string Type,
        string EntityType,
        List<string> LabelsOrTypes,
        List<string> Properties,
        string State);
}
