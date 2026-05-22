using System.Text.Json;
using Aevatar.AI.ToolProviders.Workflow.Tools;
using Aevatar.Workflow.Application.Abstractions.Queries;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.ToolProviders.Workflow.Tests;

public sealed class WorkflowRunToolContractTests
{
    [Fact]
    public async Task EventQueryTool_Timeline_ShouldUseWorkflowRunIdAndApplyFilters()
    {
        var query = new RecordingWorkflowExecutionQueryService
        {
            Timeline =
            [
                CreateTimelineItem("step.completed", "type.workflow.completed", "kept-step"),
                CreateTimelineItem("step.requested", "type.workflow.requested", "filtered-step"),
            ],
        };
        var tool = new EventQueryTool(query, new WorkflowToolOptions { MaxTimelineItems = 9 });

        var result = await tool.ExecuteAsync(
            """{"workflow_run_id":"run-1","stage_filter":"completed","event_type_filter":"completed","take":25}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("workflow_run_id").GetString().Should().Be("run-1");
        root.GetProperty("count").GetInt32().Should().Be(1);
        root.GetProperty("total_available").GetInt32().Should().Be(2);
        root.GetProperty("events")[0].GetProperty("step_id").GetString().Should().Be("kept-step");
        query.Calls.Should().Equal("ListWorkflowRunTimelineExport:run-1:25");
    }

    [Fact]
    public async Task EventQueryTool_Timeline_ShouldAcceptDeprecatedActorIdAlias()
    {
        var query = new RecordingWorkflowExecutionQueryService
        {
            Timeline = [CreateTimelineItem("step.completed", "type.workflow.completed", "alias-step")],
        };
        var tool = new EventQueryTool(query, new WorkflowToolOptions());

        var result = await tool.ExecuteAsync("""{"actor_id":"legacy-run"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("workflow_run_id").GetString().Should().Be("legacy-run");
        query.Calls.Should().Equal("ListWorkflowRunTimelineExport:legacy-run:50");
    }

    [Fact]
    public async Task EventQueryTool_Edges_ShouldForwardWorkflowRunAndEdgeFilters()
    {
        var query = new RecordingWorkflowExecutionQueryService
        {
            GraphEdges =
            [
                new WorkflowRunGraphExportEdge
                {
                    EdgeId = "edge-1",
                    FromNodeId = "run-1",
                    ToNodeId = "child-1",
                    EdgeType = "CHILD_OF",
                    UpdatedAt = new DateTimeOffset(2026, 5, 20, 1, 2, 3, TimeSpan.Zero),
                },
            ],
        };
        var tool = new EventQueryTool(query, new WorkflowToolOptions());

        var result = await tool.ExecuteAsync(
            """{"workflow_run_id":"run-1","action":"edges","take":7,"edge_types":["CHILD_OF","OWNS"]}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("workflow_run_id").GetString().Should().Be("run-1");
        root.GetProperty("edges")[0].GetProperty("id").GetString().Should().Be("edge-1");
        query.Calls.Should().Equal("ListWorkflowRunGraphExportEdges:run-1:7:Both:CHILD_OF,OWNS");
    }

    [Fact]
    public async Task EventQueryTool_WhenWorkflowRunIdMissing_ShouldReturnNewErrorAndSchemaAllowAlias()
    {
        var query = new RecordingWorkflowExecutionQueryService();
        var tool = new EventQueryTool(query, new WorkflowToolOptions());

        var result = await tool.ExecuteAsync("{}");

        result.Should().Contain("'workflow_run_id' is required");
        tool.ParametersSchema.Should().Contain("\"anyOf\"");
        tool.ParametersSchema.Should().Contain("\"workflow_run_id\"");
        tool.ParametersSchema.Should().Contain("\"actor_id\"");
        query.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowStatusTool_Status_ShouldUseWorkflowRunReportArtifact()
    {
        var query = new RecordingWorkflowExecutionQueryService
        {
            Report = new WorkflowRunReport
            {
                RootActorId = "run-1",
                WorkflowName = "demo",
                CompletionStatus = WorkflowRunCompletionStatus.Completed,
                StateVersion = 42,
                Success = true,
                Summary = new WorkflowRunStatistics
                {
                    TotalSteps = 2,
                    RequestedSteps = 2,
                    CompletedSteps = 2,
                    RoleReplyCount = 1,
                },
                Steps =
                [
                    new WorkflowRunStepTrace
                    {
                        StepId = "step-1",
                        StepType = "llm",
                        TargetRole = "assistant",
                        Success = true,
                    },
                ],
                Topology = [new WorkflowRunTopologyEdge("run-1", "child-1")],
            },
        };
        var tool = new WorkflowStatusTool(query, new WorkflowToolOptions());

        var result = await tool.ExecuteAsync("""{"workflow_run_id":"run-1"}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("workflow_run_id").GetString().Should().Be("run-1");
        root.GetProperty("workflow_name").GetString().Should().Be("demo");
        root.GetProperty("status").GetString().Should().Be("Completed");
        root.GetProperty("summary").GetProperty("total_steps").GetInt32().Should().Be(2);
        query.Calls.Should().Equal("GetWorkflowRunReportArtifact:run-1");
    }

    [Fact]
    public async Task WorkflowStatusTool_Status_ShouldAcceptDeprecatedActorIdAliasAndReportMissingArtifact()
    {
        var query = new RecordingWorkflowExecutionQueryService();
        var tool = new WorkflowStatusTool(query, new WorkflowToolOptions());

        var result = await tool.ExecuteAsync("""{"actor_id":"legacy-run"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString().Should().Be("No workflow run found for 'legacy-run'");
        query.Calls.Should().Equal("GetWorkflowRunReportArtifact:legacy-run");
    }

    [Fact]
    public async Task WorkflowStatusTool_Timeline_ShouldAcceptWorkflowRunIdAndAlias()
    {
        var query = new RecordingWorkflowExecutionQueryService
        {
            Timeline = [CreateTimelineItem("step.completed", "type.workflow.completed", "step-1")],
        };
        var tool = new WorkflowStatusTool(query, new WorkflowToolOptions { MaxTimelineItems = 6 });

        var workflowRunResult = await tool.ExecuteAsync("""{"action":"timeline","workflow_run_id":"run-1","take":4}""");
        var aliasResult = await tool.ExecuteAsync("""{"action":"timeline","actor_id":"legacy-run"}""");

        using var workflowRunDocument = JsonDocument.Parse(workflowRunResult);
        workflowRunDocument.RootElement.GetProperty("workflow_run_id").GetString().Should().Be("run-1");
        workflowRunDocument.RootElement.GetProperty("events")[0].GetProperty("step_id").GetString().Should().Be("step-1");

        using var aliasDocument = JsonDocument.Parse(aliasResult);
        aliasDocument.RootElement.GetProperty("workflow_run_id").GetString().Should().Be("legacy-run");
        query.Calls.Should().Equal(
            "ListWorkflowRunTimelineExport:run-1:4",
            "ListWorkflowRunTimelineExport:legacy-run:6");
    }

    [Fact]
    public async Task WorkflowStatusTool_WhenWorkflowRunIdMissing_ShouldReturnNewErrors()
    {
        var query = new RecordingWorkflowExecutionQueryService();
        var tool = new WorkflowStatusTool(query, new WorkflowToolOptions());

        var statusResult = await tool.ExecuteAsync("{}");
        var timelineResult = await tool.ExecuteAsync("""{"action":"timeline"}""");

        statusResult.Should().Contain("'workflow_run_id' is required for 'status' action");
        timelineResult.Should().Contain("'workflow_run_id' is required for 'timeline' action");
        query.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ActorInspectTool_Graph_ShouldUseWorkflowRunGraphExportSubgraph()
    {
        var query = new RecordingWorkflowExecutionQueryService
        {
            GraphSubgraph = new WorkflowRunGraphExportSubgraph
            {
                RootNodeId = "run-1",
                Nodes =
                {
                    new WorkflowRunGraphExportNode
                    {
                        NodeId = "run-1",
                        NodeType = "workflow-run",
                        UpdatedAt = new DateTimeOffset(2026, 5, 20, 1, 2, 3, TimeSpan.Zero),
                    },
                },
                Edges =
                {
                    new WorkflowRunGraphExportEdge
                    {
                        FromNodeId = "run-1",
                        ToNodeId = "child-1",
                        EdgeType = "CHILD_OF",
                    },
                },
            },
        };
        var tool = new ActorInspectTool(query, new WorkflowToolOptions { MaxGraphDepth = 2 });

        var result = await tool.ExecuteAsync("""{"action":"graph","actor_id":"run-1","graph_depth":4,"take":11}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("root").GetString().Should().Be("run-1");
        root.GetProperty("node_count").GetInt32().Should().Be(1);
        root.GetProperty("edge_count").GetInt32().Should().Be(1);
        root.GetProperty("edges")[0].GetProperty("to").GetString().Should().Be("child-1");
        query.Calls.Should().Equal("GetWorkflowRunGraphExportSubgraph:run-1:4:11");
    }

    private static WorkflowRunTimelineExportItem CreateTimelineItem(string stage, string eventType, string stepId)
    {
        var item = new WorkflowRunTimelineExportItem
        {
            Stage = stage,
            Message = $"message for {stepId}",
            AgentId = "agent-1",
            StepId = stepId,
            StepType = "llm",
            EventType = eventType,
            Timestamp = new DateTimeOffset(2026, 5, 20, 1, 2, 3, TimeSpan.Zero),
        };
        item.Data.Add("payload", stepId);
        return item;
    }

    private sealed class RecordingWorkflowExecutionQueryService : IWorkflowExecutionQueryApplicationService
    {
        public bool ActorQueryEnabled { get; init; } = true;
        public List<string> Calls { get; } = [];
        public WorkflowRunReport? Report { get; init; }
        public IReadOnlyList<WorkflowRunTimelineExportItem> Timeline { get; init; } = [];
        public IReadOnlyList<WorkflowRunGraphExportEdge> GraphEdges { get; init; } = [];
        public WorkflowRunGraphExportSubgraph GraphSubgraph { get; init; } = new();

        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowAgentSummary>>([]);

        public IReadOnlyList<string> ListWorkflows() => [];

        public IReadOnlyList<WorkflowCatalogItem> ListWorkflowCatalog() => [];

        public WorkflowCatalogItemDetail? GetWorkflowDetail(string workflowName) => null;

        public WorkflowCapabilitiesDocument GetCapabilities() => new();

        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult<WorkflowActorSnapshot?>(null);

        public Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(string workflowRunId, CancellationToken ct = default)
        {
            Calls.Add($"GetWorkflowRunReportArtifact:{workflowRunId}");
            return Task.FromResult(Report);
        }

        public Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(
            string workflowRunId,
            int take = 200,
            CancellationToken ct = default)
        {
            Calls.Add($"ListWorkflowRunTimelineExport:{workflowRunId}:{take}");
            return Task.FromResult(Timeline);
        }

        public Task<IReadOnlyList<WorkflowRunGraphExportEdge>> ListWorkflowRunGraphExportEdgesAsync(
            string workflowRunId,
            int take = 200,
            WorkflowRunGraphExportQueryOptions? options = null,
            CancellationToken ct = default)
        {
            Calls.Add($"ListWorkflowRunGraphExportEdges:{workflowRunId}:{take}:{options?.Direction}:{string.Join(",", options?.EdgeTypes ?? [])}");
            return Task.FromResult(GraphEdges);
        }

        public Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(
            string workflowRunId,
            int depth = 2,
            int take = 200,
            WorkflowRunGraphExportQueryOptions? options = null,
            CancellationToken ct = default)
        {
            Calls.Add($"GetWorkflowRunGraphExportSubgraph:{workflowRunId}:{depth}:{take}");
            return Task.FromResult(GraphSubgraph);
        }
    }
}
