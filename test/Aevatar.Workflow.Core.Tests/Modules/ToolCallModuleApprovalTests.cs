using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class ToolCallModuleApprovalTests
{
    [Fact]
    public async Task PendingApproval_ShouldPersistStateAndPublishSuspensionWithoutCompletion()
    {
        var pending = new WorkflowToolApprovalPendingOutcome(
            ApprovalRequestId: "approval-1",
            ToolName: "danger",
            ToolCallId: "workflow:run-1:danger_step:exec-1",
            ArgumentsJson: """{"danger":true}""",
            ApprovalMode: "AlwaysRequire",
            IsReadOnly: false,
            IsDestructive: true);
        var tool = new ScriptedWorkflowTool("danger", _ => new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var fileRef = BuildWorkflowFileRef("file-approval");

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            "danger_step",
            """{"danger":true}""",
            "exec-1",
            [fileRef],
            "idem-approval-1");
        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            "danger_step",
            """{"danger":true}""",
            "exec-1",
            [fileRef],
            "idem-approval-1");

        tool.Requests.Should().ContainSingle();
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallStartedEvent>()
            .Should().ContainSingle().Which.ArgumentsJson.Should().BeEmpty();
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Should().BeEmpty();
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
        var suspended = ctx.Published.Select(x => x.Event).OfType<WorkflowSuspendedEvent>().Should().ContainSingle().Subject;
        suspended.RunId.Should().Be("run-1");
        suspended.StepId.Should().Be("danger_step");
        suspended.SuspensionType.Should().Be("tool_approval");
        suspended.ToolApproval.Should().NotBeNull();
        suspended.ToolApproval.ExecutionId.Should().Be("exec-1");
        suspended.ToolApproval.ToolName.Should().Be("danger");
        suspended.ToolApproval.ToolCallId.Should().Be("workflow:run-1:danger_step:exec-1");
        suspended.ToolApproval.ApprovalRequestId.Should().Be("approval-1");
        ctx.Published.Single(x => x.Event is WorkflowSuspendedEvent)
            .Direction.Should().Be(TopologyAudience.Self);
        var state = ctx.LoadState<ToolCallModuleState>("tool_call");
        state.PendingApprovals.Should().ContainKey("run-1:danger_step:exec-1:workflow:run-1:danger_step:exec-1:approval-1");
        var pendingState = state.PendingApprovals.Values.Should().ContainSingle().Subject;
        pendingState.ProtectedMaterialReference.Should().NotBeNull();
        pendingState.ProtectedMaterialDigestSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        pendingState.ExecutionPhase.Should().Be(WorkflowToolCallExecutionPhase.ApprovalPending);
        pendingState.TimeoutMs.Should().BeGreaterThan(0);
        pendingState.TimeoutDeadlineUnixMs.Should().BeGreaterThan(ctx.UtcNow.ToUnixTimeMilliseconds());
        pendingState.ContinuationToken.Should().NotBeNullOrWhiteSpace();
        pendingState.Attempt.Should().Be(1);
        pendingState.ArgumentsJson.Should().BeEmpty();
        pendingState.Input.Should().BeEmpty();
        pendingState.InputFileRefs.Should().BeEmpty();
        pendingState.IdempotencyKey.Should().BeEmpty();
        pendingState.ExternalInvocation.Should().BeNull();
        pendingState.DisplayName.Should().BeEmpty();
    }

    [Fact]
    public async Task ApprovedResume_AfterOriginalDeadline_ShouldFailWithoutRedispatch()
    {
        var pending = new WorkflowToolApprovalPendingOutcome(
            ApprovalRequestId: "approval-1",
            ToolName: "danger",
            ToolCallId: "workflow:run-1:danger_step:exec-1",
            ArgumentsJson: "{}",
            ApprovalMode: "AlwaysRequire",
            IsReadOnly: false,
            IsDestructive: true);
        var tool = new ScriptedWorkflowTool(
            "danger",
            request => request.ApprovalGrant is null
                ? new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending)
                : WorkflowToolExecutionResult.Success("{}"));
        var module = CreateModule(tool);
        var now = new DateTimeOffset(2026, 8, 14, 1, 2, 3, TimeSpan.Zero);
        var ctx = new RecordingWorkflowContext { CurrentUtcNow = now };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            "danger_step",
            "{}",
            "exec-1",
            timeoutMs: 500);
        var approval = ctx.LoadState<ToolCallModuleState>("tool_call")
            .PendingApprovals.Values.Should().ContainSingle().Subject.Clone();
        approval.TimeoutMs.Should().Be(500);
        approval.TimeoutDeadlineUnixMs.Should().Be(now.AddMilliseconds(500).ToUnixTimeMilliseconds());
        ctx.Published.Clear();
        ctx.CurrentUtcNow = now.AddMilliseconds(500);

        await HandleAndDrainAsync(
            module,
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "danger_step",
                Approved = true,
                ToolApproval = new WorkflowToolApprovalResume
                {
                    ExecutionId = "exec-1",
                    ToolCallId = "workflow:run-1:danger_step:exec-1",
                    ApprovalRequestId = "approval-1",
                },
            }),
            ctx);

        tool.Requests.Should().ContainSingle();
        var completed = ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>().Should().ContainSingle().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("tool_approval_deadline_exceeded");
        completed.RetryDisposition.Should().Be(WorkflowStepRetryDisposition.Forbidden);
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().BeEmpty();
    }

    [Fact]
    public async Task ApprovedResume_ShouldReplayOriginalToolArgumentsWithTypedGrantAndClearPendingState()
    {
        var issuedAt = new DateTimeOffset(2026, 7, 31, 10, 11, 12, TimeSpan.Zero);
        var pending = new WorkflowToolApprovalPendingOutcome(
            ApprovalRequestId: "approval-1",
            ToolName: "danger",
            ToolCallId: "workflow:run-1:danger_step:exec-1",
            ArgumentsJson: """{"danger":true}""",
            ApprovalMode: "AlwaysRequire",
            IsReadOnly: false,
            IsDestructive: true);
        var tool = new ScriptedWorkflowTool(
            "danger",
            request => request.ApprovalGrant is null
                ? new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending)
                : WorkflowToolExecutionResult.Success("""{"executed":true}"""));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var fileRef = BuildWorkflowFileRef("file-replay");

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            "danger_step",
            """{"danger":true}""",
            "exec-1",
            [fileRef],
            "idem-approval-1",
            issuedAt,
            displayName: "Dangerous step");
        var pendingState = ctx.LoadState<ToolCallModuleState>("tool_call")
            .PendingApprovals.Values.Should().ContainSingle().Subject;
        pendingState.IssuedAtUnixMs.Should().Be(issuedAt.ToUnixTimeMilliseconds());
        pendingState.DisplayName.Should().BeEmpty();
        ctx.Published.Clear();

        var resumed = new WorkflowResumedEvent
        {
            RunId = "run-1",
            StepId = "danger_step",
            Approved = true,
            ToolApproval = new WorkflowToolApprovalResume
            {
                ExecutionId = "exec-1",
                ToolCallId = "workflow:run-1:danger_step:exec-1",
                ApprovalRequestId = "approval-1",
            },
        };
        await HandleAndDrainAsync(
            module,
            Envelope(resumed),
            ctx);
        await HandleAndDrainAsync(
            module,
            Envelope(resumed),
            ctx);

        tool.Requests.Should().HaveCount(2);
        tool.Requests[1].ArgumentsJson.Should().Be("""{"danger":true}""");
        tool.Requests.Select(request => request.IssuedAtUnixMs)
            .Should().OnlyContain(value => value == issuedAt.ToUnixTimeMilliseconds());
        tool.Requests.Count(request => request.ApprovalGrant is not null).Should().Be(1);
        tool.Requests[1].InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-replay");
        tool.Requests[1].IdempotencyKey.Should().Be("idem-approval-1");
        tool.Requests[1].ApprovalGrant.Should().NotBeNull();
        var grant = tool.Requests[1].ApprovalGrant!;
        grant.ApprovalRequestId.Should().Be("approval-1");
        grant.ToolName.Should().Be("danger");
        grant.ToolCallId.Should().Be("workflow:run-1:danger_step:exec-1");
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Single().Success.Should().BeTrue();
        var completed = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("""{"executed":true}""");
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolApprovalResumeRejectedEvent>().Should().BeEmpty();
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().BeEmpty();
    }

    [Fact]
    public async Task CompletedApproval_ShouldOnlyDedupeTheExactIdentityAndDecision()
    {
        var pending = new WorkflowToolApprovalPendingOutcome(
            ApprovalRequestId: "approval-1",
            ToolName: "danger",
            ToolCallId: "workflow:run-1:danger_step:exec-1",
            ArgumentsJson: "{}",
            ApprovalMode: "AlwaysRequire",
            IsReadOnly: false,
            IsDestructive: true);
        var tool = new ScriptedWorkflowTool(
            "danger",
            request => request.ApprovalGrant is null
                ? new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending)
                : WorkflowToolExecutionResult.Success("{}"));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        await ExecuteToolCallAsync(module, ctx, tool.Name, "danger_step", "{}", "exec-1");
        var exact = new WorkflowResumedEvent
        {
            RunId = "run-1",
            StepId = "danger_step",
            Approved = true,
            ToolApproval = new WorkflowToolApprovalResume
            {
                ExecutionId = "exec-1",
                ToolCallId = "workflow:run-1:danger_step:exec-1",
                ApprovalRequestId = "approval-1",
            },
        };
        await HandleAndDrainAsync(module, Envelope(exact), ctx);
        var reloaded = ToolCallModuleState.Parser.ParseFrom(
            ctx.LoadState<ToolCallModuleState>("tool_call").ToByteArray());
        await ctx.SaveStateAsync("tool_call", reloaded);
        var tombstone = reloaded.CompletionTombstones.Should().ContainSingle().Subject.Value;
        tombstone.ApprovalRequestId.Should().Be("approval-1");
        tombstone.TerminalDecision.Should().Be(WorkflowToolCallTerminalDecision.Approved);
        ctx.Published.Clear();

        await HandleAndDrainAsync(module, Envelope(exact), ctx);

        ctx.Published.Should().BeEmpty();
        var mismatches = new[]
        {
            Mutate(exact.Clone(), value => value.RunId = "run-other"),
            Mutate(exact.Clone(), value => value.StepId = "step-other"),
            Mutate(exact.Clone(), value => value.ToolApproval.ToolCallId = "call-other"),
            Mutate(exact.Clone(), value => value.ToolApproval.ExecutionId = "exec-other"),
            Mutate(exact.Clone(), value => value.ToolApproval.ApprovalRequestId = "approval-other"),
            Mutate(exact.Clone(), value => value.Approved = false),
        };
        foreach (var mismatch in mismatches)
            await HandleAndDrainAsync(module, Envelope(mismatch), ctx);

        tool.Requests.Should().HaveCount(2);
        ctx.Published.Select(x => x.Event)
            .OfType<WorkflowToolApprovalResumeRejectedEvent>()
            .Should().HaveCount(mismatches.Length);
    }

    [Fact]
    public async Task ApprovedResume_WhenToolReturnsTypedFailure_ShouldPublishFailedToolAndStepOutcomes()
    {
        const string resultJson = """{"error":true,"status":503}""";
        var pending = new WorkflowToolApprovalPendingOutcome(
            ApprovalRequestId: "approval-1",
            ToolName: "danger",
            ToolCallId: "workflow:run-1:danger_step:exec-1",
            ArgumentsJson: """{"danger":true}""",
            ApprovalMode: "AlwaysRequire",
            IsReadOnly: false,
            IsDestructive: true);
        var tool = new ScriptedWorkflowTool(
            "danger",
            request => request.ApprovalGrant is null
                ? new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending)
                : WorkflowToolExecutionResult.Failed(
                    resultJson,
                    "NYXID_PROXY_HTTP_503",
                    "The service request failed."));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            "danger_step",
            """{"danger":true}""",
            "exec-1");
        ctx.Published.Clear();

        await HandleAndDrainAsync(
            module,
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "danger_step",
                Approved = true,
                ToolApproval = new WorkflowToolApprovalResume
                {
                    ExecutionId = "exec-1",
                    ToolCallId = "workflow:run-1:danger_step:exec-1",
                    ApprovalRequestId = "approval-1",
                },
            }),
            ctx);

        var toolCompleted = ctx.Published.Select(x => x.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Single();
        toolCompleted.Success.Should().BeFalse();
        toolCompleted.ResultJson.Should().Be(resultJson);
        toolCompleted.Error.Should().Contain("NYXID_PROXY_HTTP_503");

        var stepCompleted = ctx.Published.Select(x => x.Event)
            .OfType<StepCompletedEvent>()
            .Single();
        stepCompleted.Success.Should().BeFalse();
        stepCompleted.Output.Should().Be(resultJson);
        stepCompleted.Error.Should().Contain("The service request failed.");
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().BeEmpty();
    }

    [Fact]
    public async Task ApprovedResume_WhenPreTerminalFailureIsRetryable_ShouldPersistDurableRetry()
    {
        var pending = new WorkflowToolApprovalPendingOutcome(
            ApprovalRequestId: "approval-1",
            ToolName: "danger",
            ToolCallId: "workflow:run-1:danger_step:exec-1",
            ArgumentsJson: """{"danger":true}""",
            ApprovalMode: "AlwaysRequire",
            IsReadOnly: false,
            IsDestructive: true);
        var tool = new ScriptedWorkflowTool(
            "danger",
            request => request.ApprovalGrant is null
                ? new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending)
                : WorkflowToolExecutionResult.Failed(
                    """{"error":"tool_admission_unavailable"}""",
                    "tool_admission_unavailable",
                    "The durable tool admission ledger is unavailable.",
                    terminalInvoked: false,
                    retryable: true));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            "danger_step",
            """{"danger":true}""",
            "exec-1");
        ctx.Published.Clear();

        await HandleAndDrainAsync(
            module,
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "danger_step",
                Approved = true,
                ToolApproval = new WorkflowToolApprovalResume
                {
                    ExecutionId = "exec-1",
                    ToolCallId = "workflow:run-1:danger_step:exec-1",
                    ApprovalRequestId = "approval-1",
                },
            }),
            ctx);

        var state = ctx.LoadState<ToolCallModuleState>("tool_call");
        state.PendingApprovals.Should().BeEmpty();
        var execution = state.PendingExecutions.Values.Should().ContainSingle().Subject;
        execution.Attempt.Should().Be(3);
        execution.TerminalDecision.Should().Be(WorkflowToolCallTerminalDecision.Approved);
        execution.RetryLease.Should().NotBeNull();
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Should().BeEmpty();
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task RejectedResume_ShouldFailClosedAndClearPendingState()
    {
        var pending = new WorkflowToolApprovalPendingOutcome(
            ApprovalRequestId: "approval-1",
            ToolName: "danger",
            ToolCallId: "workflow:run-1:danger_step:exec-1",
            ArgumentsJson: """{"danger":true}""",
            ApprovalMode: "AlwaysRequire",
            IsReadOnly: false,
            IsDestructive: true);
        var tool = new ScriptedWorkflowTool("danger", _ => new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, "danger_step", """{"danger":true}""", "exec-1");
        ctx.Published.Clear();

        var resumed = new WorkflowResumedEvent
        {
            RunId = "run-1",
            StepId = "danger_step",
            Approved = false,
            Feedback = "blocked",
            ToolApproval = new WorkflowToolApprovalResume
            {
                ExecutionId = "exec-1",
                ToolCallId = "workflow:run-1:danger_step:exec-1",
                ApprovalRequestId = "approval-1",
            },
        };
        await HandleAndDrainAsync(
            module,
            Envelope(resumed),
            ctx);
        await HandleAndDrainAsync(
            module,
            Envelope(resumed),
            ctx);

        tool.Requests.Should().ContainSingle();
        var toolCompleted = ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Single();
        toolCompleted.Success.Should().BeFalse();
        toolCompleted.Error.Should().Contain("approval_denied");
        var completed = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("approval_denied");
        completed.Error.Should().Contain("approval rejected");
        completed.Error.Should().Contain("blocked");
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolApprovalResumeRejectedEvent>().Should().BeEmpty();
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().BeEmpty();
    }

    [Fact]
    public async Task MismatchedResume_ShouldPublishTypedRejectionWithoutClearingPendingState()
    {
        var pending = new WorkflowToolApprovalPendingOutcome(
            ApprovalRequestId: "approval-1",
            ToolName: "danger",
            ToolCallId: "workflow:run-1:danger_step:exec-1",
            ArgumentsJson: """{"danger":true}""",
            ApprovalMode: "AlwaysRequire",
            IsReadOnly: false,
            IsDestructive: true);
        var tool = new ScriptedWorkflowTool("danger", _ => new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, "danger_step", """{"danger":true}""", "exec-1");
        ctx.Published.Clear();

        await HandleAndDrainAsync(
            module,
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "danger_step",
                Approved = true,
                ToolApproval = new WorkflowToolApprovalResume
                {
                    ExecutionId = "exec-1",
                    ToolCallId = "workflow:run-1:danger_step:exec-1",
                    ApprovalRequestId = "other-approval",
                },
            }),
            ctx);

        tool.Requests.Should().ContainSingle();
        var rejected = ctx.Published.Select(x => x.Event)
            .OfType<WorkflowToolApprovalResumeRejectedEvent>()
            .Should().ContainSingle().Subject;
        rejected.RunId.Should().Be("run-1");
        rejected.StepId.Should().Be("danger_step");
        rejected.Reason.Should().Be(WorkflowToolApprovalResumeRejectionReason.IdentityMismatch);
        rejected.SubmittedApproval.Should().NotBeNull();
        rejected.SubmittedApproval.ExecutionId.Should().Be("exec-1");
        rejected.SubmittedApproval.ToolCallId.Should().Be("workflow:run-1:danger_step:exec-1");
        rejected.SubmittedApproval.ApprovalRequestId.Should().Be("other-approval");
        ctx.Published.Single(x => x.Event is WorkflowToolApprovalResumeRejectedEvent)
            .Direction.Should().Be(TopologyAudience.Self);
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().ContainSingle();
    }

    [Fact]
    public async Task ResumeWithoutPendingApproval_ShouldPublishTypedNotFoundRejection()
    {
        var module = CreateModule(new ScriptedWorkflowTool(
            "danger",
            _ => WorkflowToolExecutionResult.Success("{}")));
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "danger_step",
                Approved = true,
                ToolApproval = new WorkflowToolApprovalResume
                {
                    ExecutionId = "exec-1",
                    ToolCallId = "tool-call-1",
                    ApprovalRequestId = "approval-1",
                },
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(x => x.Event)
            .OfType<WorkflowToolApprovalResumeRejectedEvent>()
            .Should().ContainSingle()
            .Which.Reason.Should().Be(WorkflowToolApprovalResumeRejectionReason.PendingApprovalNotFound);
    }

    [Fact]
    public async Task ResumeWithIncompleteApprovalIdentity_ShouldPublishTypedInvalidIdentityRejection()
    {
        var module = CreateModule(new ScriptedWorkflowTool(
            "danger",
            _ => WorkflowToolExecutionResult.Success("{}")));
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "danger_step",
                Approved = true,
                ToolApproval = new WorkflowToolApprovalResume
                {
                    ExecutionId = "exec-1",
                    ToolCallId = "tool-call-1",
                },
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(x => x.Event)
            .OfType<WorkflowToolApprovalResumeRejectedEvent>()
            .Should().ContainSingle()
            .Which.Reason.Should().Be(WorkflowToolApprovalResumeRejectionReason.InvalidIdentity);
    }

    private static ToolCallModule CreateModule(IWorkflowTool tool) =>
        new([new SingleToolSource(tool)], NullLogger<ToolCallModule>.Instance);

    private static T Mutate<T>(T value, Action<T> mutation)
    {
        mutation(value);
        return value;
    }

    private static async Task ExecuteToolCallAsync(
        ToolCallModule module,
        RecordingWorkflowContext ctx,
        string toolName,
        string stepId,
        string input,
        string executionId,
        IReadOnlyList<WorkflowFileRef>? inputFileRefs = null,
        string idempotencyKey = "",
        DateTimeOffset? issuedAt = null,
        string displayName = "",
        int timeoutMs = 0)
    {
        var request = new StepRequestEvent
        {
            StepId = stepId,
            StepType = "tool_call",
            RunId = ctx.RunId,
            ExecutionId = executionId,
            IdempotencyKey = idempotencyKey,
            Input = input,
            DisplayName = displayName,
            TimeoutMs = timeoutMs,
            Parameters = { ["tool"] = toolName },
        };
        request.InputFileRefs.Add(inputFileRefs?.Select(static fileRef => fileRef.Clone()) ?? []);

        await module.HandleAsync(
            Envelope(request, issuedAt),
            ctx,
            CancellationToken.None);
        await DrainToolCallContinuationsAsync(module, ctx);
    }

    private static async Task HandleAndDrainAsync(
        ToolCallModule module,
        EventEnvelope envelope,
        RecordingWorkflowContext ctx)
    {
        await module.HandleAsync(envelope, ctx, CancellationToken.None);
        await DrainToolCallContinuationsAsync(module, ctx);
    }

    private static async Task DrainToolCallContinuationsAsync(
        ToolCallModule module,
        RecordingWorkflowContext ctx)
    {
        while (true)
        {
            var pending = ctx.LoadState<ToolCallModuleState>("tool_call")
                .PendingExecutions.Values.FirstOrDefault(static candidate =>
                    candidate.ExecutionPhase == WorkflowToolCallExecutionPhase.ExecutionPending);
            if (pending == null)
                return;

            var completed = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>(candidate =>
                candidate.CallId == pending.CallId &&
                candidate.ExecutionId == pending.ExecutionId &&
                candidate.Attempt == pending.Attempt &&
                candidate.ContinuationToken == pending.ContinuationToken);
            ctx.Published.RemoveAll(item => ReferenceEquals(item.Event, completed));

            await module.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);
        }
    }

    private static WorkflowFileRef BuildWorkflowFileRef(string fileId) =>
        new()
        {
            FileId = fileId,
            ArtifactId = $"artifact-{fileId}",
            SourceKind = WorkflowFileSourceKind.ChatInput,
            FileName = $"{fileId}.txt",
            MediaType = "text/plain",
        };

    private static EventEnvelope Envelope(IMessage evt, DateTimeOffset? issuedAt = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(issuedAt ?? DateTimeOffset.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };

    private sealed class ScriptedWorkflowTool(
        string name,
        Func<WorkflowToolExecutionRequest, WorkflowToolExecutionResult> execute) : IWorkflowTool
    {
        public string Name { get; } = name;

        public List<WorkflowToolExecutionRequest> Requests { get; } = [];

        public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(execute(request));
        }
    }

    private sealed class SingleToolSource(IWorkflowTool tool) : IWorkflowToolSource
    {
        public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IWorkflowTool>>([tool]);
        }
    }

    private sealed class RecordingWorkflowContext
        : IWorkflowExecutionContext,
          IWorkflowExecutionRuntimeContextAccessor,
          IWorkflowExecutionStateHost,
          IRuntimeSecretStoreAccessor
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);
        private readonly Channel<IMessage> _publishedEvents = Channel.CreateUnbounded<IMessage>();
        private readonly Dictionary<IMessage, EventEnvelopePublishOptions?> _publishedOptions =
            new(ReferenceEqualityComparer.Instance);

        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";
        public string RunId => "run-1";
        public DateTimeOffset CurrentUtcNow { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UtcNow => CurrentUtcNow;
        public string ScopeId => "scope-1";
        public IServiceProvider Services { get; } = new EmptyServiceProvider();
        public ILogger Logger { get; } = NullLogger.Instance;
        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();
        public IRuntimeSecretStore? RuntimeSecretStore { get; } = new InMemoryRuntimeSecretStore();
        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();
        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();
        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public async Task<TEvent> WaitForPublishedAsync<TEvent>(Func<TEvent, bool>? predicate = null)
            where TEvent : class, IMessage
        {
            while (true)
            {
                var evt = await _publishedEvents.Reader.ReadAsync().AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
                if (evt is TEvent typed && (predicate == null || predicate(typed)))
                    return typed;
            }
        }

        public EventEnvelope PublishedEnvelope(IMessage evt)
        {
            var envelope = Envelope(evt);
            envelope.Route = EnvelopeRouteSemantics.CreateTopologyPublication(AgentId, TopologyAudience.Self);
            if (_publishedOptions.GetValueOrDefault(evt)?.Delivery?.OperationId is { Length: > 0 } operationId)
            {
                envelope.Runtime = new EnvelopeRuntime
                {
                    DeliveryIdentity = new DeliveryIdentity { OperationId = operationId },
                };
            }

            return envelope;
        }

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            if (!_states.TryGetValue(scopeKey, out var packed) || !packed.Is(new TState().Descriptor))
                return new TState();

            return packed.Unpack<TState>() ?? new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            _states
                .Where(x => string.IsNullOrEmpty(scopeKeyPrefix) || x.Key.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
                .Where(x => x.Value.Is(new TState().Descriptor))
                .Select(x => new KeyValuePair<string, TState>(x.Key, x.Value.Unpack<TState>() ?? new TState()))
                .ToList();

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            ct.ThrowIfCancellationRequested();
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Any? GetExecutionState(string scopeKey) => _states.GetValueOrDefault(scopeKey);

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() => _states.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _states[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default) =>
            ClearStateAsync(scopeKey, ct);

        Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
            WorkflowCompletedEvent terminalFailure,
            StepCompletedEvent? terminalStep,
            CancellationToken ct)
        {
            _ = terminalFailure;
            _ = terminalStep;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
            CompensableStepDispatchedEvent evt,
            CancellationToken ct)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
            CompensationStepCompletedEvent completion,
            CancellationToken ct = default)
        {
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
            string runId,
            string error,
            CancellationToken ct = default)
        {
            _ = runId;
            _ = error;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
            new(
                WorkflowCompensationTransitionStatus.NoCompensableLedger,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);

        public Task UpdateExecutionContextAsync(
            WorkflowRunExecutionContextDelta delta,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = delta;
            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            _ = options;
            Published.Add((evt, direction));
            _publishedOptions[evt] = options?.DeepClone();
            _publishedEvents.Writer.TryWrite(evt);
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            _ = targetActorId;
            _ = evt;
            _ = options;
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = callbackId;
            _ = dueTime;
            _ = evt;
            _ = options;
            return Task.FromResult(new RuntimeCallbackLease(AgentId, "callback-1", 1, RuntimeCallbackBackend.InMemory));
        }

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = lease;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
