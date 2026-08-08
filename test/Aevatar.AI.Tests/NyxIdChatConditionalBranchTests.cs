using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatConditionalBranchTests
{
    private static readonly Timestamp Now = Timestamp.FromDateTimeOffset(
        new DateTimeOffset(2026, 8, 9, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void NumericThresholdResolution_ShouldPersistSuggestedAndUserOverride()
    {
        var pending = NyxIdChatNeedsYouDecisions.RequestInput(
            WaitingInputState(),
            NumericInputRequest(),
            Now).State;
        var command = new NyxIdChatInputResolveCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            RequestId = "input-threshold",
            ClientRequestId = "client-threshold",
            ExpectedStateVersion = 11,
            Answer = new NyxIdChatInputAnswer { FreeText = "75" },
        };

        var decision = NyxIdChatNeedsYouDecisions.ResolveInput(
            pending,
            command,
            currentStateVersion: 11,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.Resolution!.NumericThreshold.Should().BeEquivalentTo(
            new NyxIdChatNumericThresholdResolution
            {
                SuggestedValue = 70,
                EffectiveValue = 75,
                Origin = NyxIdChatThresholdOrigin.UserOverride,
            });
        var reloaded = NyxIdChatConversationGAgentState.Parser.ParseFrom(
            decision.State.ToByteArray());
        reloaded.LatestInputResolution.NumericThreshold.EffectiveValue.Should().Be(75);
        reloaded.LatestInputResolution.NumericThreshold.Origin.Should()
            .Be(NyxIdChatThresholdOrigin.UserOverride);
    }

    [Theory]
    [InlineData("75.5")]
    [InlineData("101")]
    [InlineData("not-a-number")]
    public void NumericThresholdResolution_ShouldRejectNonIntegerOrOutOfRange(string answer)
    {
        var pending = NyxIdChatNeedsYouDecisions.RequestInput(
            WaitingInputState(),
            NumericInputRequest(),
            Now).State;

        var decision = NyxIdChatNeedsYouDecisions.ResolveInput(
            pending,
            new NyxIdChatInputResolveCommand
            {
                ScopeId = "scope-alpha",
                ConversationActorId = "conversation-alpha",
                RequestId = "input-threshold",
                ClientRequestId = "client-threshold",
                ExpectedStateVersion = 11,
                Answer = new NyxIdChatInputAnswer { FreeText = answer },
            },
            currentStateVersion: 11,
            Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.State.PendingInput.Should().NotBeNull();
        decision.State.LatestInputResolution.Should().BeNull();
    }

    [Fact]
    public void ConditionBelowThreshold_ShouldAtomicallySkipGuardedEffectWithoutOperation()
    {
        var state = ConditionReadyState();
        var signal = ConditionProposal(state, observedValue: 72);

        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(state, signal, Now);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.State.ActiveTask.SchemaVersion.Should().Be(5);
        var condition = decision.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Condition);
        condition.Status.Should().Be(NyxIdChatStepStatus.Done);
        condition.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        condition.Source.Condition.Condition.Should().BeEquivalentTo(
            new
            {
                SuggestedThreshold = 70L,
                EffectiveThreshold = 75L,
                ThresholdOrigin = NyxIdChatThresholdOrigin.UserOverride,
                ObservedValue = 72L,
                Comparison = NyxIdChatIntegerComparison.Gte,
                Outcome = NyxIdChatConditionOutcome.False,
                GuardedToolName = "repository_update",
            },
            options => options.ExcludingMissingMembers());
        var guarded = decision.State.ActiveTask.Steps.Single(step => step.Guard is not null);
        guarded.Status.Should().Be(NyxIdChatStepStatus.Skipped);
        guarded.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        guarded.Operation.Should().BeNull();
        guarded.Guard.ConditionStepId.Should().Be(condition.StepId);
        decision.NextCommand.Should().NotBeNull();
        decision.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.ConditionContinuation);
        NyxIdChatTurnOperationDispatchPort.MayDispatchExternalEffect(decision.NextCommand!)
            .Should().BeFalse();
        decision.State.PendingApproval.Should().BeNull();

        var completed = NyxIdChatTaskLifecycle.ApplyOperationResult(
            decision.State,
            new NyxIdChatOperationResultSignal
            {
                Key = decision.NextCommand.Key.Clone(),
                Llm = new NyxIdChatLLMOperationResult { Content = "The condition was false." },
            },
            Now);
        completed.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        completed.State.ActiveTask.Steps.Single(step => step.Guard is not null)
            .Operation.Should().BeNull();
        completed.NextCommand.Should().BeNull();
    }

    [Fact]
    public void TrueConditionWithoutGuardedTool_ShouldFailClosedWithoutOperation()
    {
        var source = ConditionReadyState();
        var evaluated = NyxIdChatTaskLifecycle.ApplyOperationResult(
            source,
            ConditionProposal(source, observedValue: 80),
            Now);

        var rejected = NyxIdChatTaskLifecycle.ApplyOperationResult(
            evaluated.State,
            new NyxIdChatOperationResultSignal
            {
                Key = evaluated.NextCommand!.Key.Clone(),
                Llm = new NyxIdChatLLMOperationResult
                {
                    Content = "No guarded tool call was produced.",
                },
            },
            Now);

        rejected.ReasonCode.Should().Be(
            NyxIdChatTaskLifecycle.ConditionGuardedToolRequired);
        rejected.NextCommand.Should().BeNull();
        rejected.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        var guarded = rejected.State.ActiveTask.Steps.Single(step => step.Guard is not null);
        guarded.Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        guarded.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        guarded.Operation.Should().BeNull();
        rejected.State.PendingApproval.Should().BeNull();
    }

    [Fact]
    public void ConditionAtOrAboveThreshold_ShouldPermitOnlyExactGuardedTool()
    {
        var evaluated = NyxIdChatTaskLifecycle.ApplyOperationResult(
            ConditionReadyState(),
            ConditionProposal(ConditionReadyState(), observedValue: 80),
            Now);
        var guardedBefore = evaluated.State.ActiveTask.Steps.Single(step => step.Guard is not null);
        guardedBefore.Status.Should().Be(NyxIdChatStepStatus.Planned);
        guardedBefore.Operation.Should().BeNull();

        var exact = NyxIdChatTaskLifecycle.ApplyOperationResult(
            evaluated.State,
            GuardedToolCall(evaluated.NextCommand!.Key, "repository_update"),
            Now);

        exact.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        var guarded = exact.State.ActiveTask.Steps.Single(step => step.Guard is not null);
        guarded.StepId.Should().Be(guardedBefore.StepId);
        guarded.Operation.Should().NotBeNull();
        guarded.Source.Tool.ToolName.Should().Be("repository_update");
        exact.NextCommand.Should().NotBeNull();
        exact.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
        exact.NextCommand.Tool.ToolName.Should().Be("repository_update");
    }

    [Fact]
    public void TrueConditionWithWrongTool_ShouldFailClosedWithoutDispatch()
    {
        var source = ConditionReadyState();
        var evaluated = NyxIdChatTaskLifecycle.ApplyOperationResult(
            source,
            ConditionProposal(source, observedValue: 80),
            Now);

        var rejected = NyxIdChatTaskLifecycle.ApplyOperationResult(
            evaluated.State,
            GuardedToolCall(evaluated.NextCommand!.Key, "repository_delete"),
            Now);

        rejected.ReasonCode.Should().Be(NyxIdChatTaskLifecycle.ConditionGuardMismatch);
        rejected.NextCommand.Should().BeNull();
        rejected.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        var guarded = rejected.State.ActiveTask.Steps.Single(step => step.Guard is not null);
        guarded.Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        guarded.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        guarded.Operation.Should().BeNull();
    }

    [Fact]
    public void ConditionProposalWithStaleInput_ShouldFailClosed()
    {
        var state = ConditionReadyState();
        state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Llm)
            .DependsOn.Clear();

        var rejected = NyxIdChatTaskLifecycle.ApplyOperationResult(
            state,
            ConditionProposal(state, observedValue: 80),
            Now);

        rejected.ReasonCode.Should().Be(NyxIdChatTaskLifecycle.ConditionSourceStale);
        rejected.State.ActiveTask.Steps.Should().NotContain(step =>
            step.Kind == NyxIdChatStepKind.Condition);
        rejected.NextCommand.Should().BeNull();
    }

    [Fact]
    public void DuplicateConditionProposal_ShouldNotCommitASecondCondition()
    {
        var state = ConditionReadyState();
        var signal = ConditionProposal(state, observedValue: 72);
        var accepted = NyxIdChatTaskLifecycle.ApplyOperationResult(state, signal, Now);

        var replay = NyxIdChatTaskLifecycle.ApplyOperationResult(accepted.State, signal, Now);

        replay.Outcome.Should().Be(NyxIdChatTransitionOutcome.Idempotent);
        replay.State.ActiveTask.Steps.Count(step => step.Kind == NyxIdChatStepKind.Condition)
            .Should().Be(1);
        replay.NextCommand.Should().BeNull();
    }

    [Fact]
    public void OrdinaryNoToolReply_ShouldNotCreateConditionOrSkippedEffect()
    {
        var state = ActiveLlmState();

        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(
            state,
            new NyxIdChatOperationResultSignal
            {
                Key = state.ActiveTask.Steps.Single().Operation.Key.Clone(),
                Llm = new NyxIdChatLLMOperationResult { Content = "No action is needed." },
            },
            Now);

        decision.State.ActiveTask.Steps.Should().ContainSingle();
        decision.State.ActiveTask.Steps.Should().NotContain(step =>
            step.Kind == NyxIdChatStepKind.Condition ||
            step.Status == NyxIdChatStepStatus.Skipped);
    }

    private static NyxIdChatConversationGAgentState ConditionReadyState()
    {
        var state = ActiveLlmState();
        var current = state.ActiveTask.Steps.Single();
        current.Order = 2;
        current.DependsOn.Add("step-input");
        state.ActiveTask.Steps.Insert(0, new NyxIdChatTaskStepState
        {
            StepId = "step-input",
            Order = 1,
            Kind = NyxIdChatStepKind.Input,
            Status = NyxIdChatStepStatus.Done,
            Required = true,
            Source = new NyxIdChatStepSource
            {
                Input = new NyxIdChatInputStepSource { RequestId = "input-threshold" },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
        });
        state.RecentInputResolutions.Add(new NyxIdChatInputResolutionState
        {
            RequestId = "input-threshold",
            ClientRequestId = "client-threshold",
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
            NumericThreshold = new NyxIdChatNumericThresholdResolution
            {
                SuggestedValue = 70,
                EffectiveValue = 75,
                Origin = NyxIdChatThresholdOrigin.UserOverride,
            },
            CommittedAt = Now.Clone(),
        });
        state.LatestInputResolution = state.RecentInputResolutions.Single().Clone();
        return state;
    }

    private static NyxIdChatConversationGAgentState ActiveLlmState()
    {
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = "conversation-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-llm",
            OperationId = "operation-llm",
            OperationGeneration = 1,
        };
        var state = new NyxIdChatConversationGAgentState
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Active,
            },
            LatestTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Active,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TaskId = "task-alpha",
                TurnId = "turn-alpha",
                Status = NyxIdChatTaskStatus.Active,
                ActiveStepId = key.StepId,
                ActiveOperationId = key.OperationId,
                SchemaVersion = 5,
                PlanRevision = 1,
            },
        };
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = key.StepId,
            Order = 1,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Source = new NyxIdChatStepSource { Llm = new NyxIdChatLLMStepSource() },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Operation = new NyxIdChatOperationState
            {
                Key = key,
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Dispatched,
            },
        });
        return state;
    }

    private static NyxIdChatOperationResultSignal ConditionProposal(
        NyxIdChatConversationGAgentState state,
        long observedValue) =>
        new()
        {
            Key = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Llm)
                .Operation.Key.Clone(),
            Llm = new NyxIdChatLLMOperationResult
            {
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "call-condition",
                        ToolName = NyxIdChatConditionEvaluateContract.ToolName,
                        ArgumentsJson =
                            $$"""{"source_input_request_id":"input-threshold","observed_value":{{observedValue}},"guarded_tool_name":"repository_update"}""",
                        Safety = new NyxIdChatToolCallSafety
                        {
                            IsReadOnly = true,
                            MayChangeExternalState = false,
                        },
                    },
                },
            },
        };

    private static NyxIdChatOperationResultSignal GuardedToolCall(
        NyxIdChatOperationKey key,
        string toolName) =>
        new()
        {
            Key = key.Clone(),
            Llm = new NyxIdChatLLMOperationResult
            {
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "call-guarded",
                        ToolName = toolName,
                        ArgumentsJson = "{}",
                        Safety = new NyxIdChatToolCallSafety
                        {
                            MayChangeExternalState = true,
                            SideEffectKind = "repository.update",
                        },
                    },
                },
            },
        };

    private static NyxIdChatConversationGAgentState WaitingInputState()
    {
        var state = new NyxIdChatConversationGAgentState
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
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
                ActiveStepId = "step-input",
            },
        };
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = "step-input",
            Kind = NyxIdChatStepKind.Input,
            Status = NyxIdChatStepStatus.Waiting,
            Source = new NyxIdChatStepSource
            {
                Input = new NyxIdChatInputStepSource { RequestId = "input-threshold" },
            },
        });
        return state;
    }

    private static NyxIdChatInputRequestCommand NumericInputRequest() => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = "step-input",
        RequestId = "input-threshold",
        ToolCallId = "call-input",
        Prompt = "Choose the threshold.",
        AllowFreeText = true,
        NumericThreshold = new NyxIdChatNumericThresholdInputSpec
        {
            SuggestedValue = 70,
            MinimumValue = 0,
            MaximumValue = 100,
        },
    };
}
