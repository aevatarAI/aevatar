using System.Text.Json;
using Aevatar.AI.ToolProviders.Workflow.Tools;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
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
    public async Task EventQueryTool_Timeline_ShouldRejectActorIdAlias()
    {
        var query = new RecordingWorkflowExecutionQueryService
        {
            Timeline = [CreateTimelineItem("step.completed", "type.workflow.completed", "alias-step")],
        };
        var tool = new EventQueryTool(query, new WorkflowToolOptions());

        var result = await tool.ExecuteAsync("""{"actor_id":"legacy-run"}""");

        result.Should().Contain("'workflow_run_id' is required");
        query.Calls.Should().BeEmpty();
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
    public async Task EventQueryTool_WhenWorkflowRunIdMissing_ShouldReturnNewErrorAndSchemaRequireWorkflowRunId()
    {
        var query = new RecordingWorkflowExecutionQueryService();
        var tool = new EventQueryTool(query, new WorkflowToolOptions());

        var result = await tool.ExecuteAsync("{}");

        result.Should().Contain("'workflow_run_id' is required");
        tool.ParametersSchema.Should().Contain("\"required\": [\"workflow_run_id\"]");
        tool.ParametersSchema.Should().Contain("\"workflow_run_id\"");
        tool.ParametersSchema.Should().NotContain("\"actor_id\"");
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
    public async Task WorkflowStatusTool_Status_ShouldRejectActorIdAlias()
    {
        var query = new RecordingWorkflowExecutionQueryService();
        var tool = new WorkflowStatusTool(query, new WorkflowToolOptions());

        var result = await tool.ExecuteAsync("""{"actor_id":"legacy-run"}""");

        result.Should().Contain("'workflow_run_id' is required for 'status' action");
        tool.ParametersSchema.Should().NotContain("\"actor_id\"");
        query.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowStatusTool_Timeline_ShouldRequireWorkflowRunId()
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

        aliasResult.Should().Contain("'workflow_run_id' is required for 'timeline' action");
        query.Calls.Should().Equal("ListWorkflowRunTimelineExport:run-1:4");
    }

    [Fact]
    public async Task WorkflowStatusTool_CatalogAndDetail_ShouldAwaitAsyncQueryMethods()
    {
        var query = new RecordingWorkflowExecutionQueryService
        {
            Catalog =
            [
                new WorkflowCatalogItem
                {
                    Name = "direct",
                    Description = "Direct workflow.",
                    Category = "deterministic",
                    Group = "starter-workflows",
                    Source = "builtin",
                },
            ],
            Detail = new WorkflowCatalogItemDetail
            {
                Catalog = new WorkflowCatalogItem
                {
                    Name = "direct",
                    Description = "Direct workflow.",
                },
                Definition = new WorkflowCatalogDefinition
                {
                    Roles =
                    [
                        new WorkflowCatalogRole
                        {
                            Id = "assistant",
                            Name = "Assistant",
                        },
                    ],
                    Steps =
                    [
                        new WorkflowCatalogStep
                        {
                            Id = "start",
                            Type = "llm",
                            TargetRole = "assistant",
                        },
                    ],
                },
            },
        };
        var tool = new WorkflowStatusTool(query, new WorkflowToolOptions());

        var catalogResult = await tool.ExecuteAsync("""{"action":"catalog"}""");
        var detailResult = await tool.ExecuteAsync("""{"action":"detail","workflow_name":"direct"}""");

        using var catalogDocument = JsonDocument.Parse(catalogResult);
        catalogDocument.RootElement.GetProperty("workflows")[0].GetProperty("name").GetString().Should().Be("direct");
        using var detailDocument = JsonDocument.Parse(detailResult);
        detailDocument.RootElement.GetProperty("name").GetString().Should().Be("direct");
        query.Calls.Should().Equal("ListWorkflowCatalog", "GetWorkflowDetail:direct");
    }

    [Fact]
    public async Task WorkflowStatusTool_WhenActorIdMissing_ShouldReturnNewErrors()
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
    public async Task WorkflowActorCurrentStateTool_DefaultSnapshot_ShouldUseActorId()
    {
        var query = new RecordingWorkflowExecutionQueryService
        {
            CurrentState = new WorkflowActorSnapshot
            {
                ActorId = "run-1",
                WorkflowName = "demo",
                CompletionStatus = WorkflowRunCompletionStatus.Completed,
                StateVersion = 42,
                LastCommandId = "cmd-1",
                LastEventId = "event-1",
                LastUpdatedAt = new DateTimeOffset(2026, 5, 20, 1, 2, 3, TimeSpan.Zero),
                LastSuccess = true,
                LastOutput = "done",
                TotalSteps = 3,
                RequestedSteps = 3,
                CompletedSteps = 3,
                RoleReplyCount = 2,
            },
        };
        var tool = new WorkflowActorCurrentStateTool(query, new WorkflowToolOptions());

        var result = await tool.ExecuteAsync("""{"actor_id":"run-1"}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("actor_id").GetString().Should().Be("run-1");
        root.GetProperty("workflow_name").GetString().Should().Be("demo");
        root.GetProperty("status").GetString().Should().Be("Completed");
        root.GetProperty("state_version").GetInt64().Should().Be(42);
        root.GetProperty("steps").GetProperty("role_replies").GetInt32().Should().Be(2);
        query.Calls.Should().Equal("GetWorkflowActorCurrentState:run-1");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"actor_id":""}""")]
    [InlineData("""{"actor_id":"   "}""")]
    public async Task WorkflowActorCurrentStateTool_WhenActorIdMissingOrBlank_ShouldNotCallQueryService(
        string argumentsJson)
    {
        var query = new RecordingWorkflowExecutionQueryService();
        var tool = new WorkflowActorCurrentStateTool(query, new WorkflowToolOptions());

        var result = await tool.ExecuteAsync(argumentsJson);

        result.Should().Contain("'actor_id' is required");
        query.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowActorCurrentStateTool_WhenCurrentStateQueryDisabled_ShouldReturnDeploymentDisabledError()
    {
        var query = new RecordingWorkflowExecutionQueryService
        {
            WorkflowActorCurrentStateQueryEnabled = false,
        };
        var tool = new WorkflowActorCurrentStateTool(query, new WorkflowToolOptions());

        var result = await tool.ExecuteAsync("""{"actor_id":"run-1"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString()
            .Should().Be("Workflow actor current-state query is not enabled on this deployment.");
        query.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("list")]
    [InlineData("agents")]
    public async Task WorkflowActorCurrentStateTool_ListAndAgents_ShouldReturnRegisteredAgents(string action)
    {
        var query = new RecordingWorkflowExecutionQueryService
        {
            Agents =
            [
                new WorkflowAgentSummary("agent-1", "assistant", "Assistant agent"),
                new WorkflowAgentSummary("agent-2", "worker", "Worker agent"),
            ],
        };
        var tool = new WorkflowActorCurrentStateTool(query, new WorkflowToolOptions());

        var result = await tool.ExecuteAsync($$"""{"action":"{{action}}"}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("count").GetInt32().Should().Be(2);
        root.GetProperty("agents")[0].GetProperty("id").GetString().Should().Be("agent-1");
        root.GetProperty("agents")[0].GetProperty("type").GetString().Should().Be("assistant");
        root.GetProperty("agents")[1].GetProperty("description").GetString().Should().Be("Worker agent");
        query.Calls.Should().Equal("ListAgents");
    }

    [Fact]
    public async Task WorkflowActorCurrentStateTool_DeadLetters_ShouldUseSagaStatusFilteredCurrentStateList()
    {
        var query = new RecordingWorkflowExecutionQueryService
        {
            CurrentStates =
            [
                new WorkflowActorSnapshot
                {
                    ActorId = "run-dead-letter-1",
                    WorkflowName = "orders",
                    CompletionStatus = WorkflowRunCompletionStatus.Failed,
                    SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
                    StateVersion = 44,
                    LastEventId = "evt-44",
                    DeadLetterFailedCompensationStepId = "refund_payment",
                    DeadLetterRemainingUncompensated = 2,
                    DeadLetterError = "refund failed",
                },
            ],
        };
        var tool = new WorkflowActorCurrentStateTool(
            query,
            new WorkflowToolOptions { MaxWorkflowActorCurrentStates = 5 });

        var result = await tool.ExecuteAsync("""{"action":"dead_letters","take":9}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("count").GetInt32().Should().Be(1);
        var deadLetter = root.GetProperty("dead_letters")[0];
        deadLetter.GetProperty("actor_id").GetString().Should().Be("run-dead-letter-1");
        deadLetter.GetProperty("state_version").GetInt64().Should().Be(44);
        deadLetter.GetProperty("saga_status").GetString().Should().Be("CompensationDeadLetter");
        deadLetter.GetProperty("dead_letter").GetProperty("failed_compensation_step_id").GetString()
            .Should().Be("refund_payment");
        deadLetter.GetProperty("dead_letter").GetProperty("remaining_uncompensated").GetInt32()
            .Should().Be(2);
        deadLetter.GetProperty("dead_letter").GetProperty("error").GetString()
            .Should().Be("refund failed");
        query.LastCurrentStateListQuery.Should().NotBeNull();
        query.LastCurrentStateListQuery!.Take.Should().Be(9);
        query.LastCurrentStateListQuery.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        query.Calls.Should().Equal("ListWorkflowActorCurrentStatesQuery:9:CompensationDeadLetter");
        tool.ParametersSchema.Should().Contain("\"dead_letters\"");
    }

    [Fact]
    public async Task WorkflowActorCurrentStateTool_Graph_ShouldNotExposeWorkflowArtifactSubgraph()
    {
        var query = new RecordingWorkflowExecutionQueryService();
        var tool = new WorkflowActorCurrentStateTool(query, new WorkflowToolOptions { MaxGraphDepth = 2 });

        var result = await tool.ExecuteAsync("""{"action":"graph","actor_id":"run-1","graph_depth":4,"take":11}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString().Should().Be("Unsupported workflow_actor_current_state action 'graph'");
        tool.ParametersSchema.Should().NotContain("\"graph\"");
        tool.ParametersSchema.Should().NotContain("\"graph_depth\"");
        query.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowArtifactQueryTool_Subgraph_ShouldUseWorkflowRunGraphExportSubgraph()
    {
        var query = new RecordingWorkflowExecutionQueryService
        {
            GraphSubgraph = new WorkflowRunGraphExportSubgraph
            {
                RootNodeId = "run-1",
                SourceStateVersion = 12,
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
        var tool = new WorkflowArtifactQueryTool(query, new WorkflowToolOptions { MaxGraphDepth = 2 });

        var result = await tool.ExecuteAsync("""{"action":"subgraph","workflow_run_id":"run-1","graph_depth":4,"take":11}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("workflow_run_id").GetString().Should().Be("run-1");
        root.GetProperty("artifact").GetString().Should().Be("graph_subgraph");
        root.GetProperty("subgraph").GetProperty("root").GetString().Should().Be("run-1");
        root.GetProperty("subgraph").GetProperty("source_state_version").GetInt64().Should().Be(12);
        root.GetProperty("subgraph").GetProperty("node_count").GetInt32().Should().Be(1);
        root.GetProperty("subgraph").GetProperty("edge_count").GetInt32().Should().Be(1);
        root.GetProperty("subgraph").GetProperty("edges")[0].GetProperty("to").GetString().Should().Be("child-1");
        tool.ParametersSchema.Should().NotContain("\"actor_id\"");
        query.Calls.Should().Equal("GetWorkflowRunGraphExportSubgraph:run-1:4:11");
    }

    [Fact]
    public async Task WorkflowArtifactQueryTool_ShouldRejectActorIdAliasAndNonSubgraphActions()
    {
        var query = new RecordingWorkflowExecutionQueryService();
        var tool = new WorkflowArtifactQueryTool(query, new WorkflowToolOptions());

        var aliasResult = await tool.ExecuteAsync("""{"actor_id":"legacy-run"}""");
        var wrongActionResult = await tool.ExecuteAsync("""{"action":"edges","workflow_run_id":"run-1"}""");

        aliasResult.Should().Contain("'workflow_run_id' is required");
        wrongActionResult.Should().Contain("only supports action='subgraph'");
        query.Calls.Should().BeEmpty();
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
        public bool WorkflowActorCurrentStateQueryEnabled { get; init; } = true;
        public List<string> Calls { get; } = [];
        public WorkflowRunReport? Report { get; init; }
        public IReadOnlyList<WorkflowCatalogItem> Catalog { get; init; } = [];
        public WorkflowCatalogItemDetail? Detail { get; init; }
        public IReadOnlyList<WorkflowAgentSummary> Agents { get; init; } = [];
        public WorkflowActorSnapshot? CurrentState { get; init; }
        public IReadOnlyList<WorkflowActorSnapshot> CurrentStates { get; init; } = [];
        public WorkflowActorCurrentStateListQuery? LastCurrentStateListQuery { get; private set; }
        public IReadOnlyList<WorkflowRunTimelineExportItem> Timeline { get; init; } = [];
        public IReadOnlyList<WorkflowRunGraphExportEdge> GraphEdges { get; init; } = [];
        public WorkflowRunGraphExportSubgraph GraphSubgraph { get; init; } = new();

        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default)
        {
            Calls.Add("ListAgents");
            return Task.FromResult(Agents);
        }

        public IReadOnlyList<string> ListWorkflows() => [];

        public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default)
        {
            Calls.Add("ListWorkflowCatalog");
            return Task.FromResult(Catalog);
        }

        public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(
            string workflowName,
            CancellationToken ct = default)
        {
            Calls.Add($"GetWorkflowDetail:{workflowName}");
            return Task.FromResult(Detail);
        }

        public Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(new WorkflowCapabilitiesDocument());

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(string actorId, CancellationToken ct = default)
        {
            Calls.Add($"GetWorkflowActorCurrentState:{actorId}");
            return Task.FromResult(CurrentState);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            WorkflowActorCurrentStateListQuery query,
            CancellationToken ct = default)
        {
            LastCurrentStateListQuery = query;
            Calls.Add($"ListWorkflowActorCurrentStatesQuery:{query.Take}:{query.SagaStatus}");
            return Task.FromResult(CurrentStates);
        }

        public Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(string actorId, CancellationToken ct = default)
        {
            Calls.Add($"GetWorkflowRunReportArtifact:{actorId}");
            return Task.FromResult(Report);
        }

        public Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(
            string actorId,
            int take = 200,
            CancellationToken ct = default)
        {
            Calls.Add($"ListWorkflowRunTimelineExport:{actorId}:{take}");
            return Task.FromResult(Timeline);
        }

        public Task<IReadOnlyList<WorkflowRunGraphExportEdge>> ListWorkflowRunGraphExportEdgesAsync(
            string actorId,
            int take = 200,
            WorkflowRunGraphExportQueryOptions? options = null,
            CancellationToken ct = default)
        {
            Calls.Add($"ListWorkflowRunGraphExportEdges:{actorId}:{take}:{options?.Direction}:{string.Join(",", options?.EdgeTypes ?? [])}");
            return Task.FromResult(GraphEdges);
        }

        public Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowRunGraphExportQueryOptions? options = null,
            CancellationToken ct = default)
        {
            Calls.Add($"GetWorkflowRunGraphExportSubgraph:{actorId}:{depth}:{take}");
            return Task.FromResult(GraphSubgraph);
        }
    }
}
