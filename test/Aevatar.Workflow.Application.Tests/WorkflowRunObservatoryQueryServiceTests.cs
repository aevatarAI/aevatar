using Aevatar.Workflow.Application.Abstractions.Observatory;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Observatory;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunObservatoryQueryServiceTests
{
    private const string CallerScope = "scope-alice";
    private const string OtherScope = "scope-bob";

    [Fact]
    public async Task ListRunsForScopeAsync_ShouldQueryByCallerScope_AndReturnOnlyOwnedRuns()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            ListResult =
            [
                Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Running, started: 200, updated: 300),
                Snapshot("run-2", CallerScope, WorkflowRunCompletionStatus.Completed, started: 100, updated: 150),
            ],
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var runs = await service.ListRunsForScopeAsync(CallerScope, new ObservatoryRunListFilter());

        currentState.LastListQuery!.ScopeId.Should().Be(CallerScope);
        runs.Should().HaveCount(2);
        // sorted by started_at desc
        runs[0].RunId.Should().Be("run-1");
        runs[1].RunId.Should().Be("run-2");
        runs[0].Status.Should().Be("running");
    }

    [Fact]
    public async Task ListRunsForScopeAsync_ShouldDropRunsThatDoNotMatchCallerScope()
    {
        // Defense-in-depth: even if the readmodel returned a foreign run, the service must filter it out.
        var currentState = new FakeCurrentStateQueryPort
        {
            ListResult =
            [
                Snapshot("run-own", CallerScope, WorkflowRunCompletionStatus.Running),
                Snapshot("run-foreign", OtherScope, WorkflowRunCompletionStatus.Running),
            ],
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var runs = await service.ListRunsForScopeAsync(CallerScope, new ObservatoryRunListFilter());

        runs.Should().ContainSingle().Which.RunId.Should().Be("run-own");
    }

    [Fact]
    public async Task ListRunsForScopeAsync_ShouldFilterByStatus_WhenRequested()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            ListResult =
            [
                Snapshot("run-running", CallerScope, WorkflowRunCompletionStatus.Running),
                Snapshot("run-done", CallerScope, WorkflowRunCompletionStatus.Completed),
            ],
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var runs = await service.ListRunsForScopeAsync(
            CallerScope,
            new ObservatoryRunListFilter { Status = "completed" });

        runs.Should().ContainSingle().Which.RunId.Should().Be("run-done");
    }

    [Fact]
    public async Task ListRunsForScopeAsync_ShouldPassScheduleIdsToQuery_WhenRequested()
    {
        var currentState = new FakeCurrentStateQueryPort();
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        await service.ListRunsForScopeAsync(
            CallerScope,
            new ObservatoryRunListFilter { ScheduleIds = ["schedule-a", "schedule-b"] });

        currentState.LastListQuery.Should().NotBeNull();
        currentState.LastListQuery!.ScheduleIds.Should().BeEquivalentTo(["schedule-a", "schedule-b"]);
    }

    [Fact]
    public async Task ListRunsForScopeAsync_ShouldReturnEmpty_WhenScopeBlank()
    {
        var currentState = new FakeCurrentStateQueryPort();
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var runs = await service.ListRunsForScopeAsync("  ", new ObservatoryRunListFilter());

        runs.Should().BeEmpty();
        currentState.LastListQuery.Should().BeNull();
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldReturnNull_WhenRunBelongsToAnotherScope()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            SingleResult = Snapshot("run-1", OtherScope, WorkflowRunCompletionStatus.Running),
        };
        var artifact = new FakeArtifactQueryPort();
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().BeNull();
        // Cross-scope must NOT read the timeline at all (no existence disclosure).
        artifact.ReportRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldReturnNull_WhenRunMissing()
    {
        var currentState = new FakeCurrentStateQueryPort { SingleResult = null };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var detail = await service.GetRunForScopeAsync(CallerScope, "missing");

        detail.Should().BeNull();
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldReturnReconstructedTimeline_WhenOwned()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            SingleResult = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Running),
        };
        var report = new WorkflowRunReport
        {
            Usage = new WorkflowRunUsageMetrics { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15, Cost = 0.004 },
            Timeline =
            [
                TimelineEvent("workflow.start", "started"),
                TimelineEvent("step.request", "draft (llm_call)", stepId: "draft"),
                TimelineEvent("role.reply", "planner"),
                ToolCallEvent("search", "call-1", "{\"q\":\"x\"}", "{\"hits\":3}", success: true),
                TimelineEvent("step.completed", "draft (success)", stepId: "draft"),
                TimelineEvent("workflow.completed", "completed"),
            ],
            RoleReplies =
            [
                new WorkflowRunRoleReply { RoleId = "planner", Content = "here is the plan", ContentLength = 16 },
            ],
        };
        var artifact = new FakeArtifactQueryPort { Report = report };
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.Summary.RunId.Should().Be("run-1");
        detail.Timeline.Select(x => x.Kind).Should().ContainInOrder(
            "RunStarted", "StepStarted", "Message", "ToolCall", "StepFinished", "RunFinished");

        // role.reply Message event carries the merged LLM/agent response content (not just the role id).
        var replyEvent = detail.Timeline.Single(x => x.Kind == "Message");
        replyEvent.Message.Should().Be("planner");
        replyEvent.Content.Should().Be("here is the plan");

        detail.UsageTotals.TotalTokens.Should().Be(15);
        detail.UsageTotals.Cost.Should().Be(0.004);

        var toolEvent = detail.Timeline.Single(x => x.Kind == "ToolCall");
        toolEvent.ToolCall.Should().NotBeNull();
        toolEvent.ToolCall!.ToolName.Should().Be("search");
        toolEvent.ToolCall.CallId.Should().Be("call-1");
        toolEvent.ToolCall.ArgumentsJson.Should().Be("{\"q\":\"x\"}");
        toolEvent.ToolCall.ResultJson.Should().Be("{\"hits\":3}");
        toolEvent.ToolCall.Success.Should().BeTrue();
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldExposeActiveHumanApprovalFacts()
    {
        var snapshot = Snapshot(
            "run-1",
            CallerScope,
            WorkflowRunCompletionStatus.Running,
            started: 1,
            updated: 5);
        var currentState = new FakeCurrentStateQueryPort { SingleResult = snapshot };
        var report = new WorkflowRunReport
        {
            Steps =
            [
                new WorkflowRunStepTrace
                {
                    StepId = "show_for_approval",
                    StepType = "human_approval",
                    RequestedAt = DateTimeOffset.UnixEpoch.AddSeconds(4),
                    SuspensionType = "human_approval",
                    SuspensionPrompt = "Review the generated workflow YAML.",
                    SuspensionContent = "name: daily_tech_digest\nsteps: []",
                    SuspensionTimeoutSeconds = 3600,
                },
            ],
        };
        var service = new WorkflowRunObservatoryQueryService(
            currentState,
            new FakeArtifactQueryPort { Report = report });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        var approval = detail!.Steps.Should().ContainSingle().Subject;
        approval.CompletedAtUtc.Should().BeNull();
        approval.SuspensionType.Should().Be("human_approval");
        approval.SuspensionPrompt.Should().Be("Review the generated workflow YAML.");
        approval.SuspensionContent.Should().Be("name: daily_tech_digest\nsteps: []");
        approval.SuspensionTimeoutSeconds.Should().Be(3600);
    }

    // 06-26 detail enrichment: the final result + per-step trace + rollup statistics are surfaced from the
    // committed run-report artifact. Final output/input are NOT truncated; step output is a 240-char preview.
    [Fact]
    public async Task GetRunForScopeAsync_ShouldSurfaceFinalResultStepsAndStatistics_WhenOwned()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            SingleResult = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Completed),
        };
        var report = new WorkflowRunReport
        {
            Input = "do the thing",
            FinalOutput = "the thing is done",
            FinalError = string.Empty,
            Steps =
            [
                new WorkflowRunStepTrace
                {
                    StepId = "draft",
                    StepType = "llm_call",
                    TargetRole = "planner",
                    RequestedAt = DateTimeOffset.UnixEpoch,
                    CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(2),
                    Success = true,
                    OutputPreview = "preview of the step output",
                    Usage = new WorkflowRunUsageMetrics
                    {
                        PromptTokens = 4,
                        CompletionTokens = 6,
                        TotalTokens = 10,
                        Cost = 0.002,
                    },
                },
            ],
            Summary = new WorkflowRunStatistics
            {
                TotalSteps = 1,
                RequestedSteps = 1,
                CompletedSteps = 1,
                RoleReplyCount = 1,
                StepTypeCounts = new Dictionary<string, int> { ["llm_call"] = 1 },
            },
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort { Report = report });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.Input.Should().Be("do the thing");
        detail.FinalOutput.Should().Be("the thing is done");
        detail.FinalError.Should().BeEmpty();

        detail.Steps.Should().ContainSingle();
        var step = detail.Steps[0];
        step.StepId.Should().Be("draft");
        step.StepType.Should().Be("llm_call");
        step.TargetRole.Should().Be("planner");
        step.Success.Should().BeTrue();
        step.OutputPreview.Should().Be("preview of the step output");
        step.DurationMs.Should().Be(2000);
        step.Usage.TotalTokens.Should().Be(10);

        detail.Statistics.TotalSteps.Should().Be(1);
        detail.Statistics.CompletedSteps.Should().Be(1);
        detail.Statistics.RoleReplyCount.Should().Be(1);
        detail.Statistics.StepTypeCounts["llm_call"].Should().Be(1);
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldFallBackToSummary_WhenReportNotYetMaterialized()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            SingleResult = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Running),
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort { Report = null });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.Timeline.Should().BeEmpty();
        detail.UsageTotals.TotalTokens.Should().Be(0);
        // Enriched fields degrade to empty when the report artifact has not materialized yet.
        detail.FinalOutput.Should().BeEmpty();
        detail.Steps.Should().BeEmpty();
        detail.Statistics.TotalSteps.Should().Be(0);
        detail.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldSurfaceDiagnostics_WhenRunFailed()
    {
        var snapshot = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Failed, started: 1, updated: 9);
        snapshot.LastError = "current state failure";
        snapshot.SagaStatus = WorkflowSagaStatus.CompensationDeadLetter;
        snapshot.DeadLetterFailedCompensationStepId = "refund";
        snapshot.DeadLetterRemainingUncompensated = 2;
        snapshot.DeadLetterError = "refund failed";
        var currentState = new FakeCurrentStateQueryPort { SingleResult = snapshot };
        var report = new WorkflowRunReport
        {
            FinalError = "final report failure",
            EndedAt = DateTimeOffset.UnixEpoch.AddSeconds(9),
            Steps =
            [
                new WorkflowRunStepTrace
                {
                    StepId = "publish",
                    StepType = "tool_call",
                    TargetRole = "publisher",
                    RequestedAt = DateTimeOffset.UnixEpoch.AddSeconds(3),
                    CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(4),
                    Success = false,
                    Error = "tool rejected request",
                    NextStepId = "notify",
                    BranchKey = "error",
                },
                new WorkflowRunStepTrace
                {
                    StepId = "notify",
                    StepType = "llm_call",
                    TargetRole = "notifier",
                    RequestedAt = DateTimeOffset.UnixEpoch.AddSeconds(5),
                },
            ],
            Timeline =
            [
                ToolCallEvent("publish_tool", "call-1", "{}", "{}", success: false),
            ],
        };
        report.Timeline[0].StepId = "publish";
        report.Timeline[0].StepType = "tool_call";
        report.Timeline[0].Data["error"] = "tool call failed";
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort { Report = report });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.Diagnostics.Should().Contain(log =>
            log.Code == "compensation_dead_letter" &&
            log.StepId == "refund" &&
            log.Message == "refund failed");
        detail.Diagnostics.Should().Contain(log =>
            log.Code == "current_state_last_error" &&
            log.Message == "current state failure");
        detail.Diagnostics.Should().Contain(log =>
            log.Code == "final_error" &&
            log.Message == "final report failure");
        detail.Diagnostics.Should().Contain(log =>
            log.Code == "step_failed" &&
            log.StepId == "publish" &&
            log.StepType == "tool_call" &&
            log.TargetRole == "publisher" &&
            log.Message == "tool rejected request");
        detail.Diagnostics.Should().Contain(log =>
            log.Code == "tool_call_failed" &&
            log.StepId == "publish" &&
            log.Message == "tool call failed");
        detail.Diagnostics.Should().Contain(log =>
            log.Code == "active_step" &&
            log.StepId == "notify");
        detail.Diagnostics.Should().Contain(log =>
            log.Code == "last_known_step" &&
            log.StepId == "notify");
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldSurfaceCurrentStateDiagnostic_WhenReportNotYetMaterialized()
    {
        var snapshot = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.TimedOut, started: 1, updated: 9);
        snapshot.LastError = "timeout waiting for role";
        var currentState = new FakeCurrentStateQueryPort { SingleResult = snapshot };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort { Report = null });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.Diagnostics.Should().Contain(log =>
            log.Code == "current_state_last_error" &&
            log.Message == "timeout waiting for role");
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldSanitizeCurrentStateErrors_WhenReportNotYetMaterialized()
    {
        var snapshot = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Failed, started: 1, updated: 9);
        snapshot.LastError = "Authorization: Bearer last-error-secret";
        snapshot.SagaStatus = WorkflowSagaStatus.CompensationDeadLetter;
        snapshot.DeadLetterError = """{"api_key":"dead-letter-secret","reason":"refund failed"}""";
        var currentState = new FakeCurrentStateQueryPort { SingleResult = snapshot };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort { Report = null });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        var lastError = detail!.Diagnostics.Single(item => item.Code == "current_state_last_error");
        var deadLetterError = detail.Diagnostics.Single(item => item.Code == "compensation_dead_letter");
        lastError.Message.Should().Contain("[redacted]");
        deadLetterError.Message.Should().Contain("\"api_key\":\"[redacted]\"");
        detail.Diagnostics.Select(item => item.Message).Should().NotContain(item =>
            item.Contains("last-error-secret", StringComparison.Ordinal) ||
            item.Contains("dead-letter-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldExplainProblemTerminalWithoutFailureDetail()
    {
        var snapshot = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Stopped, started: 1, updated: 9);
        var currentState = new FakeCurrentStateQueryPort { SingleResult = snapshot };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort { Report = null });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "terminal_without_failure_detail" &&
            diagnostic.Severity == "warning");
    }

    [Fact]
    public async Task GetRunGraphForScopeAsync_ShouldReturnNull_WhenRunBelongsToAnotherScope()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            SingleResult = Snapshot("run-1", OtherScope, WorkflowRunCompletionStatus.Running),
        };
        var artifact = new FakeArtifactQueryPort();
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        var graph = await service.GetRunGraphForScopeAsync(CallerScope, "run-1");

        graph.Should().BeNull();
        artifact.GraphRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRunGraphForScopeAsync_ShouldReturnGraph_WhenOwned()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            SingleResult = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Running),
        };
        var subgraph = new WorkflowRunGraphExportSubgraph { RootNodeId = "run-1" };
        subgraph.Nodes.Add(new WorkflowRunGraphExportNode { NodeId = "n1", NodeType = "role" });
        subgraph.Edges.Add(new WorkflowRunGraphExportEdge { EdgeId = "e1", FromNodeId = "run-1", ToNodeId = "n1", EdgeType = "child" });
        var artifact = new FakeArtifactQueryPort { Subgraph = subgraph };
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        var graph = await service.GetRunGraphForScopeAsync(CallerScope, "run-1");

        graph.Should().NotBeNull();
        graph!.RootNodeId.Should().Be("run-1");
        graph.Nodes.Should().ContainSingle().Which.NodeId.Should().Be("n1");
        graph.Edges.Should().ContainSingle().Which.EdgeType.Should().Be("child");
    }

    // 06-21: graph node ids are composite (step:{actor}:{cmd}:{stepId}); the viewer can only join a node
    // to its timeline steps if the bare stepId is surfaced. WorkflowStep nodes carry it in properties;
    // run / actor nodes have none.
    [Fact]
    public async Task GetRunGraphForScopeAsync_ShouldExposeBareStepId_ForStepNodesOnly()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            SingleResult = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Completed),
        };
        var subgraph = new WorkflowRunGraphExportSubgraph { RootNodeId = "run-1" };
        var stepNode = new WorkflowRunGraphExportNode { NodeId = "step:run-1:cmd:answer", NodeType = "WorkflowStep" };
        stepNode.Properties.Add("stepId", "answer");
        subgraph.Nodes.Add(stepNode);
        subgraph.Nodes.Add(new WorkflowRunGraphExportNode { NodeId = "run:run-1:cmd", NodeType = "WorkflowRun" });
        var artifact = new FakeArtifactQueryPort { Subgraph = subgraph };
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        var graph = await service.GetRunGraphForScopeAsync(CallerScope, "run-1");

        graph.Should().NotBeNull();
        graph!.Nodes.Single(node => node.NodeType == "WorkflowStep").StepId.Should().Be("answer");
        graph.Nodes.Single(node => node.NodeType == "WorkflowRun").StepId.Should().BeEmpty();
    }

    // 06-20-observatory-admin-cross-scope (G3/G4): cross-scope admin overview.
    [Fact]
    public async Task ListAllRunsAsync_ShouldReturnRunsAcrossAllScopes_WithoutScopeFilter()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            ListResult =
            [
                Snapshot("run-a", CallerScope, WorkflowRunCompletionStatus.Running, started: 300, updated: 300),
                Snapshot("run-b", OtherScope, WorkflowRunCompletionStatus.Completed, started: 100, updated: 100),
            ],
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var runs = await service.ListAllRunsAsync(new ObservatoryRunListFilter());

        // No scope filter is pushed to the readmodel — this is the cross-scope path.
        string.IsNullOrEmpty(currentState.LastListQuery!.ScopeId).Should().BeTrue();
        runs.Should().HaveCount(2);
        runs[0].RunId.Should().Be("run-a"); // sorted by started desc
        runs[1].RunId.Should().Be("run-b");
        // Every row carries its owning scope so the admin overview can attribute it.
        runs.Single(r => r.RunId == "run-a").ScopeId.Should().Be(CallerScope);
        runs.Single(r => r.RunId == "run-b").ScopeId.Should().Be(OtherScope);
    }

    [Fact]
    public async Task ListAllRunsAsync_ShouldFilterByStatus_WithinWindow()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            ListResult =
            [
                Snapshot("run-running", CallerScope, WorkflowRunCompletionStatus.Running),
                Snapshot("run-failed", OtherScope, WorkflowRunCompletionStatus.Failed),
            ],
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var runs = await service.ListAllRunsAsync(new ObservatoryRunListFilter { Status = "failed" });

        runs.Should().ContainSingle().Which.RunId.Should().Be("run-failed");
    }

    [Fact]
    public async Task GetRunAsync_ShouldResolveRunAcrossScopes_AndReturnDetail()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            Snapshots =
            [
                Snapshot("run-foreign", OtherScope, WorkflowRunCompletionStatus.Completed),
            ],
        };
        var artifact = new FakeArtifactQueryPort
        {
            Report = new WorkflowRunReport { FinalOutput = "done" },
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        var detail = await service.GetRunAsync("run-foreign");

        detail.Should().NotBeNull();
        detail!.Summary.RunId.Should().Be("run-foreign");
        detail.Summary.ScopeId.Should().Be(OtherScope);
        detail.FinalOutput.Should().Be("done");
        currentState.GetRequests.Should().ContainSingle().Which.Should().Be("run-foreign");
        artifact.ReportRequests.Should().ContainSingle().Which.Should().Be("run-foreign");
    }

    [Fact]
    public async Task GetRunAsync_ShouldReturnNull_WhenRunMissingOrScopeMissing()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            Snapshots =
            [
                Snapshot("run-without-scope", string.Empty, WorkflowRunCompletionStatus.Completed),
            ],
        };
        var artifact = new FakeArtifactQueryPort();
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        (await service.GetRunAsync("missing")).Should().BeNull();
        (await service.GetRunAsync("run-without-scope")).Should().BeNull();
        artifact.ReportRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRunGraphAsync_ShouldResolveRunAcrossScopes_AndReturnGraph()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            Snapshots =
            [
                Snapshot("run-foreign", OtherScope, WorkflowRunCompletionStatus.Completed),
            ],
        };
        var subgraph = new WorkflowRunGraphExportSubgraph { RootNodeId = "run-foreign" };
        subgraph.Nodes.Add(new WorkflowRunGraphExportNode { NodeId = "run-foreign", NodeType = "WorkflowRun" });
        var artifact = new FakeArtifactQueryPort { Subgraph = subgraph };
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        var graph = await service.GetRunGraphAsync("run-foreign");

        graph.Should().NotBeNull();
        graph!.RootNodeId.Should().Be("run-foreign");
        currentState.GetRequests.Should().ContainSingle().Which.Should().Be("run-foreign");
        artifact.GraphRequests.Should().ContainSingle().Which.Should().Be("run-foreign");
    }

    [Fact]
    public async Task ListRunsForScopeAsync_ShouldPopulateScopeIdOnSummaries()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            ListResult = [Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Running)],
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var runs = await service.ListRunsForScopeAsync(CallerScope, new ObservatoryRunListFilter());

        runs.Should().ContainSingle().Which.ScopeId.Should().Be(CallerScope);
    }

    private static WorkflowActorSnapshot Snapshot(
        string runId,
        string scopeId,
        WorkflowRunCompletionStatus status,
        long started = 0,
        long updated = 0)
    {
        var snapshot = new WorkflowActorSnapshot
        {
            ActorId = runId,
            ScopeId = scopeId,
            WorkflowName = "wf-" + runId,
            CompletionStatus = status,
            StateVersion = 7,
        };
        if (updated > 0)
            snapshot.LastUpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(updated);
        if (started > 0)
            snapshot.StartedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddSeconds(started));
        return snapshot;
    }

    private static WorkflowRunTimelineEvent TimelineEvent(string stage, string message, string? stepId = null) =>
        new()
        {
            Stage = stage,
            Message = message,
            StepId = stepId ?? string.Empty,
            Timestamp = DateTimeOffset.UnixEpoch,
        };

    private static WorkflowRunTimelineEvent ToolCallEvent(
        string toolName,
        string callId,
        string argsJson,
        string resultJson,
        bool success)
    {
        var item = new WorkflowRunTimelineEvent
        {
            Stage = "tool.call",
            Message = toolName,
            Timestamp = DateTimeOffset.UnixEpoch,
        };
        item.Data["call_id"] = callId;
        item.Data["arguments_json"] = argsJson;
        item.Data["result_json"] = resultJson;
        item.Data["success"] = success ? "true" : "false";
        return item;
    }

    private sealed class FakeCurrentStateQueryPort : IWorkflowExecutionCurrentStateQueryPort
    {
        public bool WorkflowActorCurrentStateQueryEnabled => true;
        public IReadOnlyList<WorkflowActorSnapshot> ListResult { get; init; } = [];
        public WorkflowActorSnapshot? SingleResult { get; init; }
        public IReadOnlyList<WorkflowActorSnapshot> Snapshots { get; init; } = [];
        public WorkflowActorCurrentStateListQuery? LastListQuery { get; private set; }
        public List<string> GetRequests { get; } = [];

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(string actorId, CancellationToken ct = default)
        {
            GetRequests.Add(actorId);
            if (SingleResult != null)
            {
                return Task.FromResult(string.Equals(SingleResult.ActorId, actorId, StringComparison.Ordinal)
                    ? SingleResult
                    : null);
            }

            return Task.FromResult(Snapshots.FirstOrDefault(snapshot =>
                string.Equals(snapshot.ActorId, actorId, StringComparison.Ordinal)));
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(int take = 200, CancellationToken ct = default) =>
            Task.FromResult(ListResult);

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            WorkflowActorCurrentStateListQuery query,
            CancellationToken ct = default)
        {
            LastListQuery = query;
            return Task.FromResult(ListResult);
        }

        public Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult<WorkflowActorProjectionState?>(null);
    }

    private sealed class FakeArtifactQueryPort : IWorkflowExecutionArtifactQueryPort
    {
        public bool WorkflowArtifactQueryEnabled => true;
        public WorkflowRunReport? Report { get; init; }
        public WorkflowRunGraphExportSubgraph Subgraph { get; init; } = new();
        public List<string> ReportRequests { get; } = [];
        public List<string> GraphRequests { get; } = [];

        public Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(string actorId, CancellationToken ct = default)
        {
            ReportRequests.Add(actorId);
            return Task.FromResult(Report);
        }

        public Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(string actorId, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRunTimelineExportItem>>([]);

        public Task<IReadOnlyList<WorkflowRunGraphExportEdge>> GetWorkflowRunGraphExportEdgesAsync(string actorId, int take = 200, WorkflowRunGraphExportQueryOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRunGraphExportEdge>>([]);

        public Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(string actorId, int depth = 2, int take = 200, WorkflowRunGraphExportQueryOptions? options = null, CancellationToken ct = default)
        {
            GraphRequests.Add(actorId);
            return Task.FromResult(Subgraph);
        }
    }
}
