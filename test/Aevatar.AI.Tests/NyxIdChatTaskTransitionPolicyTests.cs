using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatTaskTransitionPolicyTests
{
    [Fact]
    public void StartOperation_FromPlannedStep_ShouldReturnNewRunningStateWithoutMutatingInput()
    {
        var state = CreateState();
        var originalBytes = state.ToByteArray();
        var key = CreateKey();

        var decision = NyxIdChatTaskTransitionPolicy.StartOperation(
            state,
            key,
            NyxIdChatStepKind.Tool,
            mayChangeExternalState: true);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.ReasonCode.Should().Be(NyxIdChatTaskTransitionPolicy.OperationStarted);
        decision.State.Should().NotBeSameAs(state);
        decision.State.ActiveTask.ActiveStepId.Should().Be("step-alpha");
        decision.State.ActiveTask.ActiveOperationId.Should().Be("operation-alpha");
        var step = decision.State.ActiveTask.Steps.Single();
        step.Status.Should().Be(NyxIdChatStepStatus.Running);
        step.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
        step.Operation.Key.Should().BeEquivalentTo(key);
        step.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        step.Operation.MayChangeExternalState.Should().BeTrue();
        state.ToByteArray().Should().Equal(originalBytes);
        state.ActiveTask.Steps.Single().Operation.Should().BeNull();
    }

    [Fact]
    public void StartOperation_WithSameCurrentKey_ShouldBeIdempotent()
    {
        var started = Start(CreateState());

        var decision = NyxIdChatTaskTransitionPolicy.StartOperation(
            started,
            CreateKey(),
            NyxIdChatStepKind.Tool,
            mayChangeExternalState: true);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Idempotent);
        decision.ReasonCode.Should().Be(NyxIdChatTaskTransitionPolicy.OperationAlreadyStarted);
        decision.State.Should().BeEquivalentTo(started);
    }

    [Theory]
    [InlineData("conversation-bravo", "turn-alpha", "task-alpha", "step-alpha", "operation-alpha", 1)]
    [InlineData("conversation-alpha", "turn-bravo", "task-alpha", "step-alpha", "operation-alpha", 1)]
    [InlineData("conversation-alpha", "turn-alpha", "task-bravo", "step-alpha", "operation-alpha", 1)]
    [InlineData("conversation-alpha", "turn-alpha", "task-alpha", "step-bravo", "operation-alpha", 1)]
    public void StartOperation_WithIdentityMismatch_ShouldReject(
        string conversationActorId,
        string turnId,
        string taskId,
        string stepId,
        string operationId,
        long generation)
    {
        var state = CreateState();
        var key = CreateKey(
            conversationActorId,
            turnId,
            taskId,
            stepId,
            operationId,
            generation);

        var decision = NyxIdChatTaskTransitionPolicy.StartOperation(
            state,
            key,
            NyxIdChatStepKind.Tool,
            mayChangeExternalState: false);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatTaskTransitionPolicy.IdentityMismatch);
    }

    [Fact]
    public void StartOperation_WithTwoRunningGenerations_ShouldReject()
    {
        var started = Start(CreateState());
        var nextGeneration = CreateKey(operationId: "operation-bravo", generation: 2);

        var decision = NyxIdChatTaskTransitionPolicy.StartOperation(
            started,
            nextGeneration,
            NyxIdChatStepKind.Tool,
            mayChangeExternalState: true);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatTaskTransitionPolicy.OperationAlreadyRunning);
    }

    [Fact]
    public void StartOperation_WithGenerationGap_ShouldReject()
    {
        var state = CreateState();

        var decision = NyxIdChatTaskTransitionPolicy.StartOperation(
            state,
            CreateKey(generation: 2),
            NyxIdChatStepKind.Tool,
            mayChangeExternalState: false);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatTaskTransitionPolicy.InvalidOperationGeneration);
    }

    [Theory]
    [InlineData(NyxIdChatStepStatus.Done)]
    [InlineData(NyxIdChatStepStatus.Skipped)]
    [InlineData(NyxIdChatStepStatus.Cancelled)]
    [InlineData(NyxIdChatStepStatus.Uncertain)]
    public void StartOperation_FromTerminalStep_ShouldReject(NyxIdChatStepStatus status)
    {
        var state = CreateState(status: status);

        var decision = NyxIdChatTaskTransitionPolicy.StartOperation(
            state,
            CreateKey(),
            NyxIdChatStepKind.Tool,
            mayChangeExternalState: false);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatTaskTransitionPolicy.StepAlreadyTerminal);
    }

    [Fact]
    public void StartOperation_AfterControlFence_ShouldReject()
    {
        var state = CreateState();
        state.ControlFence = new NyxIdChatControlFenceState
        {
            Kind = NyxIdChatControlKind.Stop,
            RequestId = "stop-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            OperationGeneration = 0,
            Outcome = NyxIdChatControlOutcome.Accepted,
        };

        var decision = NyxIdChatTaskTransitionPolicy.StartOperation(
            state,
            CreateKey(),
            NyxIdChatStepKind.Tool,
            mayChangeExternalState: false);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatTaskTransitionPolicy.ControlFenceActive);
    }

    [Fact]
    public void ReconcileOperation_WithSuccessfulLLMResult_ShouldCompleteStepAndTask()
    {
        var started = Start(
            CreateState(kind: NyxIdChatStepKind.Llm),
            NyxIdChatStepKind.Llm,
            mayChangeExternalState: false);
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = CreateKey(),
            Llm = new NyxIdChatLLMOperationResult
            {
                Content = "done",
                FinishReason = "stop",
            },
        };

        var decision = NyxIdChatTaskTransitionPolicy.ReconcileOperation(started, signal);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.ReasonCode.Should().Be(NyxIdChatTaskTransitionPolicy.OperationSucceeded);
        decision.State.ActiveTask.Steps.Single().Status.Should().Be(NyxIdChatStepStatus.Done);
        decision.State.ActiveTask.Steps.Single().Operation.Phase.Should()
            .Be(NyxIdChatOperationPhase.Succeeded);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        started.ActiveTask.Steps.Single().Status.Should().Be(NyxIdChatStepStatus.Running);
    }

    [Fact]
    public void ReconcileOperation_WithSuccessfulToolReceipt_ShouldUseTypedEffectEvidence()
    {
        var started = Start(CreateState(), NyxIdChatStepKind.Tool, mayChangeExternalState: true);
        var signal = ToolSignal(
            AgentToolReceiptStatus.Success,
            NyxIdChatEffectEvidence.Confirmed);

        var decision = NyxIdChatTaskTransitionPolicy.ReconcileOperation(started, signal);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        var step = decision.State.ActiveTask.Steps.Single();
        step.Status.Should().Be(NyxIdChatStepStatus.Done);
        step.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.Confirmed);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
    }

    [Fact]
    public void ReconcileOperation_WithTypedToolFailureBeforeEffect_ShouldFailRequiredTask()
    {
        var started = Start(CreateState(), NyxIdChatStepKind.Tool, mayChangeExternalState: true);
        var signal = ToolSignal(
            AgentToolReceiptStatus.Error,
            NyxIdChatEffectEvidence.NotApplied,
            errorCode: "TOOL_FAILED");

        var decision = NyxIdChatTaskTransitionPolicy.ReconcileOperation(started, signal);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.ReasonCode.Should().Be(NyxIdChatTaskTransitionPolicy.OperationFailed);
        var step = decision.State.ActiveTask.Steps.Single();
        step.Status.Should().Be(NyxIdChatStepStatus.Failed);
        step.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        step.FailureCode.Should().Be("TOOL_FAILED");
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
    }

    [Fact]
    public void ReconcileOperation_WithRetryableLlmFailure_ShouldKeepTurnActiveForControl()
    {
        var started = Start(
            CreateState(kind: NyxIdChatStepKind.Llm),
            NyxIdChatStepKind.Llm,
            mayChangeExternalState: false);
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = CreateKey(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = "MODEL_FAILED",
                SafeMessage = "The model attempt failed.",
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            },
        };

        var decision = NyxIdChatTaskTransitionPolicy.ReconcileOperation(started, signal);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        var step = decision.State.ActiveTask.Steps.Single();
        step.Status.Should().Be(NyxIdChatStepStatus.Failed);
        step.AvailableActions.Retry.Should().BeTrue();
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        decision.State.ActiveTask.ActiveStepId.Should().Be(step.StepId);
        decision.State.ActiveTask.ActiveOperationId.Should().BeEmpty();
        decision.State.ActiveTask.FailureCode.Should().Be("MODEL_FAILED");
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        decision.State.ActiveTurn.FailureCode.Should().Be("MODEL_FAILED");
    }

    [Fact]
    public void ReconcileOperation_WithSkippableOptionalFailure_ShouldKeepTurnActiveForControl()
    {
        var started = Start(
            CreateState(required: false),
            NyxIdChatStepKind.Tool,
            mayChangeExternalState: false);

        var decision = NyxIdChatTaskTransitionPolicy.ReconcileOperation(
            started,
            ToolSignal(
                AgentToolReceiptStatus.Error,
                NyxIdChatEffectEvidence.NotApplied,
                errorCode: "OPTIONAL_FAILED"));

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        var step = decision.State.ActiveTask.Steps.Single();
        step.Status.Should().Be(NyxIdChatStepStatus.Failed);
        step.AvailableActions.Skip.Should().BeTrue();
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        decision.State.ActiveTask.ActiveStepId.Should().Be(step.StepId);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
    }

    [Fact]
    public void ReconcileOperation_WithUncertainEffect_ShouldNeverSucceedRequiredTask()
    {
        var started = Start(CreateState(), NyxIdChatStepKind.Tool, mayChangeExternalState: true);
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = CreateKey(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = "TOOL_OUTCOME_UNKNOWN",
                SafeMessage = "The external result could not be proved.",
                ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
            },
        };

        var decision = NyxIdChatTaskTransitionPolicy.ReconcileOperation(started, signal);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        var step = decision.State.ActiveTask.Steps.Single();
        step.Status.Should().Be(NyxIdChatStepStatus.Uncertain);
        step.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Uncertain);
        step.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
    }

    [Fact]
    public void ReconcileOperation_WithRequiredFailedSibling_ShouldNotMarkTaskSucceeded()
    {
        var state = CreateState(kind: NyxIdChatStepKind.Llm);
        state.ActiveTask.Steps.Insert(0, new NyxIdChatTaskStepState
        {
            StepId = "step-required-failed",
            Order = 0,
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Failed,
            Required = true,
            ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
        });
        var started = Start(state, NyxIdChatStepKind.Llm, mayChangeExternalState: false);
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = CreateKey(),
            Llm = new NyxIdChatLLMOperationResult { Content = "done" },
        };

        var decision = NyxIdChatTaskTransitionPolicy.ReconcileOperation(started, signal);

        decision.State.ActiveTask.Steps.Single(x => x.StepId == "step-alpha").Status.Should()
            .Be(NyxIdChatStepStatus.Done);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
    }

    [Fact]
    public void ReconcileOperation_WithBrowserCompletionAndVerifiedPostcondition_ShouldCompleteStep()
    {
        var started = Start(
            CreateState(kind: NyxIdChatStepKind.Postcondition),
            NyxIdChatStepKind.Postcondition,
            mayChangeExternalState: false);
        var signal = PostconditionSignal(verified: true);

        var decision = NyxIdChatTaskTransitionPolicy.ReconcileOperation(started, signal);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        decision.State.ActiveTask.Steps.Single().Status.Should().Be(NyxIdChatStepStatus.Done);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
    }

    [Fact]
    public void ReconcileOperation_WithBrowserCompletionButNoVerifiedPostcondition_ShouldReject()
    {
        var started = Start(
            CreateState(kind: NyxIdChatStepKind.Postcondition),
            NyxIdChatStepKind.Postcondition,
            mayChangeExternalState: false);
        var signal = PostconditionSignal(verified: false);

        var decision = NyxIdChatTaskTransitionPolicy.ReconcileOperation(started, signal);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatTaskTransitionPolicy.PostconditionNotVerified);
        decision.State.ActiveTask.Steps.Single().Status.Should().Be(NyxIdChatStepStatus.Running);
    }

    [Theory]
    [InlineData("conversation-bravo", "turn-alpha", "task-alpha", "step-alpha", "operation-alpha", 1)]
    [InlineData("conversation-alpha", "turn-bravo", "task-alpha", "step-alpha", "operation-alpha", 1)]
    [InlineData("conversation-alpha", "turn-alpha", "task-bravo", "step-alpha", "operation-alpha", 1)]
    [InlineData("conversation-alpha", "turn-alpha", "task-alpha", "step-bravo", "operation-alpha", 1)]
    [InlineData("conversation-alpha", "turn-alpha", "task-alpha", "step-alpha", "operation-bravo", 1)]
    [InlineData("conversation-alpha", "turn-alpha", "task-alpha", "step-alpha", "operation-alpha", 2)]
    public void ReconcileOperation_WithAnyKeyMismatch_ShouldReject(
        string conversationActorId,
        string turnId,
        string taskId,
        string stepId,
        string operationId,
        long generation)
    {
        var started = Start(CreateState());
        var signal = ToolSignal(AgentToolReceiptStatus.Success, NyxIdChatEffectEvidence.NotApplied);
        signal.Key = CreateKey(
            conversationActorId,
            turnId,
            taskId,
            stepId,
            operationId,
            generation);

        var decision = NyxIdChatTaskTransitionPolicy.ReconcileOperation(started, signal);

        decision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.ReasonCode.Should().Be(NyxIdChatTaskTransitionPolicy.OperationKeyMismatch);
    }

    [Fact]
    public void ReconcileOperation_WithDuplicateSameOutcome_ShouldBeIdempotent()
    {
        var started = Start(CreateState());
        var signal = ToolSignal(AgentToolReceiptStatus.Success, NyxIdChatEffectEvidence.NotApplied);
        var completed = NyxIdChatTaskTransitionPolicy.ReconcileOperation(started, signal).State;

        var duplicate = NyxIdChatTaskTransitionPolicy.ReconcileOperation(completed, signal);

        duplicate.Outcome.Should().Be(NyxIdChatTransitionOutcome.Idempotent);
        duplicate.ReasonCode.Should().Be(NyxIdChatTaskTransitionPolicy.OperationAlreadyReconciled);
    }

    [Fact]
    public void ReconcileOperation_WithConflictingTerminalOutcome_ShouldRejectRegression()
    {
        var started = Start(CreateState());
        var completed = NyxIdChatTaskTransitionPolicy.ReconcileOperation(
            started,
            ToolSignal(AgentToolReceiptStatus.Success, NyxIdChatEffectEvidence.NotApplied)).State;

        var conflict = NyxIdChatTaskTransitionPolicy.ReconcileOperation(
            completed,
            ToolSignal(
                AgentToolReceiptStatus.Error,
                NyxIdChatEffectEvidence.NotApplied,
                errorCode: "CONFLICT"));

        conflict.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        conflict.ReasonCode.Should().Be(NyxIdChatTaskTransitionPolicy.TerminalOutcomeConflict);
        conflict.State.ActiveTask.Steps.Single().Status.Should().Be(NyxIdChatStepStatus.Done);
    }

    [Fact]
    public void ResolveAvailableActions_ForFailedNotAppliedToolWithoutRebuildContract_ShouldRejectRetry()
    {
        var step = CreateState(
                status: NyxIdChatStepStatus.Failed,
                effect: NyxIdChatEffectEvidence.NotApplied)
            .ActiveTask.Steps.Single();

        var actions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);

        actions.Retry.Should().BeFalse(
            "effect safety does not reconstruct transient tool arguments or capability");
        actions.Skip.Should().BeFalse();
        actions.Stop.Should().BeFalse();
    }

    [Fact]
    public void ResolveAvailableActions_ForFailedNotAppliedLlmStep_ShouldAllowRetry()
    {
        var step = CreateState(
                status: NyxIdChatStepStatus.Failed,
                effect: NyxIdChatEffectEvidence.NotApplied,
                kind: NyxIdChatStepKind.Llm)
            .ActiveTask.Steps.Single();

        var actions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);

        actions.Retry.Should().BeTrue(
            "the typed turn prompt/input can rebuild an LLM attempt with new transient capability");
        actions.Skip.Should().BeFalse();
        actions.Stop.Should().BeFalse();
    }

    [Fact]
    public void ResolveAvailableActions_ForPotentiallyChangedNonIdempotentStep_ShouldRejectUnsafeRetry()
    {
        var step = CreateState(
                status: NyxIdChatStepStatus.Uncertain,
                effect: NyxIdChatEffectEvidence.MayHaveChanged)
            .ActiveTask.Steps.Single();
        step.Operation = new NyxIdChatOperationState
        {
            Key = CreateKey(),
            Phase = NyxIdChatOperationPhase.Uncertain,
            MayChangeExternalState = true,
            Idempotent = false,
        };

        var actions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);

        actions.Retry.Should().BeFalse();
        actions.Skip.Should().BeFalse();
    }

    [Fact]
    public void ResolveAvailableActions_ForPotentiallyChangedIdempotentToolWithoutRebuildContract_ShouldRejectRetry()
    {
        var step = CreateState(
                status: NyxIdChatStepStatus.Uncertain,
                effect: NyxIdChatEffectEvidence.MayHaveChanged)
            .ActiveTask.Steps.Single();
        step.Operation = new NyxIdChatOperationState
        {
            Key = CreateKey(),
            Phase = NyxIdChatOperationPhase.Uncertain,
            MayChangeExternalState = true,
            Idempotent = true,
            IdempotencyKey = "effect-alpha",
        };

        var actions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);

        actions.Retry.Should().BeFalse(
            "an idempotency key does not by itself reconstruct intentionally non-durable tool input");
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void ResolveAvailableActions_ShouldExposeSkipOnlyWhenPolicyAllows(
        bool required,
        bool safeToSkip,
        bool expected)
    {
        var step = CreateState(
                status: NyxIdChatStepStatus.Failed,
                required: required,
                safeToSkip: safeToSkip)
            .ActiveTask.Steps.Single();

        var actions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);

        actions.Skip.Should().Be(expected);
    }

    [Theory]
    [InlineData(NyxIdChatStepStatus.Planned, true)]
    [InlineData(NyxIdChatStepStatus.Waiting, true)]
    [InlineData(NyxIdChatStepStatus.Running, true)]
    [InlineData(NyxIdChatStepStatus.Done, false)]
    [InlineData(NyxIdChatStepStatus.Failed, false)]
    [InlineData(NyxIdChatStepStatus.Skipped, false)]
    [InlineData(NyxIdChatStepStatus.Cancelled, false)]
    [InlineData(NyxIdChatStepStatus.Uncertain, false)]
    public void ResolveAvailableActions_ShouldExposeStopOnlyForActiveStep(
        NyxIdChatStepStatus status,
        bool expected)
    {
        var step = CreateState(status: status).ActiveTask.Steps.Single();

        NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step).Stop.Should().Be(expected);
    }

    private static NyxIdChatConversationGAgentState Start(
        NyxIdChatConversationGAgentState state,
        NyxIdChatStepKind kind = NyxIdChatStepKind.Tool,
        bool mayChangeExternalState = true) =>
        NyxIdChatTaskTransitionPolicy.StartOperation(
                state,
                CreateKey(),
                kind,
                mayChangeExternalState)
            .State;

    private static NyxIdChatOperationResultSignal ToolSignal(
        AgentToolReceiptStatus status,
        NyxIdChatEffectEvidence effect,
        string errorCode = "") =>
        new()
        {
            Key = CreateKey(),
            Tool = new NyxIdChatToolOperationResult
            {
                ResultJson = "{}",
                ExternalEffect = effect,
                Receipt = new AgentToolReceipt
                {
                    CallId = "call-alpha",
                    ToolName = "tool-alpha",
                    Status = status,
                    ErrorCode = errorCode,
                    ErrorMessage = string.IsNullOrEmpty(errorCode) ? "" : "The tool failed.",
                },
            },
        };

    private static NyxIdChatOperationResultSignal PostconditionSignal(bool verified) =>
        new()
        {
            Key = CreateKey(),
            ActionPostcondition = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = "action-alpha",
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = verified,
                FailureCode = verified ? "" : "POSTCONDITION_NOT_VERIFIED",
                SafeMessage = verified ? "" : "The requested resource state was not found.",
                Resource = new NyxIdChatSafeResourceRef
                {
                    UserService = new NyxIdChatUserServiceRef
                    {
                        UserServiceId = "service-alpha",
                    },
                },
            },
        };

    private static NyxIdChatOperationKey CreateKey(
        string conversationActorId = "conversation-alpha",
        string turnId = "turn-alpha",
        string taskId = "task-alpha",
        string stepId = "step-alpha",
        string operationId = "operation-alpha",
        long generation = 1) =>
        new()
        {
            ConversationActorId = conversationActorId,
            TurnId = turnId,
            TaskId = taskId,
            StepId = stepId,
            OperationId = operationId,
            OperationGeneration = generation,
        };

    private static NyxIdChatConversationGAgentState CreateState(
        NyxIdChatStepStatus status = NyxIdChatStepStatus.Planned,
        NyxIdChatEffectEvidence effect = NyxIdChatEffectEvidence.NotStarted,
        bool required = true,
        bool safeToSkip = false,
        NyxIdChatStepKind kind = NyxIdChatStepKind.Tool)
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
            },
            LatestTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                ClientRequestId = "client-alpha",
                Status = NyxIdChatTurnStatus.Active,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTaskStatus.Active,
            },
        };
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = "step-alpha",
            Order = 1,
            Kind = kind,
            Status = status,
            Required = required,
            SafeToSkip = safeToSkip,
            RetryInputRebuildable = kind == NyxIdChatStepKind.Llm,
            ExternalEffect = effect,
        });
        return state;
    }
}
