using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Models;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowGraphMapperTests
{
    private readonly WorkflowGraphMapper _mapper = new();

    [Fact]
    public void Map_ShouldReturnEmptyGraph_WhenNoSteps()
    {
        var doc = new WorkflowDocument { Name = "wf" };
        var graph = _mapper.Map(doc);
        graph.Nodes.Should().BeEmpty();
        graph.Edges.Should().BeEmpty();
    }

    [Fact]
    public void Map_ShouldCreateNodeForEachStep()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel { Id = "s1", Type = "transform" },
                new StepModel { Id = "s2", Type = "llm_call" },
            ],
        };
        var graph = _mapper.Map(doc);
        graph.Nodes.Should().HaveCount(2);
        graph.Nodes.Should().Contain(n => n.Id == "s1");
        graph.Nodes.Should().Contain(n => n.Id == "s2");
    }

    [Fact]
    public void Map_ShouldCreateNextEdge()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel { Id = "s1", Type = "transform", Next = "s2" },
                new StepModel { Id = "s2", Type = "transform" },
            ],
        };
        var graph = _mapper.Map(doc);
        graph.Edges.Should().Contain(e => e.Source == "s1" && e.Target == "s2" && e.Kind == "next");
    }

    [Fact]
    public void Map_ShouldCreateBranchEdges()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel
                {
                    Id = "cond", Type = "conditional",
                    Branches = new Dictionary<string, string> { ["true"] = "s1", ["false"] = "s2" },
                },
                new StepModel { Id = "s1", Type = "transform" },
                new StepModel { Id = "s2", Type = "transform" },
            ],
        };
        var graph = _mapper.Map(doc);
        graph.Edges.Should().Contain(e => e.Source == "cond" && e.Target == "s1" && e.Kind == "branch" && e.Label == "true");
        graph.Edges.Should().Contain(e => e.Source == "cond" && e.Target == "s2" && e.Kind == "branch" && e.Label == "false");
    }

    [Fact]
    public void Map_ShouldCreateChildEdges()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel
                {
                    Id = "parent", Type = "foreach",
                    Children = [new StepModel { Id = "child", Type = "transform" }],
                },
            ],
        };
        var graph = _mapper.Map(doc);
        graph.Nodes.Should().HaveCount(2);
        graph.Edges.Should().Contain(e => e.Source == "parent" && e.Target == "child" && e.Kind == "child");
    }

    [Fact]
    public void Map_ShouldSetHasChildren()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel
                {
                    Id = "parent", Type = "foreach",
                    Children = [new StepModel { Id = "child", Type = "transform" }],
                },
            ],
        };
        var graph = _mapper.Map(doc);
        graph.Nodes.Should().Contain(n => n.Id == "parent" && n.HasChildren);
    }

    [Fact]
    public void Map_ShouldSetIsImportOnlyType_ForActorSend()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel { Id = "s1", Type = "actor_send" }],
        };
        var graph = _mapper.Map(doc);
        graph.Nodes.Should().Contain(n => n.Id == "s1" && n.IsImportOnlyType);
    }

    [Fact]
    public void Map_ShouldCanonicalizeNodeType()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel { Id = "s1", Type = "loop" }],
        };
        var graph = _mapper.Map(doc);
        graph.Nodes.Should().Contain(n => n.Id == "s1" && n.Type == "while");
    }

    [Fact]
    public void Map_ShouldTrackTargetRole()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel { Id = "s1", Type = "llm_call", TargetRole = "agent" }],
        };
        var graph = _mapper.Map(doc);
        graph.Nodes.Should().Contain(n => n.Id == "s1" && n.TargetRole == "agent");
    }
}
