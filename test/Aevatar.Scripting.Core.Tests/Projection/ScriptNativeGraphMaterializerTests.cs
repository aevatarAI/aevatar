using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptNativeGraphMaterializerTests
{
    [Fact]
    public void Materialize_ShouldCreateReadModel_AndRoundTripBackToGraphProjection()
    {
        var materializer = new ScriptNativeGraphMaterializer();
        var updatedAt = DateTimeOffset.Parse("2026-03-16T16:10:00+00:00");
        var nativeGraph = new ScriptNativeGraphProjection
        {
            SchemaId = "claim_case",
            SchemaVersion = "3",
            SchemaHash = "hash-graph",
            GraphScope = "scope-graph",
        };
        nativeGraph.NodeEntries.Add(new ScriptNativeGraphNodeProjection
        {
            NodeId = "node-1",
            NodeType = "owner",
            Properties = { ["kind"] = "primary" },
        });
        nativeGraph.NodeEntries.Add(new ScriptNativeGraphNodeProjection());
        nativeGraph.EdgeEntries.Add(new ScriptNativeGraphEdgeProjection
        {
            EdgeId = "edge-1",
            FromNodeId = "node-1",
            ToNodeId = "node-2",
            EdgeType = "relates_to",
            Properties = { ["weight"] = "1" },
        });
        nativeGraph.EdgeEntries.Add(new ScriptNativeGraphEdgeProjection());

        var readModel = materializer.Materialize(
            "actor-1",
            null!,
            null!,
            null!,
            new ScriptDomainFactCommitted { StateVersion = 9 },
            "evt-graph-1",
            updatedAt,
            nativeGraph);
        var graph = materializer.Materialize(readModel);

        readModel.Id.Should().Be("actor-1");
        readModel.ScriptId.Should().BeEmpty();
        readModel.DefinitionActorId.Should().BeEmpty();
        readModel.Revision.Should().BeEmpty();
        readModel.SchemaId.Should().Be("claim_case");
        readModel.GraphNodeEntries.Should().HaveCount(2);
        readModel.GraphEdgeEntries.Should().HaveCount(2);
        graph.Scope.Should().Be("scope-graph");
        graph.Nodes.Should().Contain(x => x.NodeId == "node-1" && x.NodeType == "owner");
        graph.Nodes.Should().Contain(x => x.NodeId == string.Empty && x.NodeType == string.Empty);
        graph.Edges.Should().Contain(x => x.EdgeId == "edge-1" && x.EdgeType == "relates_to");
        graph.Edges.Should().Contain(x => x.EdgeId == string.Empty && x.EdgeType == string.Empty);
    }

    [Fact]
    public void Materialize_ShouldValidateArguments()
    {
        var materializer = new ScriptNativeGraphMaterializer();
        var fact = new ScriptDomainFactCommitted { StateVersion = 1 };
        var nativeGraph = new ScriptNativeGraphProjection();
        var readModel = new ScriptNativeGraphReadModel();

        Action noActorId = () => materializer.Materialize(" ", "script-1", "definition-1", "rev-1", fact, "evt-1", DateTimeOffset.UtcNow, nativeGraph);
        Action noFact = () => materializer.Materialize("actor-1", "script-1", "definition-1", "rev-1", null!, "evt-1", DateTimeOffset.UtcNow, nativeGraph);
        Action noEventId = () => materializer.Materialize("actor-1", "script-1", "definition-1", "rev-1", fact, " ", DateTimeOffset.UtcNow, nativeGraph);
        Action noNativeGraph = () => materializer.Materialize("actor-1", "script-1", "definition-1", "rev-1", fact, "evt-1", DateTimeOffset.UtcNow, null!);
        Action noReadModel = () => materializer.Materialize((ScriptNativeGraphReadModel)null!);

        noActorId.Should().Throw<ArgumentException>();
        noFact.Should().Throw<ArgumentNullException>();
        noEventId.Should().Throw<ArgumentException>();
        noNativeGraph.Should().Throw<ArgumentNullException>();
        noReadModel.Should().Throw<ArgumentNullException>();
    }
}
