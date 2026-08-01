using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatControlCommandTests
{
    private static readonly Timestamp Now = Timestamp.FromDateTimeOffset(
        new DateTimeOffset(2026, 7, 25, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void RetryFailedLlm_ShouldCommitNextGenerationWithoutPersistingCapability()
    {
        var state = FailedStepState(
            NyxIdChatStepKind.Llm,
            required: true,
            safeToSkip: false,
            retryInputRebuildable: true);
        var command = RetryCommand();

        var decision = NyxIdChatControlCommands.Retry(state, command, stateVersion: 3, Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.ShouldDispatch.Should().BeTrue();
        decision.Result.Kind.Should().Be(NyxIdChatStepControlKind.Retry);
        decision.Result.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.Result.RequestId.Should().Be("retry-alpha");
        decision.Result.OperationGeneration.Should().Be(2);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
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
        ExpectedOperationGeneration = 1,
        ExpectedStateVersion = 3,
        LlmControl = new LLMControlContextPayload
        {
            NyxIdAccessToken = "retry-runtime-token-alpha",
        },
        ToolContext = new AgentToolExecutionContextPayload
        {
            Credentials = new AgentToolCredentialsPayload
            {
                NyxIdAccessToken = "retry-runtime-token-alpha",
            },
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
