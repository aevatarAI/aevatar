using System.Text.Json;
using FluentAssertions;
using Sisyphus.Application.Models;
using Sisyphus.Application.Models.Graph;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Tests;

public class LaTeXGeneratorTests
{
    [Fact]
    public void GenerateLatex_SingleTheorem_ProducesValidDocument()
    {
        var nodes = new List<GraphNode>
        {
            MakeNode("n1", "theorem", "A test theorem", "Let $x \\in \\mathbb{R}$. Then $x^2 \\geq 0$."),
        };

        var latex = PaperGeneratorService.GenerateLatex(nodes, []);

        latex.Should().Contain(@"\documentclass");
        latex.Should().Contain(@"\begin{document}");
        latex.Should().Contain(@"\end{document}");
        latex.Should().Contain(@"\begin{theorem}");
        latex.Should().Contain(@"\end{theorem}");
        latex.Should().Contain(@"\label{node:n1}");
    }

    [Fact]
    public void GenerateLatex_ProofUsesBuiltInEnvironment()
    {
        var nodes = new List<GraphNode>
        {
            MakeNode("p1", "proof", "A proof", "We proceed by contradiction."),
        };

        var latex = PaperGeneratorService.GenerateLatex(nodes, []);

        latex.Should().Contain(@"\begin{proof}");
        latex.Should().Contain(@"\end{proof}");
    }

    [Fact]
    public void GenerateLatex_AllValidTypes_HaveNewtheoremDeclarations()
    {
        var types = new[]
        {
            "theorem", "lemma", "definition", "corollary", "conjecture",
            "proposition", "remark", "conclusion", "example", "notation",
            "axiom", "observation", "note",
        };

        var latex = PaperGeneratorService.GenerateLatex([], []);

        foreach (var type in types)
        {
            latex.Should().Contain($@"\newtheorem{{{type}}}");
        }
    }

    [Fact]
    public void GenerateLatex_WithEdges_EmitsCrossReferences()
    {
        var nodes = new List<GraphNode>
        {
            MakeNode("def1", "definition", "A definition", "Body of def"),
            MakeNode("thm1", "theorem", "A theorem", "Body of theorem"),
        };
        var edges = new List<GraphEdge>
        {
            new()
            {
                Id = "e1", Source = "thm1", Target = "def1", Type = "references",
                Properties = [],
            },
        };

        var latex = PaperGeneratorService.GenerateLatex(nodes, edges);

        latex.Should().Contain(@"\ref{node:def1}");
    }

    [Fact]
    public void GenerateLatex_EmptyNodes_ProducesMinimalDocument()
    {
        var latex = PaperGeneratorService.GenerateLatex([], []);

        latex.Should().Contain(@"\documentclass");
        latex.Should().Contain(@"\begin{document}");
        latex.Should().Contain(@"\end{document}");
        latex.Should().Contain(@"\usepackage{amsmath,amssymb,amsthm}");
        latex.Should().Contain(@"\usepackage{hyperref}");
    }

    [Fact]
    public void GenerateLatex_IncludesHyperrefPackage()
    {
        var latex = PaperGeneratorService.GenerateLatex([], []);
        latex.Should().Contain(@"\usepackage{hyperref}");
    }

    [Fact]
    public void GenerateLatex_IncludesAmsthm()
    {
        var latex = PaperGeneratorService.GenerateLatex([], []);
        latex.Should().Contain(@"\usepackage{amsmath,amssymb,amsthm}");
    }

    [Fact]
    public void GenerateLatex_MultipleNodes_IncludesLabels()
    {
        var nodes = new List<GraphNode>
        {
            MakeNode("a", "axiom", "An axiom", "Axiom body"),
            MakeNode("b", "lemma", "A lemma", "Lemma body"),
        };

        var latex = PaperGeneratorService.GenerateLatex(nodes, []);

        latex.Should().Contain(@"\label{node:a}");
        latex.Should().Contain(@"\label{node:b}");
    }

    private static GraphNode MakeNode(string id, string type, string nodeAbstract, string body) => new()
    {
        Id = id,
        Type = type,
        Properties = new Dictionary<string, JsonElement>
        {
            ["abstract"] = JsonSerializer.SerializeToElement(nodeAbstract),
            ["body"] = JsonSerializer.SerializeToElement(body),
        },
    };
}
