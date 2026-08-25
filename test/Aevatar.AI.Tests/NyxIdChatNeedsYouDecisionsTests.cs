using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatNeedsYouDecisionsTests
{
    private static readonly Timestamp AskedAt = Timestamp.FromDateTimeOffset(
        DateTimeOffset.Parse("2026-08-01T12:00:00Z"));
    private static readonly Timestamp ResolvedAt = Timestamp.FromDateTimeOffset(
        DateTimeOffset.Parse("2026-08-01T12:01:00Z"));
    private static readonly Timestamp ExpiresAt = Timestamp.FromDateTimeOffset(
        AskedAt.ToDateTimeOffset() + NyxIdChatTaskLifecycle.ToolApprovalExpiryWindow);

    [Fact]
    public void RequestInput_ShouldCommitExactPendingFactsAndActorAuthoredAttention()
    {
        var state = ActiveState();
        var command = InputRequest();

        var decision = NyxIdChatNeedsYouDecisions.RequestInput(state, command, AskedAt);
        var refreshed = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State);

        decision.ShouldCommit.Should().BeTrue();
        decision.IsExactReplay.Should().BeFalse();
        decision.Resolution.Should().NotBeNull();
        decision.State.ProgressSequence.Should().Be(8);
        decision.State.PendingInput.Should().BeEquivalentTo(new NyxIdChatPendingInputState
        {
            RequestId = "input-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            Prompt = "Choose a deployment region.",
            AskedAt = AskedAt.Clone(),
            AllowFreeText = false,
            MultiSelect = false,
            ToolCallId = "call-input-alpha",
            Options =
            {
                new NyxIdChatInputOption
                {
                    OptionId = "option-singapore",
                    Label = "Singapore",
                    Description = "Use the Singapore region.",
                },
                new NyxIdChatInputOption
                {
                    OptionId = "option-frankfurt",
                    Label = "Frankfurt",
                    Description = "Use the Frankfurt region.",
                },
            },
        });
        refreshed.Attention.TaskStatus.Should().Be(NyxIdChatTaskStatus.Active);
        refreshed.Attention.AttentionKind.Should().Be(NyxIdChatAttentionKind.Input);
        refreshed.Attention.AttentionSince.Should().Be(AskedAt);
        refreshed.Attention.ActiveStepSummary.Should().Be("Choose a deployment region.");
        state.PendingInput.Should().BeNull();
        state.ProgressSequence.Should().Be(7);
    }

    [Fact]
    public void RequestInput_ShouldRejectARequestForANonActiveStep()
    {
        var state = ActiveState();
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = "step-old",
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Done,
        });
        var command = InputRequest();
        command.StepId = "step-old";

        var decision = NyxIdChatNeedsYouDecisions.RequestInput(state, command, AskedAt);

        decision.ShouldCommit.Should().BeFalse();
        decision.State.ToByteArray().Should().Equal(state.ToByteArray());
    }

    [Fact]
    public void ResolveInput_ShouldFenceStaleVersionsAndKeepFirstDecisionAcrossReload()
    {
        var pending = NyxIdChatNeedsYouDecisions.RequestInput(
            ActiveState(),
            InputRequest(),
            AskedAt).State;
        var command = new NyxIdChatInputResolveCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            RequestId = "input-alpha",
            ClientRequestId = "client-input-alpha",
            Answer = SelectionAnswer("option-singapore"),
            ExpectedStateVersion = 40,
        };

        var stale = NyxIdChatNeedsYouDecisions.ResolveInput(
            pending,
            command,
            currentStateVersion: 41,
            ResolvedAt);

        stale.ShouldCommit.Should().BeFalse();
        stale.State.ToByteArray().Should().Equal(pending.ToByteArray());

        command.ExpectedStateVersion = 41;
        var accepted = NyxIdChatNeedsYouDecisions.ResolveInput(
            pending,
            command,
            currentStateVersion: 41,
            ResolvedAt);

        accepted.ShouldCommit.Should().BeTrue();
        accepted.State.PendingInput.Should().BeNull();
        accepted.State.LatestInputResolution.RequestId.Should().Be("input-alpha");
        accepted.State.LatestInputResolution.ClientRequestId.Should().Be("client-input-alpha");
        accepted.State.LatestInputResolution.AnswerSha256.Should().NotBeEmpty();
        accepted.State.LatestInputResolution.Answer.Selection.OptionIds.Should()
            .Equal("option-singapore");
        accepted.State.ToString().Should()
            .Contain("option-singapore")
            .And.NotContain("Singapore",
                "the committed selection identity is the option id, not its presentation label");
        accepted.State.ActiveTask.Steps.Single(step => step.StepId == "step-alpha")
            .Status.Should().Be(NyxIdChatStepStatus.Done);
        accepted.NextCommand.Should().NotBeNull();
        accepted.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.InputContinuation);
        accepted.NextCommand.InputContinuation.Answer.Selection.OptionIds.Should()
            .Equal("option-singapore");
        accepted.NextCommand.InputContinuation.SelectedOptions.Should().ContainSingle()
            .Which.OptionId.Should().Be("option-singapore");

        var reloaded = NyxIdChatConversationGAgentState.Parser.ParseFrom(
            accepted.State.ToByteArray());
        var exactReplay = NyxIdChatNeedsYouDecisions.ResolveInput(
            reloaded,
            command,
            currentStateVersion: 42,
            ResolvedAt);
        exactReplay.ShouldCommit.Should().BeFalse();
        exactReplay.IsExactReplay.Should().BeTrue();
        exactReplay.Resolution.Should().BeEquivalentTo(accepted.Resolution);

        var wrongIdentity = command.Clone();
        wrongIdentity.ScopeId = "scope-other";
        var mismatchedReplay = NyxIdChatNeedsYouDecisions.ResolveInput(
            reloaded,
            wrongIdentity,
            currentStateVersion: 42,
            ResolvedAt);
        mismatchedReplay.ShouldCommit.Should().BeFalse();
        mismatchedReplay.IsExactReplay.Should().BeFalse();

        var conflicting = command.Clone();
        conflicting.ClientRequestId = "client-input-conflict";
        conflicting.Answer = SelectionAnswer("option-frankfurt");
        var conflict = NyxIdChatNeedsYouDecisions.ResolveInput(
            reloaded,
            conflicting,
            currentStateVersion: 42,
            ResolvedAt);
        conflict.ShouldCommit.Should().BeFalse();
        conflict.IsExactReplay.Should().BeFalse();
        conflict.State.ToByteArray().Should().Equal(reloaded.ToByteArray());

        var repeatedRequest = NyxIdChatNeedsYouDecisions.RequestInput(
            reloaded,
            InputRequest(),
            ResolvedAt);
        repeatedRequest.ShouldCommit.Should().BeFalse();
        repeatedRequest.State.PendingInput.Should().BeNull();
    }

    [Fact]
    public void ResolveInput_ShouldPersistOnlySelectedOptionIdsAndCarryThemIntoContinuation()
    {
        var request = InputRequest();
        request.MultiSelect = true;
        var pending = NyxIdChatNeedsYouDecisions.RequestInput(
            ActiveState(),
            request,
            AskedAt).State;
        var command = new NyxIdChatInputResolveCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            RequestId = "input-alpha",
            ClientRequestId = "client-input-multi",
            Answer = SelectionAnswer("option-singapore", "option-frankfurt"),
            ExpectedStateVersion = 41,
        };

        var decision = NyxIdChatNeedsYouDecisions.ResolveInput(
            pending,
            command,
            currentStateVersion: 41,
            ResolvedAt);

        decision.ShouldCommit.Should().BeTrue();
        decision.NextCommand!.InputContinuation.Answer.Selection.OptionIds.Should()
            .Equal("option-singapore", "option-frankfurt");
        decision.NextCommand.InputContinuation.SelectedOptions.Select(static option => option.OptionId)
            .Should().Equal("option-singapore", "option-frankfurt");
        decision.State.LatestInputResolution.Answer.Selection.OptionIds.Should()
            .Equal("option-singapore", "option-frankfurt");
        decision.State.ToString().Should()
            .Contain("option-singapore")
            .And.Contain("option-frankfurt")
            .And.NotContain("Singapore")
            .And.NotContain("Frankfurt");
    }

    [Fact]
    public void ResolveInput_ShouldPersistNormalizedFreeTextForCommittedContinuationContext()
    {
        const string rawAnswer = "private-answer-sentinel";
        var request = InputRequest();
        request.AllowFreeText = true;
        var pending = NyxIdChatNeedsYouDecisions.RequestInput(
            ActiveState(),
            request,
            AskedAt).State;
        var command = new NyxIdChatInputResolveCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            RequestId = "input-alpha",
            ClientRequestId = "client-input-free-text",
            Answer = new NyxIdChatInputAnswer { FreeText = rawAnswer },
            ExpectedStateVersion = 41,
        };

        var decision = NyxIdChatNeedsYouDecisions.ResolveInput(
            pending,
            command,
            currentStateVersion: 41,
            ResolvedAt);

        decision.ShouldCommit.Should().BeTrue();
        decision.State.LatestInputResolution.Answer.FreeText.Should().Be(rawAnswer);
        decision.State.ToString().Should().Contain(rawAnswer);
        System.Text.Encoding.UTF8.GetString(decision.State.ToByteArray()).Should()
            .Contain(rawAnswer);
        decision.NextCommand!.InputContinuation.Answer.FreeText.Should().Be(rawAnswer);

        var reloaded = NyxIdChatConversationGAgentState.Parser.ParseFrom(
            decision.State.ToByteArray());
        reloaded.LatestInputResolution.Answer.FreeText.Should().Be(rawAnswer);
    }

    [Fact]
    public void ResolveApproval_ShouldFenceStaleVersionsAndKeepFirstDecisionAcrossReload()
    {
        var state = ApprovalState();
        state.PendingApproval = new NyxIdChatPendingApprovalState
        {
            ApprovalRequestId = "approval-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            ToolCallId = "call-approval-alpha",
            ToolName = "repository_delete",
            AskedAt = AskedAt.Clone(),
            Presentation = new NyxIdChatApprovalPresentation
            {
                Action = "delete",
                Target = "repository:repo-alpha",
                ActorLabel = "Aevatar Assistant",
                Reversibility = NyxIdChatApprovalReversibility.Irreversible,
                GrantBoundary = "within_grant",
            },
        };
        var command = new NyxIdChatApprovalResolveCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            RequestId = "approval-alpha",
            ClientRequestId = "client-approval-alpha",
            Approved = false,
            Reason = "Use a non-destructive operation.",
            ExpectedStateVersion = 51,
        };

        var stale = NyxIdChatNeedsYouDecisions.ResolveApproval(
            state,
            command,
            currentStateVersion: 52,
            ResolvedAt);
        stale.ShouldCommit.Should().BeFalse();
        stale.State.PendingApproval.Should().BeEquivalentTo(state.PendingApproval);

        command.ExpectedStateVersion = 52;
        var accepted = NyxIdChatNeedsYouDecisions.ResolveApproval(
            state,
            command,
            currentStateVersion: 52,
            ResolvedAt);
        accepted.ShouldCommit.Should().BeTrue();
        accepted.State.PendingApproval.Should().BeNull();
        accepted.State.LatestApprovalResolution.Approved.Should().BeFalse();
        accepted.State.LatestApprovalResolution.DecisionSha256.Should().NotBeEmpty();
        accepted.State.ToString().Should().NotContain("non-destructive");
        accepted.State.ActiveTask.Steps.Single().Status.Should().Be(NyxIdChatStepStatus.Running);
        accepted.State.ActiveTask.Steps.Single().Operation.Key.OperationGeneration.Should().Be(2);
        accepted.NextCommand.Should().NotBeNull();
        accepted.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolApprovalContinuation);
        accepted.NextCommand.ToolApprovalContinuation.Approved.Should().BeFalse();
        accepted.NextCommand.ToolApprovalContinuation.ApprovalRequestId.Should().Be("approval-alpha");
        accepted.NextCommand.ToolApprovalContinuation.Presentation
            .Skill.SkillName.Should().Be("repository-maintenance");

        var reloaded = NyxIdChatConversationGAgentState.Parser.ParseFrom(
            accepted.State.ToByteArray());
        var exactReplay = NyxIdChatNeedsYouDecisions.ResolveApproval(
            reloaded,
            command,
            currentStateVersion: 53,
            ResolvedAt);
        exactReplay.ShouldCommit.Should().BeFalse();
        exactReplay.IsExactReplay.Should().BeTrue();

        var wrongIdentity = command.Clone();
        wrongIdentity.ConversationActorId = "conversation-other";
        var mismatchedReplay = NyxIdChatNeedsYouDecisions.ResolveApproval(
            reloaded,
            wrongIdentity,
            currentStateVersion: 53,
            ResolvedAt);
        mismatchedReplay.ShouldCommit.Should().BeFalse();
        mismatchedReplay.IsExactReplay.Should().BeFalse();

        var conflicting = command.Clone();
        conflicting.Approved = true;
        conflicting.Reason = "Proceed";
        var conflict = NyxIdChatNeedsYouDecisions.ResolveApproval(
            reloaded,
            conflicting,
            currentStateVersion: 53,
            ResolvedAt);
        conflict.ShouldCommit.Should().BeFalse();
        conflict.IsExactReplay.Should().BeFalse();
        conflict.State.ToByteArray().Should().Equal(reloaded.ToByteArray());
    }

    [Fact]
    public void ResolveApproval_ShouldStillApproveBeforeTheStampedDeadline()
    {
        var state = ExpiringApprovalState();
        var beforeDeadline = Timestamp.FromDateTimeOffset(
            ExpiresAt.ToDateTimeOffset() - TimeSpan.FromSeconds(1));

        var decision = NyxIdChatNeedsYouDecisions.ResolveApproval(
            state,
            ApproveCommand(),
            currentStateVersion: 52,
            beforeDeadline);

        decision.ShouldCommit.Should().BeTrue();
        decision.Resolution!.Outcome.Should().Be(NyxIdChatNeedsYouResolutionOutcome.Accepted);
        decision.Resolution.Approved.Should().BeTrue();
        decision.NextCommand.Should().NotBeNull();
        decision.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolApprovalContinuation);
        decision.NextCommand.ToolApprovalContinuation.Approved.Should().BeTrue();
    }

    [Fact]
    public void ResolveApproval_ShouldFailClosedOnceTheStampedDeadlineElapses()
    {
        var state = ExpiringApprovalState();

        var decision = NyxIdChatNeedsYouDecisions.ResolveApproval(
            state,
            ApproveCommand(),
            currentStateVersion: 52,
            ExpiresAt);

        decision.ShouldCommit.Should().BeTrue();
        decision.IsExactReplay.Should().BeFalse();
        decision.Resolution.Should().NotBeNull();
        decision.Resolution!.RequestId.Should().Be("approval-alpha");
        decision.Resolution.Outcome.Should().Be(NyxIdChatNeedsYouResolutionOutcome.Expired);
        decision.Resolution.Approved.Should().BeFalse(
            "expiry is a denial and can never be resolved as approval");
        decision.NextCommand.Should().BeNull(
            "an expired approval must not dispatch an approval continuation or any effect");
        AssertExpiredApprovalState(decision.State);

        var reloaded = NyxIdChatConversationGAgentState.Parser.ParseFrom(
            decision.State.ToByteArray());
        var lateApprove = NyxIdChatNeedsYouDecisions.ResolveApproval(
            reloaded,
            ApproveCommand(),
            currentStateVersion: 53,
            Timestamp.FromDateTimeOffset(ExpiresAt.ToDateTimeOffset() + TimeSpan.FromSeconds(5)));
        lateApprove.ShouldCommit.Should().BeFalse();
        lateApprove.IsExactReplay.Should().BeFalse();
        lateApprove.Resolution.Should().BeNull(
            "the committed expiry outcome is final and a later approve cannot advance state");
        lateApprove.State.ToByteArray().Should().Equal(reloaded.ToByteArray());
    }

    [Fact]
    public void ExpireApproval_ShouldCommitFencedSystemDenialAtTheDeadline()
    {
        var state = ExpiringApprovalState();
        var signal = new NyxIdChatToolApprovalExpiredSignal
        {
            ApprovalRequestId = "approval-alpha",
            ExpectedExpiresAt = ExpiresAt.Clone(),
        };

        var wrongRequest = signal.Clone();
        wrongRequest.ApprovalRequestId = "approval-other";
        NyxIdChatNeedsYouDecisions.ExpireApproval(state, wrongRequest, ExpiresAt)
            .ShouldCommit.Should().BeFalse();

        var wrongFence = signal.Clone();
        wrongFence.ExpectedExpiresAt = Timestamp.FromDateTimeOffset(
            ExpiresAt.ToDateTimeOffset() + TimeSpan.FromMinutes(1));
        NyxIdChatNeedsYouDecisions.ExpireApproval(state, wrongFence, ExpiresAt)
            .ShouldCommit.Should().BeFalse();

        var beforeDeadline = Timestamp.FromDateTimeOffset(
            ExpiresAt.ToDateTimeOffset() - TimeSpan.FromSeconds(1));
        NyxIdChatNeedsYouDecisions.ExpireApproval(state, signal, beforeDeadline)
            .ShouldCommit.Should().BeFalse();

        var decision = NyxIdChatNeedsYouDecisions.ExpireApproval(state, signal, ExpiresAt);

        decision.ShouldCommit.Should().BeTrue();
        decision.Resolution!.Outcome.Should().Be(NyxIdChatNeedsYouResolutionOutcome.Expired);
        decision.Resolution.Approved.Should().BeFalse();
        decision.NextCommand.Should().BeNull(
            "a timer-driven expiry must not dispatch an approval continuation or any effect");
        AssertExpiredApprovalState(decision.State);
    }

    private static void AssertExpiredApprovalState(NyxIdChatConversationGAgentState state)
    {
        state.PendingApproval.Should().BeNull();
        state.LatestApprovalResolution.Outcome.Should().Be(
            NyxIdChatNeedsYouResolutionOutcome.Expired);
        state.LatestApprovalResolution.Approved.Should().BeFalse();
        var step = state.ActiveTask.Steps.Single();
        step.Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        step.FailureCode.Should().Be(NyxIdChatTaskLifecycle.ApprovalExpired);
        step.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        step.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Cancelled);
        step.Operation.Key.OperationGeneration.Should().Be(1,
            "expiry cancels the exact waiting generation instead of advancing it");
        state.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        state.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        state.ActiveTurn.FailureCode.Should().Be(NyxIdChatTaskLifecycle.ApprovalExpired);
        state.ActiveTurn.TerminalAt.Should().NotBeNull();
    }

    private static NyxIdChatConversationGAgentState ExpiringApprovalState()
    {
        var state = ApprovalState();
        state.ActiveTask.Steps.Single().Required = true;
        state.PendingApproval = new NyxIdChatPendingApprovalState
        {
            ApprovalRequestId = "approval-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            ToolCallId = "call-approval-alpha",
            ToolName = "repository_delete",
            AskedAt = AskedAt.Clone(),
            ExpiresAt = ExpiresAt.Clone(),
        };
        return state;
    }

    private static NyxIdChatApprovalResolveCommand ApproveCommand() => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        RequestId = "approval-alpha",
        ClientRequestId = "client-approval-late",
        Approved = true,
        ExpectedStateVersion = 52,
    };

    private static NyxIdChatInputRequestCommand InputRequest() => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = "step-alpha",
        RequestId = "input-alpha",
        ToolCallId = "call-input-alpha",
        Prompt = " Choose a deployment region. ",
        Options =
        {
            new NyxIdChatInputOption
            {
                OptionId = " option-singapore ",
                Label = " Singapore ",
                Description = " Use the Singapore region. ",
            },
            new NyxIdChatInputOption
            {
                OptionId = " option-frankfurt ",
                Label = " Frankfurt ",
                Description = " Use the Frankfurt region. ",
            },
        },
    };

    private static NyxIdChatConversationGAgentState ActiveState()
    {
        var state = new NyxIdChatConversationGAgentState
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            ProgressSequence = 7,
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Active,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTaskStatus.Active,
                ActiveStepId = "step-alpha",
            },
        };
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = "step-alpha",
            Kind = NyxIdChatStepKind.Input,
            Status = NyxIdChatStepStatus.Waiting,
            Description = "Choose a deployment region.",
            Source = new NyxIdChatStepSource
            {
                Input = new NyxIdChatInputStepSource { RequestId = "input-alpha" },
            },
        });
        return state;
    }

    private static NyxIdChatConversationGAgentState ApprovalState()
    {
        var state = ActiveState();
        var step = state.ActiveTask.Steps.Single();
        step.Kind = NyxIdChatStepKind.Tool;
        step.Source = new NyxIdChatStepSource
        {
            Tool = new NyxIdChatToolStepSource
            {
                ToolName = "repository_delete",
                Presentation = ToolPresentationDescriptors.Skill(
                    "repository_delete",
                    "Repository maintenance",
                    "Delete the exact repository.",
                    "repository-maintenance",
                    "local"),
            },
        };
        step.ApprovalRequestId = "approval-alpha";
        step.MayChangeExternalState = true;
        step.Operation = new NyxIdChatOperationState
        {
            Key = new NyxIdChatOperationKey
            {
                ConversationActorId = "conversation-alpha",
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = "step-alpha",
                OperationId = "operation-approval-alpha",
                OperationGeneration = 1,
            },
            Kind = NyxIdChatStepKind.Tool,
            Phase = NyxIdChatOperationPhase.Succeeded,
            MayChangeExternalState = true,
        };
        return state;
    }

    private static NyxIdChatInputAnswer SelectionAnswer(params string[] optionIds)
    {
        var answer = new NyxIdChatInputAnswer
        {
            Selection = new NyxIdChatInputSelectionAnswer(),
        };
        answer.Selection.OptionIds.AddRange(optionIds);
        return answer;
    }
}
