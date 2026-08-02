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
        accepted.State.ToString().Should().NotContain("Singapore");
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
    public void ResolveInput_ShouldCarryTwoSelectedOptionsOnlyInTransientContinuation()
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
        decision.State.ToString().Should()
            .NotContain("option-singapore")
            .And.NotContain("option-frankfurt")
            .And.NotContain("Singapore")
            .And.NotContain("Frankfurt");
    }

    [Fact]
    public void ResolveInput_ShouldKeepRawFreeTextOnlyInTransientContinuation()
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
        decision.State.ToString().Should().NotContain(rawAnswer);
        System.Text.Encoding.UTF8.GetString(decision.State.ToByteArray()).Should()
            .NotContain(rawAnswer);
        decision.NextCommand!.InputContinuation.Answer.FreeText.Should().Be(rawAnswer);
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
            Tool = new NyxIdChatToolStepSource { ToolName = "repository_delete" },
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
