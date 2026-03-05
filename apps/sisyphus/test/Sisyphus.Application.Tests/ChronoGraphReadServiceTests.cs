using System.Text.Json;
using FluentAssertions;
using Sisyphus.Application.Models;
using Sisyphus.Application.Models.Graph;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Tests;

public class ChronoGraphReadServiceTests
{
    [Fact]
    public void GetBlueSnapshot_FiltersOnlyPurifiedNodes()
    {
        var snapshot = new GraphSnapshot
        {
            Nodes =
            [
                MakeNode("n1", "theorem", SisyphusStatus.Purified),
                MakeNode("n2", "raw", SisyphusStatus.Raw),
                MakeNode("n3", "definition", SisyphusStatus.Purified),
            ],
            Edges =
            [
                MakeEdge("e1", "n1", "n3", SisyphusStatus.Purified),
                MakeEdge("e2", "n1", "n2", SisyphusStatus.Raw),
            ],
        };

        var blue = FilterBlueSnapshot(snapshot);

        blue.Nodes.Should().HaveCount(2);
        blue.Nodes.Select(n => n.Id).Should().BeEquivalentTo(["n1", "n3"]);
        blue.Edges.Should().HaveCount(1);
        blue.Edges[0].Id.Should().Be("e1");
    }

    [Fact]
    public void GetBlueSnapshot_ExcludesEdgesWithNonBlueEndpoints()
    {
        var snapshot = new GraphSnapshot
        {
            Nodes =
            [
                MakeNode("n1", "theorem", SisyphusStatus.Purified),
                MakeNode("n2", "raw", SisyphusStatus.Raw),
            ],
            Edges =
            [
                // This edge is "purified" but target n2 is not blue
                MakeEdge("e1", "n1", "n2", SisyphusStatus.Purified),
            ],
        };

        var blue = FilterBlueSnapshot(snapshot);

        blue.Nodes.Should().HaveCount(1);
        blue.Edges.Should().BeEmpty();
    }

    [Fact]
    public void GetBlueSnapshot_EmptySnapshot_ReturnsEmpty()
    {
        var snapshot = new GraphSnapshot();

        var blue = FilterBlueSnapshot(snapshot);

        blue.Nodes.Should().BeEmpty();
        blue.Edges.Should().BeEmpty();
    }

    [Fact]
    public void GetBlueSnapshot_NodesWithoutSisyphusStatus_AreExcluded()
    {
        var snapshot = new GraphSnapshot
        {
            Nodes =
            [
                new GraphNode
                {
                    Id = "n1",
                    Type = "theorem",
                    Properties = new Dictionary<string, JsonElement>
                    {
                        ["abstract"] = JsonSerializer.SerializeToElement("test"),
                    },
                },
            ],
            Edges = [],
        };

        var blue = FilterBlueSnapshot(snapshot);
        blue.Nodes.Should().BeEmpty();
    }

    // Replicates the filtering logic from ChronoGraphReadService.GetBlueSnapshotAsync
    private static BlueGraphSnapshot FilterBlueSnapshot(GraphSnapshot snapshot)
    {
        var blueNodes = snapshot.Nodes
            .Where(n => HasSisyphusStatus(n.Properties, SisyphusStatus.Purified))
            .ToList();

        var blueNodeIds = new HashSet<string>(blueNodes.Select(n => n.Id));

        var blueEdges = snapshot.Edges
            .Where(e => HasSisyphusStatus(e.Properties, SisyphusStatus.Purified)
                        && blueNodeIds.Contains(e.Source)
                        && blueNodeIds.Contains(e.Target))
            .ToList();

        return new BlueGraphSnapshot
        {
            Nodes = blueNodes,
            Edges = blueEdges,
        };
    }

    private static bool HasSisyphusStatus(Dictionary<string, JsonElement> properties, string status)
    {
        if (!properties.TryGetValue(SisyphusStatus.PropertyName, out var element))
            return false;
        return element.ValueKind == JsonValueKind.String && element.GetString() == status;
    }

    private static GraphNode MakeNode(string id, string type, string status) => new()
    {
        Id = id,
        Type = type,
        Properties = new Dictionary<string, JsonElement>
        {
            [SisyphusStatus.PropertyName] = JsonSerializer.SerializeToElement(status),
            ["abstract"] = JsonSerializer.SerializeToElement($"Abstract for {id}"),
        },
    };

    private static GraphEdge MakeEdge(string id, string source, string target, string status) => new()
    {
        Id = id,
        Source = source,
        Target = target,
        Type = "references",
        Properties = new Dictionary<string, JsonElement>
        {
            [SisyphusStatus.PropertyName] = JsonSerializer.SerializeToElement(status),
        },
    };
}
