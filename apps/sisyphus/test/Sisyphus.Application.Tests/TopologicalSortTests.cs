using System.Text.Json;
using FluentAssertions;
using Sisyphus.Application.Models;
using Sisyphus.Application.Models.Graph;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Tests;

public class TopologicalSortTests
{
    [Fact]
    public void TopologicalSort_LinearDependency_ReturnsCorrectOrder()
    {
        // definition -> theorem -> proof (proof references theorem, theorem references definition)
        var snapshot = new BlueGraphSnapshot
        {
            Nodes =
            [
                MakeNode("def1", "definition"),
                MakeNode("thm1", "theorem"),
                MakeNode("prf1", "proof"),
            ],
            Edges =
            [
                MakeEdge("thm1", "def1", "references"),
                MakeEdge("prf1", "thm1", "proves"),
            ],
        };

        var sorted = PaperGeneratorService.TopologicalSort(snapshot);

        var ids = sorted.Select(n => n.Id).ToList();
        ids.IndexOf("def1").Should().BeLessThan(ids.IndexOf("thm1"));
        ids.IndexOf("thm1").Should().BeLessThan(ids.IndexOf("prf1"));
    }

    [Fact]
    public void TopologicalSort_NoEdges_MaintainsInput()
    {
        var snapshot = new BlueGraphSnapshot
        {
            Nodes =
            [
                MakeNode("a", "theorem"),
                MakeNode("b", "lemma"),
                MakeNode("c", "definition"),
            ],
            Edges = [],
        };

        var sorted = PaperGeneratorService.TopologicalSort(snapshot);
        sorted.Should().HaveCount(3);
    }

    [Fact]
    public void TopologicalSort_CycleDetected_FallsBackToTypeOrder()
    {
        // A -> B -> A (cycle)
        var snapshot = new BlueGraphSnapshot
        {
            Nodes =
            [
                MakeNode("a", "theorem"),
                MakeNode("b", "lemma"),
            ],
            Edges =
            [
                MakeEdge("a", "b", "references"),
                MakeEdge("b", "a", "references"),
            ],
        };

        var sorted = PaperGeneratorService.TopologicalSort(snapshot);

        // lemma (index 3) should come before theorem (index 5) in type-based fallback
        sorted.Should().HaveCount(2);
        var ids = sorted.Select(n => n.Id).ToList();
        ids[0].Should().Be("b"); // lemma
        ids[1].Should().Be("a"); // theorem
    }

    [Fact]
    public void TopologicalSort_EmptyGraph_ReturnsEmpty()
    {
        var snapshot = new BlueGraphSnapshot { Nodes = [], Edges = [] };
        var sorted = PaperGeneratorService.TopologicalSort(snapshot);
        sorted.Should().BeEmpty();
    }

    [Fact]
    public void TopologicalSort_SingleNode_ReturnsSame()
    {
        var snapshot = new BlueGraphSnapshot
        {
            Nodes = [MakeNode("only", "axiom")],
            Edges = [],
        };

        var sorted = PaperGeneratorService.TopologicalSort(snapshot);
        sorted.Should().HaveCount(1);
        sorted[0].Id.Should().Be("only");
    }

    private static GraphNode MakeNode(string id, string type) => new()
    {
        Id = id,
        Type = type,
        Properties = new Dictionary<string, JsonElement>
        {
            [SisyphusStatus.PropertyName] = JsonSerializer.SerializeToElement(SisyphusStatus.Purified),
            ["abstract"] = JsonSerializer.SerializeToElement($"Abstract for {id}"),
            ["body"] = JsonSerializer.SerializeToElement($"Body for {id}"),
        },
    };

    private static GraphEdge MakeEdge(string source, string target, string type) => new()
    {
        Id = $"{source}-{target}",
        Source = source,
        Target = target,
        Type = type,
        Properties = [],
    };
}
