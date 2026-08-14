using Aevatar.Foundation.Projection.ReadModels;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowRunInsightReportDocumentReadModelTests
{
    private static readonly WorkflowRunInsightReportGraphMaterializer GraphMaterializer = new();

    [Fact]
    public void AddTimelineAndRoleReply_ShouldCopyProjectionPayloads()
    {
        var report = new WorkflowRunInsightReportDocument();

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
        var report = new WorkflowRunInsightReportDocument
        {
            RootActorId = " actor-1 ",
            CommandId = " cmd-1 ",
            WorkflowName = "direct",
            StateVersion = 12,
            Input = "hello",
            UpdatedAt = new DateTimeOffset(2026, 3, 11, 8, 30, 0, TimeSpan.Zero),
            Steps =
            [
                new WorkflowExecutionStepTrace
                {
                    StepId = "step-1",
                    DisplayName = "Draft response",
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
        nodes.Should().Contain(x =>
            x.NodeType == WorkflowExecutionGraphConstants.RunNodeType &&
            x.Properties["input"] == "hello" &&
            x.Properties[WorkflowExecutionGraphConstants.SourceStateVersionPropertyKey] == "12");
        nodes.Should().Contain(x =>
            x.NodeType == WorkflowExecutionGraphConstants.StepNodeType &&
            x.Properties["stepId"] == "step-1" &&
            x.Properties["displayName"] == "Draft response");
        nodes.Should().Contain(x =>
            x.NodeId == "actor:actor-1:cmd-1:child-1" &&
            x.NodeType == WorkflowExecutionGraphConstants.ActorNodeType &&
            x.Properties["actorId"] == "child-1");
        edges.Should().Contain(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeOwns);
        edges.Should().Contain(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeContainsStep && x.Properties["stepType"] == "llm_call");
        nodes.Where(x => x.NodeType != WorkflowExecutionGraphConstants.RunNodeType)
            .Should().OnlyContain(x => !x.Properties.ContainsKey(WorkflowExecutionGraphConstants.SourceStateVersionPropertyKey));
        edges.Should().OnlyContain(x => !x.Properties.ContainsKey(WorkflowExecutionGraphConstants.SourceStateVersionPropertyKey));
        edges.Should().ContainSingle(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeChildOf);
        edges.Single(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeChildOf).FromNodeId.Should().Be("actor-1");
        edges.Single(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeChildOf).ToNodeId.Should().Be("actor:actor-1:cmd-1:child-1");
    }

    [Fact]
    public void GraphNodesAndEdges_ShouldNormalizeUnknownTokens_WhenIdentifiersMissing()
    {
        var report = new WorkflowRunInsightReportDocument
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
        nodes.Should().Contain(x =>
            x.NodeId == "actor:unknown:unknown:child-1" &&
            x.NodeType == WorkflowExecutionGraphConstants.ActorNodeType &&
            x.Properties["actorId"] == "child-1");
        edges.Should().Contain(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeContainsStep);
        edges.Should().ContainSingle(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeChildOf);
        edges.Single(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeChildOf).FromNodeId.Should().Be("unknown");
        edges.Single(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeChildOf).ToNodeId.Should().Be("actor:unknown:unknown:child-1");
    }
}
