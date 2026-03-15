using Aevatar.Foundation.Projection.ReadModels;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionReportReadModelTests
{
    private static readonly WorkflowRunGraphMirrorMaterializer GraphMaterializer = new();

    [Fact]
    public void AddTimelineAndRoleReply_ShouldCopyProjectionPayloads()
    {
        var report = new WorkflowExecutionReport();

        report.AddTimeline(new ProjectionTimelineEvent
        {
            Timestamp = new DateTimeOffset(2026, 3, 11, 8, 0, 0, TimeSpan.Zero),
            Stage = "completed",
            Message = "done",
            AgentId = "agent-1",
            StepId = "step-1",
            StepType = "llm_call",
            EventType = "StepCompletedEvent",
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["branch"] = "approved",
            },
        });
        report.AddRoleReply(new ProjectionRoleReply
        {
            Timestamp = new DateTimeOffset(2026, 3, 11, 8, 1, 0, TimeSpan.Zero),
            RoleId = "assistant",
            SessionId = "session-1",
            Content = "hello",
            ContentLength = 5,
        });

        report.Timeline.Should().ContainSingle();
        report.Timeline[0].Stage.Should().Be("completed");
        report.Timeline[0].Data.Should().ContainKey("branch").WhoseValue.Should().Be("approved");
        report.RoleReplies.Should().ContainSingle();
        report.RoleReplies[0].RoleId.Should().Be("assistant");
        report.RoleReplies[0].ContentLength.Should().Be(5);
    }

    [Fact]
    public void GraphNodesAndEdges_ShouldIncludeRunStepAndTopologyActors()
    {
        var report = new WorkflowRunGraphMirrorReadModel
        {
            RootActorId = " actor-1 ",
            CommandId = " cmd-1 ",
            WorkflowName = "direct",
            Input = "hello",
            UpdatedAt = new DateTimeOffset(2026, 3, 11, 8, 30, 0, TimeSpan.Zero),
            Steps =
            [
                new WorkflowExecutionStepTrace
                {
                    StepId = "step-1",
                    StepType = "llm_call",
                    TargetRole = "assistant",
                    WorkerId = "worker-1",
                    Success = true,
                },
            ],
            Topology =
            [
                new WorkflowExecutionTopologyEdge("actor-1", "child-1"),
            ],
        };

        var graph = GraphMaterializer.Materialize(report);
        var nodes = graph.Nodes;
        var edges = graph.Edges;

        nodes.Should().Contain(x => x.NodeId == "actor-1" && x.NodeType == WorkflowExecutionGraphConstants.ActorNodeType);
        nodes.Should().Contain(x => x.NodeType == WorkflowExecutionGraphConstants.RunNodeType && x.Properties["input"] == "hello");
        nodes.Should().Contain(x => x.NodeType == WorkflowExecutionGraphConstants.StepNodeType && x.Properties["stepId"] == "step-1");
        nodes.Should().Contain(x => x.NodeId == "child-1" && x.NodeType == WorkflowExecutionGraphConstants.ActorNodeType);
        edges.Should().Contain(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeOwns);
        edges.Should().Contain(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeContainsStep && x.Properties["stepType"] == "llm_call");
        edges.Should().ContainSingle(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeChildOf);
        edges.Single(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeChildOf).FromNodeId.Should().Be("actor-1");
        edges.Single(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeChildOf).ToNodeId.Should().Be("child-1");
    }

    [Fact]
    public void GraphNodesAndEdges_ShouldNormalizeUnknownTokens_WhenIdentifiersMissing()
    {
        var report = new WorkflowRunGraphMirrorReadModel
        {
            RootActorId = " ",
            CommandId = string.Empty,
            WorkflowName = string.Empty,
            UpdatedAt = default,
            Steps =
            [
                new WorkflowExecutionStepTrace
                {
                    StepId = " ",
                },
            ],
            Topology =
            [
                new WorkflowExecutionTopologyEdge(" ", "child-1"),
            ],
        };

        var graph = GraphMaterializer.Materialize(report);
        var nodes = graph.Nodes;
        var edges = graph.Edges;

        nodes.Should().Contain(x => x.NodeId == "unknown");
        nodes.Should().Contain(x => x.NodeType == WorkflowExecutionGraphConstants.RunNodeType);
        nodes.Should().Contain(x => x.NodeType == WorkflowExecutionGraphConstants.StepNodeType && x.Properties["stepId"] == "unknown");
        edges.Should().Contain(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeOwns);
        edges.Should().Contain(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeContainsStep);
        edges.Should().ContainSingle(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeChildOf);
        edges.Single(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeChildOf).FromNodeId.Should().Be("unknown");
        edges.Single(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeChildOf).ToNodeId.Should().Be("child-1");
    }
}
