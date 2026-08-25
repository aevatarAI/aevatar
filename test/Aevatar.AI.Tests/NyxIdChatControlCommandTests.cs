using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatControlCommandTests
{
    private static readonly Timestamp Now = Timestamp.FromDateTimeOffset(
        new DateTimeOffset(2026, 7, 25, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void RetryFailedLlm_ShouldCommitFailureRecoveryRevisionWithoutPersistingCapability()
    {
        var state = FailedStepState(
            NyxIdChatStepKind.Llm,
            required: true,
            safeToSkip: false,
            retryInputRebuildable: true);
        state.ActiveTask.PlanRevision = 4;
        var command = RetryCommand();

        var decision = NyxIdChatControlCommands.Retry(state, command, stateVersion: 3, Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeTrue();
        decision.Result.Kind.Should().Be(NyxIdChatStepControlKind.Retry);
        decision.Result.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.Result.RequestId.Should().Be("retry-alpha");
        decision.Result.OperationGeneration.Should().Be(2);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        decision.State.ActiveTask.PlanRevision.Should().Be(5);
        decision.State.ActiveTask.PlanRevisions.Should().ContainSingle(record =>
            record.PlanRevision == 5 &&
            record.RevisionCause == NyxIdChatPlanRevisionCause.FailureRecovery);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        decision.State.RecentTerminalTurns.Should().BeEmpty();
        var retried = decision.State.ActiveTask.Steps.Single();
        retried.Status.Should().Be(NyxIdChatStepStatus.Running);
        retried.Operation.Key.OperationGeneration.Should().Be(2);
        retried.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        decision.NextCommand.Should().NotBeNull();
        decision.NextCommand!.Llm.Request.Prompt.Should().Be("retry the typed prompt");
        decision.NextCommand.Llm.Request.LlmControl.NyxIdAccessToken.Should().Be(
            "retry-runtime-token-alpha");
        decision.NextCommand.Llm.Request.ToolContext.Credentials.NyxIdAccessToken.Should().Be(
            "retry-runtime-token-alpha");
        decision.State.ToString().Should().NotContain("retry-runtime-token-alpha");
    }

    [Fact]
    public void RetryExactReplayAtRequestedWaterline_ShouldRedispatchWithoutAnotherCommit()
    {
        var first = NyxIdChatControlCommands.Retry(
            FailedStepState(
                NyxIdChatStepKind.Llm,
                required: true,
                safeToSkip: false,
                retryInputRebuildable: true),
            RetryCommand(),
            stateVersion: 3,
            Now);

        var replay = NyxIdChatControlCommands.Retry(
            first.State,
            RetryCommand(),
            stateVersion: 4,
            Now);

        replay.ShouldCommit.Should().BeFalse();
        replay.ShouldDispatch.Should().BeTrue(
            "the committed requested waterline may be safely delivered using renewed transient capability");
        replay.Result.Should().BeEquivalentTo(first.Result);
        replay.NextCommand!.Key.Should().BeEquivalentTo(first.NextCommand!.Key);
    }

    [Fact]
    public void RetrySameIdentityWithDifferentContent_ShouldCommitConflictWithoutDispatch()
    {
        var first = NyxIdChatControlCommands.Retry(
            FailedStepState(
                NyxIdChatStepKind.Llm,
                required: true,
                safeToSkip: false,
                retryInputRebuildable: true),
            RetryCommand(),
            stateVersion: 3,
            Now);
        var conflicting = RetryCommand();
        conflicting.ExpectedOperationGeneration = 7;
        conflicting.ExpectedStateVersion = 0;

        var decision = NyxIdChatControlCommands.Retry(
            first.State,
            conflicting,
            stateVersion: 4,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Result.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.Result.ReasonCode.Should().Be(NyxIdChatControlCommands.StepControlConflict);
        decision.State.ActiveTask.Steps.Single().Operation.Key.OperationGeneration.Should().Be(2);
    }

    [Fact]
    public void RetryToolWithoutTypedRebuildContract_ShouldReject()
    {
        var state = FailedStepState(
            NyxIdChatStepKind.Tool,
            required: true,
            safeToSkip: false,
            retryInputRebuildable: false);
        state.ActiveTask.Steps.Single().Operation.Idempotent = true;
        state.ActiveTask.Steps.Single().Operation.IdempotencyKey = "effect-alpha";

        var decision = NyxIdChatControlCommands.Retry(
            state,
            RetryCommand(),
            stateVersion: 3,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Result.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.Result.ReasonCode.Should().Be(
            NyxIdChatControlCommands.StepActionUnavailable);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
    }

    [Fact]
    public void RetryReconciledNotAppliedTool_ShouldEnterFreshGenerationWithoutAutomaticReplay()
    {
        var state = FailedStepState(
            NyxIdChatStepKind.Tool,
            required: true,
            safeToSkip: false,
            retryInputRebuildable: true);
        var step = state.ActiveTask.Steps.Single();
        step.MayChangeExternalState = true;
        step.RetryToolInput = new NyxIdChatRetryToolInputState
        {
            CallId = "call-effect-alpha",
            ToolName = "opaque-effect-tool",
            Arguments = JsonParser.Default.Parse<Struct>("{\"value\":\"alpha\"}"),
            OperationAdmission = new AgentToolOperationAdmissionPayload
            {
                ServiceInstanceId = "m-alpha",
                ServiceSlug = "svc-alpha",
                PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
                {
                    EndpointId = "endpoint-effect-alpha",
                },
            },
        };
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);

        var decision = NyxIdChatControlCommands.Retry(
            state,
            RetryCommand(),
            stateVersion: 3,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeTrue();
        decision.State.ActiveTask.TaskId.Should().Be("task-alpha");
        decision.State.ActiveTask.Steps.Single().Operation.Key.OperationGeneration.Should().Be(2);
        decision.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
        decision.NextCommand.Tool.CallId.Should().Be("call-effect-alpha");
        decision.NextCommand.Tool.IdempotencyKey.Should().Be(
            decision.NextCommand.Key.OperationId);
        decision.NextCommand.Tool.RetryAuthorizationSourceKey.Should().BeEquivalentTo(
            step.Operation.Key);
        decision.NextCommand.Key.OperationId.Should().NotBe("operation-alpha");
    }

    [Fact]
    public void LateConnectedServiceApprovalReceipt_ShouldRefineOnlyTheFencedGeneration()
    {
        var state = FailedStepState(
            NyxIdChatStepKind.Tool,
            required: true,
            safeToSkip: false,
            retryInputRebuildable: true);
        var step = state.ActiveTask.Steps.Single();
        step.Status = NyxIdChatStepStatus.Uncertain;
        step.MayChangeExternalState = true;
        step.ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged;
        step.Operation.Phase = NyxIdChatOperationPhase.Dispatched;
        step.Operation.CompletedAt = null;
        step.Source = new NyxIdChatStepSource
        {
            Tool = new NyxIdChatToolStepSource
            {
                ToolName = "repository_update",
                OperationAdmission = new AgentToolOperationAdmissionPayload
                {
                    ServiceInstanceId = "m-alpha",
                    ServiceSlug = "svc-alpha",
                },
            },
        };
        state.ActiveTask.Status = NyxIdChatTaskStatus.Stopped;
        state.ActiveTurn.Status = NyxIdChatTurnStatus.Stopped;
        state.LatestTurn = state.ActiveTurn.Clone();
        state.ControlFence = new NyxIdChatControlFenceState
        {
            Kind = NyxIdChatControlKind.Stop,
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            Outcome = NyxIdChatControlOutcome.Uncancellable,
        };
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = step.Operation.Key.Clone(),
            Tool = new NyxIdChatToolOperationResult
            {
                ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
                Receipt = new AgentToolReceipt
                {
                    CallId = "call-alpha",
                    ToolName = "repository_update",
                    Status = AgentToolReceiptStatus.ApprovalRequired,
                    ApprovalRequestId = "approval-alpha",
                    NyxIdApprovalDecisionMode = NyxIdApprovalDecisionMode.Unspecified,
                },
            },
        };

        var decision = NyxIdChatControlCommands.ReconcileLateOperationEvidence(
            state,
            signal,
            Now);

        decision.IsFencedOperation.Should().BeTrue();
        decision.ShouldCommit.Should().BeTrue();
        decision.OperationPhase.Should().Be(NyxIdChatOperationPhase.Cancelled);
        var refined = decision.State.ActiveTask.Steps.Single();
        refined.Operation.Key.Should().BeEquivalentTo(step.Operation.Key);
        refined.ApprovalRequestId.Should().Be("approval-alpha");
        refined.ApprovalObservation.Should().NotBeNull();
        refined.ApprovalObservation.ApprovalRequestId.Should().Be("approval-alpha");
        refined.ApprovalObservation.DecisionMode.Should().Be(NyxIdApprovalDecisionMode.Unspecified);
        refined.ApprovalObservation.ReceiptStatus.Should()
            .Be(AgentToolReceiptStatus.ApprovalRequired);
        refined.ApprovalObservation.ObservedAt.Should().Be(Now);
        decision.State.PendingApproval.Should().BeNull();
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void SkipFailedStep_ShouldRequireOptionalOrSafeToSkip(
        bool required,
        bool safeToSkip)
    {
        var state = FailedStepState(
            NyxIdChatStepKind.Tool,
            required,
            safeToSkip,
            retryInputRebuildable: false);
        var command = SkipCommand();

        var decision = NyxIdChatControlCommands.Skip(
            state,
            command,
            stateVersion: 3,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Result.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.State.ActiveTask.Steps.Single().Status.Should().Be(
            NyxIdChatStepStatus.Skipped);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        decision.State.RecentTerminalTurns.Should().ContainSingle(summary =>
            summary.TurnId == "turn-alpha" &&
            summary.Status == NyxIdChatTurnStatus.Succeeded);

        var replay = NyxIdChatControlCommands.Skip(
            decision.State,
            command.Clone(),
            stateVersion: 4,
            Now);
        replay.ShouldCommit.Should().BeFalse();
        replay.ShouldDispatch.Should().BeFalse();
        replay.Result.Should().BeEquivalentTo(decision.Result);
    }

    [Fact]
    public void SkipRequiredUnsafeStep_ShouldRejectWithoutChangingRecoverableState()
    {
        var state = FailedStepState(
            NyxIdChatStepKind.Tool,
            required: true,
            safeToSkip: false,
            retryInputRebuildable: false);

        var decision = NyxIdChatControlCommands.Skip(
            state,
            SkipCommand(),
            stateVersion: 3,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.Result.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.Result.ReasonCode.Should().Be(
            NyxIdChatControlCommands.StepActionUnavailable);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        decision.State.ActiveTask.Steps.Single().Status.Should().Be(
            NyxIdChatStepStatus.Failed);
    }

    [Fact]
    public void RetryTerminalFailedTurn_ShouldRejectWithoutResurrectingTurn()
    {
        var state = TerminalFailedStepState(
            NyxIdChatStepKind.Llm,
            required: true,
            safeToSkip: false,
            retryInputRebuildable: true);

        var decision = NyxIdChatControlCommands.Retry(
            state,
            RetryCommand(),
            stateVersion: 3,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Result.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.Result.ReasonCode.Should().Be(
            NyxIdChatControlCommands.StepActionUnavailable);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        decision.State.ActiveTurn.TerminalAt.Should().NotBeNull();
        decision.State.RecentTerminalTurns.Should().ContainSingle(summary =>
            summary.TurnId == "turn-alpha" &&
            summary.Status == NyxIdChatTurnStatus.Failed);
    }

    [Fact]
    public void SkipTerminalFailedTurn_ShouldRejectWithoutReplacingTerminalOutcome()
    {
        var state = TerminalFailedStepState(
            NyxIdChatStepKind.Tool,
            required: false,
            safeToSkip: false,
            retryInputRebuildable: false);

        var decision = NyxIdChatControlCommands.Skip(
            state,
            SkipCommand(),
            stateVersion: 3,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeFalse();
        decision.Result.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.Result.ReasonCode.Should().Be(
            NyxIdChatControlCommands.StepActionUnavailable);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        decision.State.ActiveTask.Steps.Single().Status.Should().Be(
            NyxIdChatStepStatus.Failed);
        decision.State.RecentTerminalTurns.Should().ContainSingle(summary =>
            summary.TurnId == "turn-alpha" &&
            summary.Status == NyxIdChatTurnStatus.Failed);
    }

    [Fact]
    public void StopPendingInput_ShouldClearNeedsYouFactWithTerminalFence()
    {
        var state = FailedStepState(
            NyxIdChatStepKind.Input,
            required: true,
            safeToSkip: false,
            retryInputRebuildable: false);
        state.ActiveTask.Steps.Single().Status = NyxIdChatStepStatus.Waiting;
        state.PendingInput = new NyxIdChatPendingInputState
        {
            RequestId = "input-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            Prompt = "Choose a region.",
            AskedAt = Now.Clone(),
        };

        var decision = NyxIdChatControlCommands.Stop(
            state,
            new NyxIdChatStopCommand
            {
                ScopeId = "scope-alpha",
                ConversationActorId = "conversation-alpha",
                TurnId = "turn-alpha",
                StopRequestId = "stop-alpha",
                ClientRequestId = "client-stop-alpha",
                ExpectedStateVersion = 3,
            },
            stateVersion: 3,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.State.PendingInput.Should().BeNull();
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State)
            .Attention.AttentionKind.Should().Be(NyxIdChatAttentionKind.None);
    }

    [Fact]
    public void Uc2Stop_ShouldReturnDurablePartialWorkReceiptWithNoExternalEffect()
    {
        var state = FailedStepState(
            NyxIdChatStepKind.Llm,
            required: true,
            safeToSkip: false,
            retryInputRebuildable: true);
        var active = state.ActiveTask.Steps.Single();
        active.Order = 3;
        active.Status = NyxIdChatStepStatus.Running;
        active.Operation.Phase = NyxIdChatOperationPhase.Running;
        active.Operation.CompletedAt = null;
        active.ExternalEffect = NyxIdChatEffectEvidence.NotStarted;
        active.FailureCode = string.Empty;
        active.SafeMessage = string.Empty;
        active.Description = "Refine the shortlist for 7 pm and a private room.";
        state.ActiveTask.Steps.Insert(0, CompletedReadOnlyStep(
            "step-uc2-input",
            1,
            NyxIdChatStepKind.Input,
            "Collect all dinner constraints and accept research-only scope."));
        state.ActiveTask.Steps.Insert(1, CompletedReadOnlyStep(
            "step-uc2-search",
            2,
            NyxIdChatStepKind.Tool,
            "Aevatar web_search found the candidate set."));
        state.ActiveTask.Steps[1].Source = new NyxIdChatStepSource
        {
            Tool = new NyxIdChatToolStepSource { ToolName = "web_search" },
        };
        var completedBytes = state.ActiveTask.Steps.Take(2)
            .Select(static step => step.ToByteString())
            .ToArray();

        var decision = NyxIdChatControlCommands.Stop(
            state,
            new NyxIdChatStopCommand
            {
                ScopeId = "scope-alpha",
                ConversationActorId = "conversation-alpha",
                TurnId = "turn-alpha",
                StopRequestId = "stop-uc2-1",
                ClientRequestId = "client-stop-uc2-1",
                ExpectedStateVersion = 17,
            },
            stateVersion: 17,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.Result.Outcome.Should().Be(NyxIdChatControlOutcome.Uncancellable);
        decision.Result.SafeMessage.Should().Contain("Partial-work receipt");
        decision.Result.SafeMessage.Should().Contain("2 completed steps were retained");
        decision.Result.SafeMessage.Should().Contain(
            "Retained: Collect all dinner constraints and accept research-only scope; " +
            "Aevatar web_search found the candidate set.");
        decision.Result.SafeMessage.Should().Contain(
            "Fenced: Refine the shortlist for 7 pm and a private room.");
        decision.Result.SafeMessage.Should().Contain("No external effect was applied");
        decision.Result.SafeMessage.Should().Contain("Late evidence cannot advance this stopped task");
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        decision.State.ActiveTask.Steps[2].Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        decision.State.ActiveTask.Steps.Take(2).Select(static step => step.ToByteString())
            .Should().Equal(completedBytes);

        var reloaded = NyxIdChatConversationGAgentState.Parser.ParseFrom(
            decision.State.ToByteArray());
        reloaded.ControlFence.SafeMessage.Should().Be(decision.Result.SafeMessage);
        reloaded.ActiveTask.SafeMessage.Should().Be(decision.Result.SafeMessage);
        reloaded.LatestTurn.SafeMessage.Should().Be(decision.Result.SafeMessage);
        reloaded.ActiveTask.Steps.Single(step => step.StepId == "step-uc2-search")
            .Source.Tool.ToolName.Should().Be("web_search");
    }

    [Theory]
    [InlineData(NyxIdChatControlKind.Stop)]
    [InlineData(NyxIdChatControlKind.Steering)]
    public void SameTurnControl_WhenStreamingProgressAdvancesStateVersion_ShouldStillFence(
        NyxIdChatControlKind kind)
    {
        var state = FailedStepState(
            NyxIdChatStepKind.Llm,
            required: true,
            safeToSkip: false,
            retryInputRebuildable: true);
        var step = state.ActiveTask.Steps.Single();
        step.Status = NyxIdChatStepStatus.Running;
        step.Operation.Phase = NyxIdChatOperationPhase.Running;
        step.Operation.CompletedAt = null;
        state.ActiveTask.FailureCode = string.Empty;
        state.ActiveTask.SafeMessage = string.Empty;
        state.ActiveTurn.FailureCode = string.Empty;
        state.ActiveTurn.SafeMessage = string.Empty;

        var decision = kind == NyxIdChatControlKind.Stop
            ? NyxIdChatControlCommands.Stop(
                state,
                new NyxIdChatStopCommand
                {
                    ScopeId = "scope-alpha",
                    ConversationActorId = "conversation-alpha",
                    TurnId = "turn-alpha",
                    StopRequestId = "stop-after-progress",
                    ClientRequestId = "client-stop-after-progress",
                    ExpectedStateVersion = 3,
                },
                stateVersion: 500,
                Now)
            : NyxIdChatControlCommands.Steer(
                state,
                new NyxIdChatSteeringCommand
                {
                    ScopeId = "scope-alpha",
                    ConversationActorId = "conversation-alpha",
                    TurnId = "turn-alpha",
                    SteeringId = "steer-after-progress",
                    ClientRequestId = "client-steer-after-progress",
                    Instruction = "Use the new constraints.",
                    ExpectedStateVersion = 3,
                },
                stateVersion: 500,
                Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.Result.Outcome.Should().Be(NyxIdChatControlOutcome.Uncancellable);
        decision.Result.ReasonCode.Should().NotBe(NyxIdChatControlCommands.StateVersionMismatch);
        decision.FencedState.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
    }

    private static NyxIdChatRetryStepCommand RetryCommand() => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = "step-alpha",
        RetryRequestId = "retry-alpha",
        ClientRequestId = "client-retry-alpha",
        CommandId = "command-retry-alpha",
        CorrelationId = "correlation-retry-alpha",
        OwnerSubject = "owner-alpha",
        ExpectedOperationGeneration = 1,
        ExpectedStateVersion = 3,
        LlmControl = new LLMControlContextPayload
        {
            NyxIdAccessToken = "retry-runtime-token-alpha",
        },
        ToolContext = (AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("retry-alpha", null),
            Credentials = new AgentToolCredentials(
                "retry-runtime-token-alpha",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            Caller = new AgentToolCallerContext(
                "scope-alpha",
                "owner-alpha",
                "retry-alpha",
                "scope-alpha"),
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                null,
                "scope-alpha",
                null,
                null),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                string.Empty,
                "owner-alpha",
                "proxy"),
            ExecutionOwner = AgentToolExecutionOwners.Actor("conversation-alpha"),
        }).ToPayload(),
    };

    private static AgentToolOperationAdmissionPayload ExactEffectAdmission() => new()
    {
        ServiceInstanceId = "m-alpha",
        ServiceSlug = "svc-alpha",
        PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
        {
            EndpointId = "endpoint-effect-alpha",
        },
    };

    private static NyxIdChatSkipStepCommand SkipCommand() => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = "step-alpha",
        SkipRequestId = "skip-alpha",
        ClientRequestId = "client-skip-alpha",
        CommandId = "command-skip-alpha",
        CorrelationId = "correlation-skip-alpha",
        ExpectedOperationGeneration = 1,
        ExpectedStateVersion = 3,
    };

    private static NyxIdChatConversationGAgentState FailedStepState(
        NyxIdChatStepKind kind,
        bool required,
        bool safeToSkip,
        bool retryInputRebuildable)
    {
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = "conversation-alpha",
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                ClientRequestId = "client-alpha",
                Status = NyxIdChatTurnStatus.Active,
                Prompt = "retry the typed prompt",
                FailureCode = "OPERATION_FAILED",
                SafeMessage = "The operation failed.",
                CreatedAt = Now.Clone(),
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTaskStatus.Active,
                FailureCode = "OPERATION_FAILED",
                SafeMessage = "The operation failed.",
                CreatedAt = Now.Clone(),
                UpdatedAt = Now.Clone(),
            },
            ProgressSequence = 3,
            UpdatedAt = Now.Clone(),
        };
        state.LatestTurn = state.ActiveTurn.Clone();
        var step = new NyxIdChatTaskStepState
        {
            StepId = "step-alpha",
            Order = 1,
            Kind = kind,
            Status = NyxIdChatStepStatus.Failed,
            Required = required,
            SafeToSkip = safeToSkip,
            RetryInputRebuildable = retryInputRebuildable,
            ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            FailureCode = "OPERATION_FAILED",
            SafeMessage = "The operation failed.",
            Operation = new NyxIdChatOperationState
            {
                Key = new NyxIdChatOperationKey
                {
                    ConversationActorId = "conversation-alpha",
                    TurnId = "turn-alpha",
                    TaskId = "task-alpha",
                    StepId = "step-alpha",
                    OperationId = "operation-alpha",
                    OperationGeneration = 1,
                },
                Kind = kind,
                Phase = NyxIdChatOperationPhase.Failed,
                TerminalCode = "OPERATION_FAILED",
                SafeMessage = "The operation failed.",
                CompletedAt = Now.Clone(),
            },
            UpdatedAt = Now.Clone(),
        };
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        state.ActiveTask.Steps.Add(step);
        state.ActiveTask.ActiveStepId = step.StepId;
        return state;
    }

    private static NyxIdChatTaskStepState CompletedReadOnlyStep(
        string stepId,
        int order,
        NyxIdChatStepKind kind,
        string description) => new()
    {
        StepId = stepId,
        Order = order,
        Kind = kind,
        Status = NyxIdChatStepStatus.Done,
        Required = true,
        Description = description,
        ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
        Operation = new NyxIdChatOperationState
        {
            Key = new NyxIdChatOperationKey
            {
                ConversationActorId = "conversation-alpha",
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = stepId,
                OperationId = $"operation-{stepId}",
                OperationGeneration = 1,
            },
            Kind = kind,
            Phase = NyxIdChatOperationPhase.Succeeded,
            Idempotent = true,
            CompletedAt = Now.Clone(),
        },
        AddedBy = NyxIdChatStepAddedBy.Initial,
        UpdatedAt = Now.Clone(),
    };

    private static NyxIdChatConversationGAgentState TerminalFailedStepState(
        NyxIdChatStepKind kind,
        bool required,
        bool safeToSkip,
        bool retryInputRebuildable)
    {
        var state = FailedStepState(kind, required, safeToSkip, retryInputRebuildable);
        var terminalAt = Now.Clone();
        state.ActiveTask.Status = NyxIdChatTaskStatus.Failed;
        state.ActiveTask.ActiveStepId = string.Empty;
        state.ActiveTask.FailureCode = "OPERATION_FAILED";
        state.ActiveTurn.Status = NyxIdChatTurnStatus.Failed;
        state.ActiveTurn.TerminalAt = terminalAt;
        state.LatestTurn = state.ActiveTurn.Clone();
        state.RecentTerminalTurns.Add(new NyxIdChatTurnSummary
        {
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            Status = NyxIdChatTurnStatus.Failed,
            FailureCode = "OPERATION_FAILED",
            SafeMessage = "The operation failed.",
            TerminalAt = terminalAt,
        });
        return state;
    }
}
