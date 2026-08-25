using Aevatar.Workflow.Application.Abstractions.Observatory;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Observatory;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;

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
    public async Task ListActivityRunsForScopeAsync_ShouldReturnPagedTypedRows_AndRedactLegacyBindingId()
    {
        const string workflowId = "wf-alpha";
        const string runId = "run-alpha";
        const string actorId = "actor-alpha";
        const string serviceIdentityCandidate = "svc-alpha";
        const string memberIdentityCandidate = "m-alpha";
        var snapshot = Snapshot(actorId, CallerScope, WorkflowRunCompletionStatus.Completed, started: 100, updated: 300);
        snapshot.RunId = runId;
        snapshot.WorkflowId = workflowId;
        snapshot.CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddSeconds(220));
        snapshot.DurationMs = 120_000;
        snapshot.RunOrigin = "member-invoke";
        snapshot.ActivityInitiator = new WorkflowRunActivityInitiatorSnapshot
        {
            Platform = "nyxid",
            Tenant = "tenant-alpha",
            ExternalUserId = memberIdentityCandidate,
            Scope = CallerScope,
            BindingId = serviceIdentityCandidate,
            DisplayValue = "alice@example.com",
            Availability = "available",
        };
        snapshot.InputSummary = "summarized input";
        snapshot.ActivityCurrentStep = new WorkflowRunActivityStepSnapshot
        {
            StepId = "step-current",
            InputSummary = "current step input",
            Availability = "available",
        };
        snapshot.ActivityFirstFailure = new WorkflowRunActivityFailureSnapshot
        {
            StepId = "step-failed",
            Message = "first failure",
            Availability = "available",
        };
        snapshot.ActivityWaiting = new WorkflowRunActivityWaitingSnapshot
        {
            StepId = "step-waiting",
            WaitingKind = "tool_approval",
            Prompt = "Approve?",
            Availability = "available",
        };
        snapshot.RecoveryCapability = RecoveryCapability();
        snapshot.Lineage = new WorkflowRunLineage
        {
            Availability = WorkflowRunLineageAvailability.Available,
            RetryFork = new WorkflowRunRetryForkLineage
            {
                Availability = WorkflowRunLineageAvailability.Available,
                SourceRunId = "run-source-gamma",
                OriginalRunId = "run-original-alpha",
                Attempt = 2,
                StartAtStepId = "step-failed",
            },
            SubWorkflow = new WorkflowRunSubWorkflowLineage
            {
                Availability = WorkflowRunLineageAvailability.Unavailable,
            },
        };
        var extraSnapshot = Snapshot("actor-beta", CallerScope, WorkflowRunCompletionStatus.Completed, started: 90, updated: 290);
        extraSnapshot.RunId = "run-beta";
        var currentState = new FakeCurrentStateQueryPort
        {
            PageResults =
            [
                new WorkflowActorCurrentStatePage([snapshot], "cursor-next", 42),
                new WorkflowActorCurrentStatePage([extraSnapshot], null, null),
            ],
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var page = await service.ListActivityRunsForScopeAsync(
            CallerScope,
            new WorkflowActivityRunFeedFilter
            {
                Status = "completed",
                WorkflowId = workflowId,
                Origins = ["member-invoke"],
                DefinitionActorIds = ["definition-alpha"],
                ScheduleIds = ["schedule-alpha"],
                FromUtc = DateTimeOffset.UnixEpoch.AddSeconds(10),
                ToUtc = DateTimeOffset.UnixEpoch.AddSeconds(400),
                Take = 1,
                Cursor = "cursor-current",
                IncludeTotalCount = true,
            });

        currentState.PageQueries.Should().HaveCount(2);
        var firstQuery = currentState.PageQueries[0];
        firstQuery.ScopeId.Should().Be(CallerScope);
        firstQuery.WorkflowId.Should().Be(workflowId);
        firstQuery.SearchText.Should().BeEmpty();
        firstQuery.Cursor.Should().Be("cursor-current");
        firstQuery.IncludeTotalCount.Should().BeTrue();
        firstQuery.Take.Should().Be(1);
        var verificationQuery = currentState.PageQueries[1];
        verificationQuery.Cursor.Should().Be("cursor-next");
        verificationQuery.IncludeTotalCount.Should().BeFalse();
        verificationQuery.Take.Should().Be(1);
        page.NextCursor.Should().Be("cursor-next");
        page.HasMore.Should().BeTrue();
        page.TotalCount.Should().Be(42);
        page.Items.Should().ContainSingle();
        var row = page.Items[0];
        row.RunId.Should().Be(runId);
        row.ActorId.Should().Be(actorId);
        row.WorkflowId.Should().Be(workflowId);
        row.Initiator.ExternalUserId.Should().Be(memberIdentityCandidate);
        row.Initiator.BindingId.Should().BeEmpty();
        row.InputSummary.Should().Be("summarized input");
        row.CurrentStep.StepId.Should().Be("step-current");
        row.FirstFailure.StepId.Should().Be("step-failed");
        row.FirstFailure.Message.Should().Be("first failure");
        row.Waiting.StepId.Should().Be("step-waiting");
        row.Waiting.WaitingKind.Should().Be("tool_approval");
        row.StartedAtUtc.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(100));
        row.CompletedAtUtc.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(220));
        row.UpdatedAtUtc.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(300));
        row.DurationMs.Should().Be(120_000);
        row.RecoveryCapability.WorkflowDefinitionRevisionId.Should().Be("rev-recovery");
        row.RecoveryCapability.RetryFailedStep.Eligibility.Should().Be(WorkflowRecoveryEligibility.Eligible);
        row.RecoveryCapability.RetryFailedStep.StartingStepId.Should().Be("step-failed");
        row.Lineage.RetryFork.SourceRunId.Should().Be("run-source-gamma");
        row.Lineage.RetryFork.OriginalRunId.Should().Be("run-original-alpha");
        row.Lineage.RetryFork.StartAtStepId.Should().Be("step-failed");
        row.Lineage.SubWorkflow.Availability.Should().Be(WorkflowRunLineageAvailability.Unavailable);
    }

    [Fact]
    public async Task ListActivityRunsForScopeAsync_ShouldPassSearchTextToCurrentStatePageQuery()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            PageResult = new WorkflowActorCurrentStatePage([], null, null),
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        await service.ListActivityRunsForScopeAsync(
            CallerScope,
            new WorkflowActivityRunFeedFilter
            {
                SearchText = "  Test Member  ",
                Status = "completed",
                WorkflowId = "workflow-alpha",
                Take = 25,
            });

        currentState.PageQueries.Should().HaveCount(1);
        currentState.PageQueries[0].SearchText.Should().Be("Test Member");
        currentState.PageQueries[0].Status.Should().Be("completed");
        currentState.PageQueries[0].WorkflowId.Should().Be("workflow-alpha");
    }

    [Fact]
    public async Task ListActivityRunsForScopeAsync_ShouldRepresentMissingFactsExplicitly()
    {
        var snapshot = Snapshot("actor-legacy", CallerScope, WorkflowRunCompletionStatus.Running, updated: 300);
        snapshot.RunId = string.Empty;
        var currentState = new FakeCurrentStateQueryPort
        {
            PageResult = new WorkflowActorCurrentStatePage([snapshot], null, null),
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var page = await service.ListActivityRunsForScopeAsync(
            CallerScope,
            new WorkflowActivityRunFeedFilter());

        page.HasMore.Should().BeFalse();
        page.TotalCount.Should().BeNull();
        page.Items.Should().ContainSingle();
        var row = page.Items[0];
        row.RunId.Should().BeEmpty();
        row.ActorId.Should().Be("actor-legacy");
        row.WorkflowId.Should().BeEmpty();
        row.Initiator.DisplayValue.Should().Be("Unknown");
        row.Initiator.Availability.Should().Be("unavailable");
        row.CurrentStep.Availability.Should().Be("unavailable");
        row.FirstFailure.Availability.Should().Be("unavailable");
        row.Waiting.Availability.Should().Be("unavailable");
        row.CompletedAtUtc.Should().BeNull();
        row.DurationMs.Should().BeNull();
        row.RecoveryCapability.RetryFailedStep.Should().NotBeNull();
        row.RecoveryCapability.RetryFailedStep.Eligibility.Should().Be(WorkflowRecoveryEligibility.Unavailable);
        row.RecoveryCapability.RetryFailedStep.UnavailableReasonCode.Should()
            .Be(WorkflowRecoveryUnavailableReasonCode.LegacyUnavailable);
        row.RecoveryCapability.RunAgain.Should().NotBeNull();
        row.RecoveryCapability.RunAgain.Eligibility.Should().Be(WorkflowRecoveryEligibility.Unavailable);
        row.RecoveryCapability.RunAgain.UnavailableReasonCode.Should()
            .Be(WorkflowRecoveryUnavailableReasonCode.LegacyUnavailable);
        row.Lineage.Availability.Should().Be(WorkflowRunLineageAvailability.LegacyUnavailable);
    }

    [Fact]
    public async Task ListActivityRunsForScopeAsync_ShouldNormalizeLegacyRecoveryActions_WhenCapabilityExistsWithoutActions()
    {
        var snapshot = Snapshot("actor-legacy-recovery", CallerScope, WorkflowRunCompletionStatus.Completed, updated: 300);
        snapshot.RecoveryCapability = new WorkflowRunRecoveryCapability
        {
            WorkflowDefinitionRevisionId = "rev-legacy",
            WorkflowDefinitionVersion = 3,
        };
        var currentState = new FakeCurrentStateQueryPort
        {
            PageResult = new WorkflowActorCurrentStatePage([snapshot], null, null),
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var page = await service.ListActivityRunsForScopeAsync(
            CallerScope,
            new WorkflowActivityRunFeedFilter());

        var row = page.Items.Should().ContainSingle().Subject;
        row.RecoveryCapability.WorkflowDefinitionRevisionId.Should().Be("rev-legacy");
        row.RecoveryCapability.WorkflowDefinitionVersion.Should().Be(3);
        row.RecoveryCapability.RetryFailedStep.Should().NotBeNull();
        row.RecoveryCapability.RetryFailedStep.Eligibility.Should().Be(WorkflowRecoveryEligibility.Unavailable);
        row.RecoveryCapability.RetryFailedStep.UnavailableReasonCode.Should()
            .Be(WorkflowRecoveryUnavailableReasonCode.LegacyUnavailable);
        row.RecoveryCapability.RetryFailedStep.UnavailableReason.Should()
            .Be("Recovery capability is unavailable for this legacy run.");
        row.RecoveryCapability.RunAgain.Should().NotBeNull();
        row.RecoveryCapability.RunAgain.Eligibility.Should().Be(WorkflowRecoveryEligibility.Unavailable);
        row.RecoveryCapability.RunAgain.UnavailableReasonCode.Should()
            .Be(WorkflowRecoveryUnavailableReasonCode.LegacyUnavailable);
        row.RecoveryCapability.RunAgain.UnavailableReason.Should()
            .Be("Recovery capability is unavailable for this legacy run.");
    }

    [Fact]
    public async Task ListActivityRunsForScopeAsync_ShouldLeaveDurationUnavailable_WhenTerminalRunHasNoStart()
    {
        var completedAt = DateTimeOffset.UnixEpoch.AddSeconds(420);
        var snapshot = Snapshot("actor-terminal-legacy", CallerScope, WorkflowRunCompletionStatus.Completed, updated: 430);
        snapshot.CompletedAtUtc = Timestamp.FromDateTimeOffset(completedAt);
        var currentState = new FakeCurrentStateQueryPort
        {
            PageResult = new WorkflowActorCurrentStatePage([snapshot], null, null),
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var page = await service.ListActivityRunsForScopeAsync(
            CallerScope,
            new WorkflowActivityRunFeedFilter());

        var row = page.Items.Should().ContainSingle().Subject;
        row.CompletedAtUtc.Should().Be(completedAt);
        row.StartedAtUtc.Should().BeNull();
        row.DurationMs.Should().BeNull();
    }

    [Fact]
    public async Task ListActivityRunsForScopeAsync_ShouldNotAdvertiseNextPage_WhenResultExactlyFillsRequestedPage()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            PageResults =
            [
                new WorkflowActorCurrentStatePage(
                    [
                        Snapshot("actor-one", CallerScope, WorkflowRunCompletionStatus.Running),
                        Snapshot("actor-two", CallerScope, WorkflowRunCompletionStatus.Completed),
                    ],
                    "provider-cursor-at-full-page",
                    2),
                new WorkflowActorCurrentStatePage([], null, null),
            ],
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var page = await service.ListActivityRunsForScopeAsync(
            CallerScope,
            new WorkflowActivityRunFeedFilter { Take = 2 });

        currentState.PageQueries.Should().HaveCount(2);
        currentState.PageQueries[0].Take.Should().Be(2);
        currentState.PageQueries[1].Take.Should().Be(1);
        currentState.PageQueries[1].Cursor.Should().Be("provider-cursor-at-full-page");
        page.Items.Should().HaveCount(2);
        page.HasMore.Should().BeFalse();
        page.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task ListActivityRunsForScopeAsync_ShouldDropForeignRowsEvenWhenPageContainsThem()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            PageResult = new WorkflowActorCurrentStatePage(
                [
                    Snapshot("actor-own", CallerScope, WorkflowRunCompletionStatus.Running),
                    Snapshot("actor-foreign", OtherScope, WorkflowRunCompletionStatus.Running),
                ],
                null,
                null),
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var page = await service.ListActivityRunsForScopeAsync(CallerScope, new WorkflowActivityRunFeedFilter());

        page.Items.Should().ContainSingle().Which.ActorId.Should().Be("actor-own");
    }

    [Fact]
    public async Task ListAllActivityRunsAsync_ShouldPageAcrossScopes_WithoutScopeFilter()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            PageResults =
            [
                new WorkflowActorCurrentStatePage(
                    [
                        Snapshot("actor-own", CallerScope, WorkflowRunCompletionStatus.Running),
                        Snapshot("actor-other", OtherScope, WorkflowRunCompletionStatus.Completed),
                    ],
                    "cursor-next",
                    null),
                new WorkflowActorCurrentStatePage([], null, null),
            ],
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort());

        var page = await service.ListAllActivityRunsAsync(new WorkflowActivityRunFeedFilter { Cursor = "cursor-current" });

        currentState.PageQueries.Should().HaveCount(2);
        currentState.PageQueries[0].ScopeId.Should().BeEmpty();
        currentState.PageQueries[0].Cursor.Should().Be("cursor-current");
        currentState.PageQueries[1].ScopeId.Should().BeEmpty();
        currentState.PageQueries[1].Cursor.Should().Be("cursor-next");
        currentState.PageQueries[1].Take.Should().Be(1);
        page.HasMore.Should().BeFalse();
        page.Items.Select(item => item.ScopeId).Should().BeEquivalentTo([CallerScope, OtherScope]);
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
        var snapshot = Snapshot(
            "run-alpha",
            CallerScope,
            WorkflowRunCompletionStatus.Completed,
            started: 100,
            updated: 300,
            actorId: "actor-alpha");
        snapshot.WorkflowId = "wf-alpha";
        snapshot.CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddSeconds(220));
        snapshot.DurationMs = 120_000;
        var currentState = new FakeCurrentStateQueryPort
        {
            SingleResult = snapshot,
        };
        var report = new WorkflowRunReport
        {
            StateVersion = 7,
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

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-alpha");

        detail.Should().NotBeNull();
        detail!.Summary.RunId.Should().Be("run-alpha");
        detail.Summary.WorkflowId.Should().Be("wf-alpha");
        detail.Summary.StartedAtUtc.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(100));
        detail.Summary.CompletedAtUtc.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(220));
        detail.Summary.DurationMs.Should().Be(120_000);
        currentState.ScopedRunGetRequests.Should().ContainSingle().Which.Should().Be((CallerScope, "run-alpha"));
        currentState.RunGetRequests.Should().BeEmpty();
        currentState.GetRequests.Should().BeEmpty();
        artifact.ReportRequests.Should().ContainSingle().Which.Should().Be("actor-alpha");
        artifact.GraphRequests.Should().ContainSingle().Which.Should().Be("actor-alpha");
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
    public async Task GetRunForScopeAsync_ShouldResolveWithinScope_WhenAnotherScopeUsesTheSameRunId()
    {
        var callerRun = Snapshot(
            "shared-run",
            CallerScope,
            WorkflowRunCompletionStatus.Completed,
            actorId: "actor-caller");
        var currentState = new FakeCurrentStateQueryPort
        {
            Snapshots =
            [
                callerRun,
                Snapshot(
                    "shared-run",
                    OtherScope,
                    WorkflowRunCompletionStatus.Completed,
                    actorId: "actor-other"),
            ],
        };
        var artifact = new FakeArtifactQueryPort
        {
            Report = new WorkflowRunReport { StateVersion = callerRun.StateVersion },
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        var detail = await service.GetRunForScopeAsync(CallerScope, "shared-run");

        detail.Should().NotBeNull();
        detail!.Summary.RunId.Should().Be("shared-run");
        detail.Summary.ScopeId.Should().Be(CallerScope);
        currentState.ScopedRunGetRequests.Should().ContainSingle().Which
            .Should().Be((CallerScope, "shared-run"));
        currentState.RunGetRequests.Should().BeEmpty();
        artifact.ReportRequests.Should().ContainSingle().Which.Should().Be("actor-caller");
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldNotTreatActorIdAsRunId()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            SingleResult = Snapshot(
                "run-alpha",
                CallerScope,
                WorkflowRunCompletionStatus.Running,
                actorId: "actor-alpha"),
        };
        var artifact = new FakeArtifactQueryPort();
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        var detail = await service.GetRunForScopeAsync(CallerScope, "actor-alpha");

        detail.Should().BeNull();
        currentState.ScopedRunGetRequests.Should().ContainSingle().Which.Should().Be((CallerScope, "actor-alpha"));
        currentState.RunGetRequests.Should().BeEmpty();
        currentState.GetRequests.Should().BeEmpty();
        artifact.ReportRequests.Should().BeEmpty();
        artifact.GraphRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldExposeTypedOperationsInStableSequenceOrderWithHonestDuration()
    {
        var snapshot = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Running);
        snapshot.InputSummary = "Original user request.";
        var currentState = new FakeCurrentStateQueryPort
        {
            SingleResult = snapshot,
        };
        var report = new WorkflowRunReport
        {
            StateVersion = 7,
            Operations =
            [
                new WorkflowRunOperation
                {
                    SessionId = "session-alpha",
                    OperationId = "model-round-1",
                    ProgressSequence = 0,
                    Round = 1,
                    Kind = WorkflowRuntimeOperationKind.Model,
                    StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(8),
                    CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(7),
                    Model = "deepseek-chat",
                    Output = "done",
                    FinishReason = "stop",
                    Success = true,
                },
                new WorkflowRunOperation
                {
                    SessionId = "session-alpha",
                    OperationId = "call-search-1",
                    ProgressSequence = 20,
                    Kind = WorkflowRuntimeOperationKind.Tool,
                    StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(8),
                    CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(9),
                    RoleActorId = "role-actor-alpha",
                    ToolCallId = "call-search-1",
                    ToolName = "search",
                    ArgumentsJson = "{\"query\":\"status\"}",
                    ResultJson = "{\"healthy\":true}",
                    Success = true,
                },
                new WorkflowRunOperation
                {
                    SessionId = "session-alpha",
                    OperationId = "model-round-0",
                    ProgressSequence = 10,
                    Round = 0,
                    Kind = WorkflowRuntimeOperationKind.Model,
                    StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(10),
                    CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(12.25),
                    RoleActorId = "role-actor-alpha",
                    Model = "deepseek-chat",
                    Provider = "deepseek",
                    InputSummary = "Check the deployment status.",
                    AvailableToolNames = ["search", "status"],
                    Output = string.Empty,
                    ReasoningContent = "A tool is required.",
                    FinishReason = "tool_calls",
                    Usage = new WorkflowRunUsageMetrics
                    {
                        PromptTokens = 13,
                        CompletionTokens = 2,
                        TotalTokens = 15,
                    },
                    Success = true,
                },
            ],
        };
        var service = new WorkflowRunObservatoryQueryService(
            currentState,
            new FakeArtifactQueryPort { Report = report });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.InputSummary.Should().Be("Original user request.",
            "the request Input lane is distinct from every provider invocation input");
        detail.Operations.Select(operation => operation.OperationId).Should().Equal(
            "model-round-0",
            "call-search-1",
            "model-round-1");

        var firstModel = detail.Operations[0];
        firstModel.Kind.Should().Be("model");
        firstModel.Round.Should().Be(0);
        firstModel.ProgressSequence.Should().Be(10);
        firstModel.Model.Should().Be("deepseek-chat");
        firstModel.Provider.Should().Be("deepseek");
        firstModel.InputSummary.Should().Be("Check the deployment status.");
        firstModel.AvailableToolNames.Should().Equal("search", "status");
        firstModel.ReasoningContent.Should().Be("A tool is required.");
        firstModel.FinishReason.Should().Be("tool_calls");
        firstModel.Usage.TotalTokens.Should().Be(15);
        firstModel.DurationMs.Should().Be(2250);

        var tool = detail.Operations[1];
        tool.Kind.Should().Be("tool");
        tool.ToolCallId.Should().Be("call-search-1");
        tool.ToolName.Should().Be("search");
        tool.ArgumentsJson.Should().Be("{\"query\":\"status\"}");
        tool.ResultJson.Should().Be("{\"healthy\":true}");
        tool.DurationMs.Should().Be(1000);

        var invalidModel = detail.Operations[2];
        invalidModel.Kind.Should().Be("model");
        invalidModel.ProgressSequence.Should().Be(0);
        invalidModel.StartedAtUtc.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(8));
        invalidModel.CompletedAtUtc.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(7));
        invalidModel.DurationMs.Should().BeNull(
            "an end timestamp before its start is invalid rather than a zero-duration operation");
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
            StateVersion = 7,
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

    [Fact]
    public async Task GetRunForScopeAsync_ShouldExposeTypedToolApprovalIdentity()
    {
        var snapshot = Snapshot(
            "run-1",
            CallerScope,
            WorkflowRunCompletionStatus.AwaitingToolApproval,
            started: 1,
            updated: 5);
        var currentState = new FakeCurrentStateQueryPort { SingleResult = snapshot };
        var report = new WorkflowRunReport
        {
            StateVersion = 7,
            Steps =
            [
                new WorkflowRunStepTrace
                {
                    StepId = "create_approval",
                    StepType = "tool_call",
                    SuspensionType = "tool_approval",
                    SuspensionPrompt = "Approve tool execution?",
                    ToolApproval = new WorkflowRunToolApproval
                    {
                        ExecutionId = "exec-alpha",
                        ToolName = "nyxid_proxy",
                        ToolCallId = "call-alpha",
                        ApprovalRequestId = "approval-alpha",
                    },
                },
            ],
        };
        var service = new WorkflowRunObservatoryQueryService(
            currentState,
            new FakeArtifactQueryPort { Report = report });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        var approval = detail!.Steps.Should().ContainSingle().Subject;
        detail.Summary.Status.Should().Be("awaiting_tool_approval");
        approval.SuspensionType.Should().Be("tool_approval");
        approval.ToolApproval.Should().NotBeNull();
        approval.ToolApproval!.ExecutionId.Should().Be("exec-alpha");
        approval.ToolApproval.ToolName.Should().Be("nyxid_proxy");
        approval.ToolApproval.ToolCallId.Should().Be("call-alpha");
        approval.ToolApproval.ApprovalRequestId.Should().Be("approval-alpha");
        detail.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "awaiting_tool_approval" &&
            diagnostic.Severity == "info" &&
            diagnostic.StepId == "create_approval");
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldExposeCommittedToolApprovalResumeRejectionDiagnostic()
    {
        var snapshot = Snapshot(
            "run-1",
            CallerScope,
            WorkflowRunCompletionStatus.AwaitingToolApproval,
            started: 1,
            updated: 6);
        var currentState = new FakeCurrentStateQueryPort { SingleResult = snapshot };
        var rejection = TimelineEvent(
            "tool_approval.resume_rejected",
            "Tool approval resume did not match the actor-owned pending approval.",
            "create_approval");
        rejection.StepType = "tool_call";
        rejection.Data["reason"] = "IdentityMismatch";
        var report = new WorkflowRunReport
        {
            StateVersion = 7,
            Timeline = [rejection],
        };
        var service = new WorkflowRunObservatoryQueryService(
            currentState,
            new FakeArtifactQueryPort { Report = report });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "tool_approval_resume_rejected" &&
            diagnostic.Severity == "warning" &&
            diagnostic.Source == "run-report.timeline" &&
            diagnostic.StepId == "create_approval" &&
            diagnostic.StepType == "tool_call");
    }

    // 06-26 detail enrichment: the final result + per-step trace + rollup statistics are surfaced from the
    // committed run-report artifact. Final output/input are NOT truncated; step output is a 240-char preview.
    [Fact]
    public async Task GetRunForScopeAsync_ShouldSurfaceFinalResultStepsAndStatistics_WhenOwned()
    {
        var snapshot = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Completed);
        snapshot.ActivityInitiator = new WorkflowRunActivityInitiatorSnapshot
        {
            Platform = "nyxid",
            ExternalUserId = "m-alpha",
            DisplayValue = "alice@example.com",
            Availability = "available",
        };
        snapshot.InputSummary = "redacted prompt summary";
        var currentState = new FakeCurrentStateQueryPort
        {
            SingleResult = snapshot,
        };
        var report = new WorkflowRunReport
        {
            StateVersion = 7,
            Input = "do the thing",
            FinalOutput = "the thing is done",
            FinalError = string.Empty,
            Steps =
            [
                new WorkflowRunStepTrace
                {
                    StepId = "draft",
                    DisplayName = "Draft answer",
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
        detail!.Initiator.DisplayValue.Should().Be("alice@example.com");
        detail.InputSummary.Should().Be("redacted prompt summary");
        detail.Sections.Overview.VersionStatus.Should().Be(ObservatoryRunDetailSectionVersionStatus.Aligned);
        detail.Sections.Steps.VersionStatus.Should().Be(ObservatoryRunDetailSectionVersionStatus.Aligned);
        detail.Sections.Timeline.VersionStatus.Should().Be(ObservatoryRunDetailSectionVersionStatus.Aligned);
        detail.Input.Should().Be("do the thing");
        detail.FinalOutput.Should().Be("the thing is done");
        detail.FinalError.Should().BeEmpty();

        detail.Steps.Should().ContainSingle();
        var step = detail.Steps[0];
        step.StepId.Should().Be("draft");
        step.DisplayName.Should().Be("Draft answer");
        step.StepType.Should().Be("llm_call");
        step.TargetRole.Should().Be("planner");
        step.Success.Should().BeTrue();
        step.Outcome.Should().Be(WorkflowRunStepOutcome.Succeeded);
        step.OutputPreview.Should().Be("preview of the step output");
        step.DurationMs.Should().Be(2000);
        step.Usage.TotalTokens.Should().Be(10);

        detail.Statistics.TotalSteps.Should().Be(1);
        detail.Statistics.CompletedSteps.Should().Be(1);
        detail.Statistics.RoleReplyCount.Should().Be(1);
        detail.Statistics.StepTypeCounts["llm_call"].Should().Be(1);
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldExposeVersionMismatch_WhenReportVersionDiffersFromSummary()
    {
        var snapshot = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Completed);
        var report = new WorkflowRunReport
        {
            StateVersion = 6,
            ReportVersion = "3.0",
            Input = "stale input",
            FinalOutput = "stale output",
            Steps = [new WorkflowRunStepTrace { StepId = "stale", Success = true }],
            Timeline = [TimelineEvent("workflow.completed", "stale")],
        };
        var service = new WorkflowRunObservatoryQueryService(
            new FakeCurrentStateQueryPort { SingleResult = snapshot },
            new FakeArtifactQueryPort { Report = report });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.Summary.StateVersion.Should().Be(7);
        detail.ReportVersion.Should().Be("3.0");
        detail.Sections.Steps.VersionStatus.Should().Be(ObservatoryRunDetailSectionVersionStatus.VersionMismatch);
        detail.Sections.Steps.DetailStateVersion.Should().Be(7);
        detail.Sections.Steps.SourceStateVersion.Should().Be(6);
        detail.Sections.Timeline.VersionStatus.Should().Be(ObservatoryRunDetailSectionVersionStatus.VersionMismatch);
        detail.Input.Should().BeEmpty();
        detail.FinalOutput.Should().BeEmpty();
        detail.Steps.Should().BeEmpty();
        detail.Timeline.Should().BeEmpty();
        detail.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == "section_version_mismatch" &&
            diagnostic.Source == "read-model.steps" &&
            diagnostic.Message.Contains("does not match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldMapWaitingFailedAndSkippedStepOutcomes()
    {
        var skipped = new WorkflowRunStepTrace
        {
            StepId = "optional_connector",
            CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
            Success = true,
            CompletionAnnotations = new Dictionary<string, string> { ["connector.skipped"] = "true" },
        };
        var service = new WorkflowRunObservatoryQueryService(
            new FakeCurrentStateQueryPort
            {
                SingleResult = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Running),
            },
            new FakeArtifactQueryPort
            {
                Report = new WorkflowRunReport
                {
                    StateVersion = 7,
                    Steps =
                    [
                        new WorkflowRunStepTrace { StepId = "approval", RequestedAt = DateTimeOffset.UnixEpoch, SuspensionType = "tool_approval" },
                        new WorkflowRunStepTrace { StepId = "publish", CompletedAt = DateTimeOffset.UnixEpoch, Success = false },
                        skipped,
                    ],
                },
            });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.Steps.Single(step => step.StepId == "approval").Outcome.Should().Be(WorkflowRunStepOutcome.Waiting);
        detail.Steps.Single(step => step.StepId == "publish").Outcome.Should().Be(WorkflowRunStepOutcome.Failed);
        detail.Steps.Single(step => step.StepId == "optional_connector").Outcome.Should().Be(WorkflowRunStepOutcome.Skipped);
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldLabelRetainedFailureAsLatestAttempt_WhenRetryIsWaiting()
    {
        var service = new WorkflowRunObservatoryQueryService(
            new FakeCurrentStateQueryPort
            {
                SingleResult = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Running),
            },
            new FakeArtifactQueryPort
            {
                Report = new WorkflowRunReport
                {
                    StateVersion = 7,
                    ReportVersion = "3.1",
                    Steps =
                    [
                        new WorkflowRunStepTrace
                        {
                            StepId = "send_email",
                            StepType = "connector_retry",
                            TargetRole = "retry-mailer",
                            RequestedAt = DateTimeOffset.UnixEpoch.AddSeconds(10),
                            CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(11),
                            Success = false,
                            Outcome = WorkflowRunStepOutcome.Waiting,
                            Error = "stale mixed error must not escape",
                            FailureOutput = "stale mixed output must not escape",
                            RequestParameters = new Dictionary<string, string>
                            {
                                ["attempt_identity"] = "current-retry",
                            },
                            LatestFailedAttempt = new WorkflowRunFailedStepAttempt
                            {
                                StepType = "tool_call",
                                TargetRole = "original-mailer",
                                RequestedAt = DateTimeOffset.UnixEpoch.AddSeconds(2),
                                CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(3),
                                Success = false,
                                Error = "SMTP connection refused",
                                FailureOutput = "retryable transport error",
                                RequestParameters = new Dictionary<string, string>
                                {
                                    ["attempt_identity"] = "failed-original",
                                },
                            },
                        },
                    ],
                },
            });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.ReportVersion.Should().Be("3.1");
        var step = detail.Steps.Should().ContainSingle().Subject;
        step.Outcome.Should().Be(WorkflowRunStepOutcome.Waiting);
        step.StepType.Should().Be("connector_retry");
        step.TargetRole.Should().Be("retry-mailer");
        step.RequestedAtUtc.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(10));
        step.CompletedAtUtc.Should().BeNull();
        step.Success.Should().BeNull();
        step.Error.Should().BeEmpty();
        step.FailureOutput.Should().BeEmpty();
        step.RequestParameters.Should().Contain("attempt_identity", "current-retry");
        step.LatestFailedAttempt.Should().NotBeNull();
        step.LatestFailedAttempt!.StepType.Should().Be("tool_call");
        step.LatestFailedAttempt.TargetRole.Should().Be("original-mailer");
        step.LatestFailedAttempt.RequestedAtUtc.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(2));
        step.LatestFailedAttempt.CompletedAtUtc.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(3));
        step.LatestFailedAttempt.DurationMs.Should().Be(1000);
        step.LatestFailedAttempt.RequestParameters.Should().Contain("attempt_identity", "failed-original");
        detail.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == "step_retry_waiting_after_failure" &&
            diagnostic.Severity == "warning" &&
            diagnostic.StepId == "send_email" &&
            diagnostic.StepType == "tool_call" &&
            diagnostic.TargetRole == "original-mailer" &&
            diagnostic.TimestampUtc == DateTimeOffset.UnixEpoch.AddSeconds(3) &&
            diagnostic.Source == "run-report.step.latest-failed-attempt" &&
            diagnostic.Hint.Contains("latest failed attempt", StringComparison.Ordinal));
        detail.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == "step_retry_waiting_failure_output" &&
            diagnostic.Message == "retryable transport error");
        detail.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.StepId == "send_email" &&
            (diagnostic.Code == "step_failed" || diagnostic.Code == "step_failure_output"));
        detail.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Message.Contains("stale mixed", StringComparison.Ordinal));
    }

    [Fact]
    public void ObservatoryStepDetail_ShouldSerializeOutcomeAsContractValue()
    {
        var json = JsonSerializer.Serialize(new ObservatoryStepDetail
        {
            StepId = "draft",
            Outcome = WorkflowRunStepOutcome.Succeeded,
        });

        json.Should().Contain("\"Outcome\":\"succeeded\"");
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldFallBackToSummary_WhenReportNotYetMaterialized()
    {
        var snapshot = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Running);
        snapshot.RecoveryCapability = RecoveryCapability();
        var currentState = new FakeCurrentStateQueryPort
        {
            SingleResult = snapshot,
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, new FakeArtifactQueryPort { Report = null });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.ReportVersion.Should().BeEmpty();
        detail.Sections.Steps.VersionStatus.Should().Be(ObservatoryRunDetailSectionVersionStatus.Unavailable);
        detail.Sections.Steps.DetailStateVersion.Should().Be(7);
        detail.Sections.Steps.SourceStateVersion.Should().Be(0);
        detail.Sections.Timeline.VersionStatus.Should().Be(ObservatoryRunDetailSectionVersionStatus.Unavailable);
        detail.Sections.ExecutionPath.VersionStatus.Should().Be(ObservatoryRunDetailSectionVersionStatus.Unavailable);
        detail.Sections.ExecutionPath.DetailStateVersion.Should().Be(7);
        detail.Sections.ExecutionPath.SourceStateVersion.Should().Be(0);
        detail.Timeline.Should().BeEmpty();
        detail.UsageTotals.TotalTokens.Should().Be(0);
        // Enriched fields degrade to empty when the report artifact has not materialized yet.
        detail.FinalOutput.Should().BeEmpty();
        detail.Steps.Should().BeEmpty();
        detail.Statistics.TotalSteps.Should().Be(0);
        detail.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == "section_unavailable" &&
            diagnostic.Source == "read-model.steps" &&
            diagnostic.Message == "Run report artifact has not materialized.");
        detail.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == "section_unavailable" &&
            diagnostic.Source == "read-model.timeline");
        detail.RecoveryCapability.WorkflowDefinitionRevisionId.Should().Be("rev-recovery");
        detail.RecoveryCapability.RetryFailedStep.Eligibility.Should().Be(WorkflowRecoveryEligibility.Eligible);
    }

    [Fact]
    public async Task GetRunForScopeAsync_ShouldExposeRecoveryCapability_WhenReportIsMaterialized()
    {
        var snapshot = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Failed);
        snapshot.RecoveryCapability = RecoveryCapability();
        var service = new WorkflowRunObservatoryQueryService(
            new FakeCurrentStateQueryPort { SingleResult = snapshot },
            new FakeArtifactQueryPort
            {
                Report = new WorkflowRunReport { StateVersion = 7, ReportVersion = "3.0", FinalError = "boom" },
            });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.RecoveryCapability.WorkflowDefinitionRevisionId.Should().Be("rev-recovery");
        detail.ReportVersion.Should().Be("3.0");
        detail.RecoveryCapability.WorkflowDefinitionVersion.Should().Be(12);
        detail.RecoveryCapability.RetryFailedStep.RecommendedActions.Should().ContainSingle()
            .Which.Should().Be(WorkflowRecoveryRecommendedAction.Retry);
        detail.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "failure_evidence_schema_legacy" &&
            diagnostic.Severity == "warning" &&
            diagnostic.Source == "run-report.schema" &&
            diagnostic.Message.Contains("'3.0'", StringComparison.Ordinal) &&
            diagnostic.Hint.Contains("repair or reprojection", StringComparison.Ordinal));
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
            StateVersion = 7,
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
    public async Task GetRunForScopeAsync_ShouldExposeCompleteStructuredFailureEvidence()
    {
        var snapshot = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Failed, started: 1, updated: 9);
        snapshot.CompilationError = "line 7: unknown step type";
        snapshot.ActivityFirstFailure = new WorkflowRunActivityFailureSnapshot
        {
            StepId = "normalize_person",
            Message = "normalization failed",
            Availability = "available",
        };
        snapshot.RecoveryCapability = new WorkflowRunRecoveryCapability
        {
            RetryFailedStep = new WorkflowRecoveryActionCapability
            {
                Eligibility = WorkflowRecoveryEligibility.Ineligible,
                UnavailableReasonCode = WorkflowRecoveryUnavailableReasonCode.ConfigurationFailure,
                UnavailableReason = "Fix the code before retrying.",
                StartingStepId = "normalize_person",
                RecommendedActions = { WorkflowRecoveryRecommendedAction.ChangeConfiguration },
            },
            RunAgain = new WorkflowRecoveryActionCapability
            {
                Eligibility = WorkflowRecoveryEligibility.Unavailable,
                UnavailableReasonCode = WorkflowRecoveryUnavailableReasonCode.AuthorizationFailure,
                UnavailableReason = "Execution access is no longer available.",
                RecommendedActions = { WorkflowRecoveryRecommendedAction.FixAccess },
            },
        };
        var fileItemResults = new WorkflowFileItemResultSet
        {
            SourceResultCount = 50,
            ResultsTruncated = true,
            Results =
            {
                new WorkflowFileItemResult
                {
                    Index = 1,
                    Success = false,
                    Output = "partial row",
                    OutputTruncated = true,
                    Error = "row parse failed",
                    ErrorTruncated = true,
                    FileRef = new WorkflowFileRef
                    {
                        FileId = "file-alpha",
                        ArtifactId = "workflow-file://file-alpha",
                        SourceKind = WorkflowFileSourceKind.ChatInput,
                        FileName = "people.csv",
                    },
                },
            },
        };
        var voteDecision = new VoteAgreementDecision
        {
            Kind = AgreementDecisionKind.Inconclusive,
            BranchKey = "needs-review",
            WinnerCandidateId = "candidate-alpha",
            Output = "no agreement",
            OutputTruncated = true,
            Reason = "quorum not reached",
            ReasonTruncated = true,
        };
        voteDecision.LabelCounts["approve"] = 1;
        var failedTimeline = TimelineEvent(
            "step.failed",
            "stderr: SyntaxError on line 12",
            stepId: "normalize_person");
        failedTimeline.StepType = "tool_call";
        failedTimeline.Data["error"] = "stderr: SyntaxError on line 12";
        var report = new WorkflowRunReport
        {
            StateVersion = 7,
            ReportVersion = "3.1",
            FinalError = "code_execute failed",
            Steps =
            [
                new WorkflowRunStepTrace
                {
                    StepId = "normalize_person",
                    DisplayName = "Normalize person",
                    StepType = "tool_call",
                    TargetRole = "normalizer",
                    RequestedAt = DateTimeOffset.UnixEpoch.AddSeconds(2),
                    CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(3),
                    Success = false,
                    WorkerId = "worker-alpha",
                    OutputPreview = "stderr: SyntaxError",
                    Error = "code_execute failed",
                    FailureOutput = "stderr: SyntaxError on line 12 near unexpected token",
                    FailureOutputTruncated = true,
                    FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
                    RecoveryFailureKind = WorkflowRecoveryFailureKind.ConfigurationFailure,
                    RetryDisposition = WorkflowStepRetryDisposition.Forbidden,
                    FileItemResults = fileItemResults,
                    VoteAgreementDecision = voteDecision,
                    RequestParameters = new Dictionary<string, string> { ["language"] = "python" },
                    CompletionAnnotations = new Dictionary<string, string> { ["exit_code"] = "1" },
                    AssignedVariable = "normalized_person",
                    AssignedValue = "none",
                    RequestedVariableName = "person",
                },
            ],
            Operations =
            [
                FailedOperation("operation-alpha"),
                FailedOperation("operation-beta"),
            ],
            Timeline = [failedTimeline],
        };
        var service = new WorkflowRunObservatoryQueryService(
            new FakeCurrentStateQueryPort { SingleResult = snapshot },
            new FakeArtifactQueryPort { Report = report });

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.ReportVersion.Should().Be("3.1");
        detail.CompilationError.Should().Be("line 7: unknown step type");
        detail.FirstFailure.StepId.Should().Be("normalize_person");
        detail.FirstFailure.Message.Should().Be("normalization failed");
        var step = detail.Steps.Should().ContainSingle().Subject;
        step.WorkerId.Should().Be("worker-alpha");
        step.FailureOutput.Should().Contain("SyntaxError on line 12");
        step.FailureOutputTruncated.Should().BeTrue();
        step.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        step.RecoveryFailureKind.Should().Be(WorkflowRecoveryFailureKind.ConfigurationFailure);
        step.RetryDisposition.Should().Be(WorkflowStepRetryDisposition.Forbidden);
        step.CompletionAnnotations["exit_code"].Should().Be("1");
        step.AssignedVariable.Should().Be("normalized_person");
        step.AssignedValue.Should().Be("none");
        step.RequestedVariableName.Should().Be("person");
        step.FileItemResults!.Results.Should().ContainSingle(item =>
            item.Index == 1 &&
            item.FileRef!.FileId == "file-alpha" &&
            item.FileRef.SourceKind == WorkflowFileSourceKind.ChatInput &&
            item.OutputTruncated &&
            item.Error == "row parse failed" &&
            item.ErrorTruncated);
        step.FileItemResults.SourceResultCount.Should().Be(50);
        step.FileItemResults.ResultsTruncated.Should().BeTrue();
        step.VoteAgreementDecision!.Kind.Should().Be(AgreementDecisionKind.Inconclusive);
        step.VoteAgreementDecision.OutputTruncated.Should().BeTrue();
        step.VoteAgreementDecision.ReasonTruncated.Should().BeTrue();
        step.VoteAgreementDecision.LabelCounts["approve"].Should().Be(1);
        detail.Timeline.Should().ContainSingle(item =>
            item.Kind == "RunError" &&
            item.Message == "stderr: SyntaxError on line 12" &&
            item.Data["error"] == "stderr: SyntaxError on line 12");
        detail.Diagnostics.Should().Contain(item => item.Code == "compilation_error");
        detail.Diagnostics.Should().Contain(item => item.Code == "activity_first_failure");
        detail.Diagnostics.Should().Contain(item =>
            item.Code == "step_failure_output" && item.Message.Contains("unexpected token", StringComparison.Ordinal));
        detail.Diagnostics.Count(item => item.Code == "operation_failed").Should().Be(2);
        detail.Diagnostics.Should().Contain(item =>
            item.Code == "recovery_retry_failed_step_blocked" && item.StepId == "normalize_person");
        detail.Diagnostics.Should().Contain(item => item.Code == "recovery_run_again_blocked");
        detail.Diagnostics.Should().NotContain(item => item.Code == "failure_evidence_schema_legacy");

        var json = JsonSerializer.Serialize(step, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().Contain("\"failureOutcome\":\"OutcomeUncertain\"");
        json.Should().Contain("\"recoveryFailureKind\":\"ConfigurationFailure\"");
        json.Should().Contain("\"retryDisposition\":\"Forbidden\"");
        json.Should().Contain("\"fileItemResults\":{\"results\":[");
        json.Should().Contain("\"sourceKind\":\"ChatInput\"");
        json.Should().Contain("\"outputTruncated\":true");
        json.Should().Contain("\"errorTruncated\":true");
        json.Should().Contain("\"voteAgreementDecision\":{\"kind\":\"Inconclusive\"");
        json.Should().Contain("\"reasonTruncated\":true");
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
        detail.Diagnostics.Should().Contain(log =>
            log.Code == "failure_evidence_schema_legacy" &&
            log.Message.Contains("missing", StringComparison.Ordinal));
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
        var subgraph = new WorkflowRunGraphExportSubgraph { RootNodeId = "run-1", SourceStateVersion = 7 };
        subgraph.Nodes.Add(new WorkflowRunGraphExportNode { NodeId = "n1", NodeType = "role" });
        subgraph.Edges.Add(new WorkflowRunGraphExportEdge { EdgeId = "e1", FromNodeId = "run-1", ToNodeId = "n1", EdgeType = "child" });
        var artifact = new FakeArtifactQueryPort { Subgraph = subgraph };
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        var graph = await service.GetRunGraphForScopeAsync(CallerScope, "run-1");

        graph.Should().NotBeNull();
        graph!.RootNodeId.Should().Be("run-1");
        graph.VersionStatus.Should().Be(ObservatoryRunDetailSectionVersionStatus.Aligned);
        graph.SourceStateVersion.Should().Be(7);
        graph.Nodes.Should().ContainSingle().Which.NodeId.Should().Be("n1");
        graph.Edges.Should().ContainSingle().Which.EdgeType.Should().Be("child");
    }

    [Fact]
    public async Task GetRunForScopeAsync_WhenGraphExportDisabled_ShouldNotEmitGraphUnavailableWarning()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            SingleResult = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Completed),
        };
        var artifact = new FakeArtifactQueryPort
        {
            WorkflowGraphExportEnabled = false,
            Report = new WorkflowRunReport { StateVersion = 7 },
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

        detail.Should().NotBeNull();
        detail!.Sections.ExecutionPath.VersionStatus
            .Should().Be(ObservatoryRunDetailSectionVersionStatus.Disabled);
        detail.ExecutionPath.VersionStatus
            .Should().Be(ObservatoryRunDetailSectionVersionStatus.Disabled);
        detail.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Source == "read-model.execution_path" ||
            diagnostic.Message == "Execution path graph source version is unavailable.");
        artifact.GraphRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRunGraphForScopeAsync_ShouldExposeMismatchAndHideStaleGraph_WhenSourceVersionDiffers()
    {
        var currentState = new FakeCurrentStateQueryPort
        {
            SingleResult = Snapshot("run-1", CallerScope, WorkflowRunCompletionStatus.Running),
        };
        var subgraph = new WorkflowRunGraphExportSubgraph { RootNodeId = "run-1", SourceStateVersion = 6 };
        subgraph.Nodes.Add(new WorkflowRunGraphExportNode { NodeId = "stale-node", NodeType = "WorkflowRun" });
        var service = new WorkflowRunObservatoryQueryService(
            currentState,
            new FakeArtifactQueryPort { Subgraph = subgraph });

        var graph = await service.GetRunGraphForScopeAsync(CallerScope, "run-1");

        graph.Should().NotBeNull();
        graph!.VersionStatus.Should().Be(ObservatoryRunDetailSectionVersionStatus.VersionMismatch);
        graph.DetailStateVersion.Should().Be(7);
        graph.SourceStateVersion.Should().Be(6);
        graph.Nodes.Should().BeEmpty();
        graph.Edges.Should().BeEmpty();
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
        var subgraph = new WorkflowRunGraphExportSubgraph { RootNodeId = "run-1", SourceStateVersion = 7 };
        var stepNode = new WorkflowRunGraphExportNode { NodeId = "step:run-1:cmd:answer", NodeType = "WorkflowStep" };
        stepNode.Properties.Add("stepId", "answer");
        stepNode.Properties.Add("displayName", "Answer customer");
        subgraph.Nodes.Add(stepNode);
        subgraph.Nodes.Add(new WorkflowRunGraphExportNode { NodeId = "run:run-1:cmd", NodeType = "WorkflowRun" });
        var artifact = new FakeArtifactQueryPort { Subgraph = subgraph };
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        var graph = await service.GetRunGraphForScopeAsync(CallerScope, "run-1");

        graph.Should().NotBeNull();
        graph!.Nodes.Single(node => node.NodeType == "WorkflowStep").StepId.Should().Be("answer");
        graph.Nodes.Single(node => node.NodeType == "WorkflowStep").DisplayName.Should().Be("Answer customer");
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
            Report = new WorkflowRunReport { StateVersion = 7, FinalOutput = "done" },
        };
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        var detail = await service.GetRunAsync("run-foreign");

        detail.Should().NotBeNull();
        detail!.Summary.RunId.Should().Be("run-foreign");
        detail.Summary.ScopeId.Should().Be(OtherScope);
        detail.FinalOutput.Should().Be("done");
        currentState.RunGetRequests.Should().ContainSingle().Which.Should().Be("run-foreign");
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
        var subgraph = new WorkflowRunGraphExportSubgraph { RootNodeId = "run-foreign", SourceStateVersion = 7 };
        subgraph.Nodes.Add(new WorkflowRunGraphExportNode { NodeId = "run-foreign", NodeType = "WorkflowRun" });
        var artifact = new FakeArtifactQueryPort { Subgraph = subgraph };
        var service = new WorkflowRunObservatoryQueryService(currentState, artifact);

        var graph = await service.GetRunGraphAsync("run-foreign");

        graph.Should().NotBeNull();
        graph!.RootNodeId.Should().Be("run-foreign");
        currentState.RunGetRequests.Should().ContainSingle().Which.Should().Be("run-foreign");
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
        long updated = 0,
        string? actorId = null)
    {
        var snapshot = new WorkflowActorSnapshot
        {
            ActorId = actorId ?? runId,
            RunId = runId,
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

    private static WorkflowRunRecoveryCapability RecoveryCapability()
    {
        var capability = new WorkflowRunRecoveryCapability
        {
            WorkflowDefinitionRevisionId = "rev-recovery",
            WorkflowDefinitionVersion = 12,
            RetryFailedStep = new WorkflowRecoveryActionCapability
            {
                Eligibility = WorkflowRecoveryEligibility.Eligible,
                UnavailableReasonCode = WorkflowRecoveryUnavailableReasonCode.None,
                StartingStepId = "step-failed",
                ReusesPriorStepOutputs = true,
                MayIncurModelOrToolCost = true,
            },
            RunAgain = new WorkflowRecoveryActionCapability
            {
                Eligibility = WorkflowRecoveryEligibility.Eligible,
                UnavailableReasonCode = WorkflowRecoveryUnavailableReasonCode.None,
                StartingStepId = "step-a",
                MayIncurModelOrToolCost = true,
            },
        };
        capability.RetryFailedStep.RecommendedActions.Add(WorkflowRecoveryRecommendedAction.Retry);
        capability.RunAgain.RecommendedActions.Add(WorkflowRecoveryRecommendedAction.RunAgain);
        return capability;
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

    private static WorkflowRunOperation FailedOperation(string operationId) =>
        new()
        {
            SessionId = "session-alpha",
            OperationId = operationId,
            Kind = WorkflowRuntimeOperationKind.Tool,
            StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(2),
            CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(3),
            Success = false,
            Error = "provider operation failed",
            ToolName = "code_execute",
        };

    private sealed class FakeCurrentStateQueryPort : IWorkflowExecutionCurrentStateQueryPort
    {
        public bool WorkflowActorCurrentStateQueryEnabled => true;
        public IReadOnlyList<WorkflowActorSnapshot> ListResult { get; init; } = [];
        public WorkflowActorCurrentStatePage PageResult { get; init; } = new([], null, null);
        public IReadOnlyList<WorkflowActorCurrentStatePage> PageResults { get; init; } = [];
        public WorkflowActorSnapshot? SingleResult { get; init; }
        public IReadOnlyList<WorkflowActorSnapshot> Snapshots { get; init; } = [];
        public WorkflowActorCurrentStateListQuery? LastListQuery { get; private set; }
        public WorkflowActorCurrentStateListQuery? LastPageQuery { get; private set; }
        public List<WorkflowActorCurrentStateListQuery> PageQueries { get; } = [];
        public List<string> GetRequests { get; } = [];
        public List<string> RunGetRequests { get; } = [];
        public List<(string ScopeId, string RunId)> ScopedRunGetRequests { get; } = [];

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

        public Task<WorkflowActorSnapshot?> GetWorkflowRunCurrentStateAsync(
            string runId,
            CancellationToken ct = default)
        {
            RunGetRequests.Add(runId);
            if (SingleResult != null)
            {
                return Task.FromResult(string.Equals(SingleResult.RunId, runId, StringComparison.Ordinal)
                    ? SingleResult
                    : null);
            }

            return Task.FromResult(Snapshots.FirstOrDefault(snapshot =>
                string.Equals(snapshot.RunId, runId, StringComparison.Ordinal)));
        }

        public Task<WorkflowActorSnapshot?> GetWorkflowRunCurrentStateForScopeAsync(
            string scopeId,
            string runId,
            CancellationToken ct = default)
        {
            ScopedRunGetRequests.Add((scopeId, runId));
            if (SingleResult != null)
            {
                return Task.FromResult(
                    string.Equals(SingleResult.ScopeId, scopeId, StringComparison.Ordinal) &&
                    string.Equals(SingleResult.RunId, runId, StringComparison.Ordinal)
                        ? SingleResult
                        : null);
            }

            var exactMatches = Snapshots
                .Where(snapshot =>
                    string.Equals(snapshot.ScopeId, scopeId, StringComparison.Ordinal) &&
                    string.Equals(snapshot.RunId, runId, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            return Task.FromResult(exactMatches.Length == 1 ? exactMatches[0] : null);
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

        public Task<WorkflowActorCurrentStatePage> PageWorkflowActorCurrentStatesAsync(
            WorkflowActorCurrentStateListQuery query,
            CancellationToken ct = default)
        {
            LastPageQuery = query;
            PageQueries.Add(query);
            var index = PageQueries.Count - 1;
            return Task.FromResult(index < PageResults.Count ? PageResults[index] : PageResult);
        }

        public Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult<WorkflowActorProjectionState?>(null);
    }

    private sealed class FakeArtifactQueryPort : IWorkflowExecutionArtifactQueryPort
    {
        public bool WorkflowArtifactQueryEnabled => true;
        public bool WorkflowGraphExportEnabled { get; init; } = true;
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
