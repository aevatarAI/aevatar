using System.Collections;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptProjectionReadModelSupportCoverageTests
{
    [Fact]
    public void ToStruct_AndToDictionary_ShouldRoundTripRichObjectGraph()
    {
        var now = DateTimeOffset.Parse("2026-03-16T12:00:00+00:00");
        var model = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["text"] = "hello",
            ["flag"] = true,
            ["byte"] = (byte)1,
            ["sbyte"] = (sbyte)2,
            ["short"] = (short)3,
            ["ushort"] = (ushort)4,
            ["int"] = 5,
            ["uint"] = (uint)6,
            ["long"] = 7L,
            ["ulong"] = 8UL,
            ["float"] = 9.5f,
            ["double"] = 10.5d,
            ["decimal"] = 11.5m,
            ["timestamp"] = now,
            ["readonly"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["inner"] = "value",
            },
            ["mutable"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["count"] = 12L,
            },
            ["list"] = new List<object?> { "alpha", 13L, false },
            ["legacy"] = new ArrayList { "beta", 14L, true },
            ["fallback"] = new DisplayOnlyValue(),
            ["null"] = null,
        };

        var restored = ScriptProjectionReadModelSupport.ToDictionary(
            ScriptProjectionReadModelSupport.ToStruct(model));

        restored["text"].Should().Be("hello");
        restored["flag"].Should().Be(true);
        restored["byte"].Should().Be(1L);
        restored["sbyte"].Should().Be(2L);
        restored["short"].Should().Be(3L);
        restored["ushort"].Should().Be(4L);
        restored["int"].Should().Be(5L);
        restored["uint"].Should().Be(6L);
        restored["long"].Should().Be(7L);
        restored["ulong"].Should().Be(8L);
        restored["float"].Should().Be(9.5d);
        restored["double"].Should().Be(10.5d);
        restored["decimal"].Should().Be(11.5d);
        restored["timestamp"].Should().Be(now.ToString("O"));
        restored["readonly"].Should().BeAssignableTo<IDictionary<string, object?>>();
        restored["mutable"].Should().BeAssignableTo<IDictionary<string, object?>>();
        restored["list"].Should().BeAssignableTo<IReadOnlyList<object?>>();
        restored["legacy"].Should().BeAssignableTo<IReadOnlyList<object?>>();
        restored["fallback"].Should().Be("display-only");
        restored["null"].Should().BeNull();
        ScriptProjectionReadModelSupport.ToStruct(null).Fields.Should().BeEmpty();
        ScriptProjectionReadModelSupport.ToDictionary(null).Should().BeEmpty();
    }

    [Fact]
    public void CloneObjectGraph_ShouldCloneCollections_AndPreserveScalarValues()
    {
        var now = DateTimeOffset.Parse("2026-03-16T15:00:00+00:00");
        var source = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["text"] = "hello",
            ["flag"] = true,
            ["timestamp"] = now,
            ["readonly"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = 3L,
            },
            ["mutable"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = "nested",
            },
            ["list"] = new List<object?> { "alpha", 4L },
            ["legacy"] = new ArrayList { "beta", 5L },
            ["custom"] = new DisplayOnlyValue(),
        };

        var cloned = ScriptProjectionReadModelSupport.CloneObjectGraph(source);

        var clonedMap = cloned.Should().BeAssignableTo<IDictionary<string, object?>>().Subject;
        clonedMap.Should().NotBeSameAs(source);
        clonedMap["text"].Should().Be("hello");
        clonedMap["flag"].Should().Be(true);
        clonedMap["timestamp"].Should().Be(now);
        clonedMap["readonly"].Should().BeAssignableTo<IDictionary<string, object?>>();
        clonedMap["mutable"].Should().BeAssignableTo<IDictionary<string, object?>>();
        clonedMap["list"].Should().BeAssignableTo<IReadOnlyList<object?>>();
        clonedMap["legacy"].Should().BeAssignableTo<IReadOnlyList<object?>>();
        clonedMap["custom"].Should().BeSameAs(source["custom"]);
    }

    [Fact]
    public void ReadJsonValue_ShouldHandleAllSupportedKinds()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """
            {
              "object": { "nested": 1 },
              "array": [1, 2.5, "x"],
              "date": "2026-03-16T00:00:00+00:00",
              "text": "hello",
              "int": 42,
              "double": 42.5,
              "true": true,
              "false": false,
              "null": null
            }
            """);
        var root = document.RootElement;

        ScriptProjectionReadModelSupport.ReadJsonValue(root.GetProperty("object"))
            .Should().BeAssignableTo<IDictionary<string, object?>>();
        ScriptProjectionReadModelSupport.ReadJsonValue(root.GetProperty("array"))
            .Should().BeAssignableTo<IReadOnlyList<object?>>();
        ScriptProjectionReadModelSupport.ReadJsonValue(root.GetProperty("date"))
            .Should().Be(DateTimeOffset.Parse("2026-03-16T00:00:00+00:00"));
        ScriptProjectionReadModelSupport.ReadJsonValue(root.GetProperty("text")).Should().Be("hello");
        ScriptProjectionReadModelSupport.ReadJsonValue(root.GetProperty("int")).Should().Be(42L);
        ScriptProjectionReadModelSupport.ReadJsonValue(root.GetProperty("double")).Should().Be(42.5d);
        ScriptProjectionReadModelSupport.ReadJsonValue(root.GetProperty("true")).Should().Be(true);
        ScriptProjectionReadModelSupport.ReadJsonValue(root.GetProperty("false")).Should().Be(false);
        ScriptProjectionReadModelSupport.ReadJsonValue(root.GetProperty("null")).Should().BeNull();
        ScriptProjectionReadModelSupport.ReadJsonValue(default).Should().BeNull();
    }

    [Fact]
    public void GraphRecordConversions_ShouldRoundTripNodeAndEdge()
    {
        var updatedAt = DateTimeOffset.Parse("2026-03-16T18:00:00+00:00");
        var node = new ProjectionGraphNode
        {
            Scope = "scope-1",
            NodeId = "node-1",
            NodeType = "type-1",
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["key"] = "value",
            },
            UpdatedAt = updatedAt,
        };
        var edge = new ProjectionGraphEdge
        {
            Scope = "scope-1",
            EdgeId = "edge-1",
            FromNodeId = "node-1",
            ToNodeId = "node-2",
            EdgeType = "relates_to",
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["weight"] = "1",
            },
            UpdatedAt = updatedAt,
        };

        var nodeRecord = ScriptProjectionReadModelSupport.ToGraphNodeRecord(node);
        var restoredNode = ScriptProjectionReadModelSupport.ToProjectionGraphNode(nodeRecord);
        var edgeRecord = ScriptProjectionReadModelSupport.ToGraphEdgeRecord(edge);
        var restoredEdge = ScriptProjectionReadModelSupport.ToProjectionGraphEdge(edgeRecord);

        restoredNode.Should().BeEquivalentTo(node);
        restoredEdge.Should().BeEquivalentTo(edge);
    }

    [Fact]
    public void ReplaceCollection_ShouldClearExistingEntries_WhenSourceIsNull_AndReplaceWhenProvided()
    {
        var target = new RepeatedField<string> { "old-1", "old-2" };

        ScriptProjectionReadModelSupport.ReplaceCollection(target, null);
        target.Should().BeEmpty();

        ScriptProjectionReadModelSupport.ReplaceCollection(target, ["new-1", "new-2"]);
        target.Should().Equal("new-1", "new-2");
    }

    private sealed class DisplayOnlyValue
    {
        public override string ToString() => "display-only";
    }
}
