using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatCanaryEffectFaultDecisionsTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 9, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryArm_ShouldPersistExactSourceLlmIntentAndRejectExactReplay()
    {
        var state = ConversationState();
        var command = ArmCommand();

        var armed = NyxIdChatCanaryEffectFaultDecisions.TryArm(
            state,
            command,
            stateVersion: 17,
            Timestamp.FromDateTimeOffset(Now),
            out var next);

        armed.Should().BeTrue();
        next.Should().NotBeSameAs(state);
        next.ProgressSequence.Should().Be(5);
        next.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Armed);
        next.CanaryEffectFault.ArmIntent.Should().NotBeNull();
        next.CanaryEffectFault.ArmIntent.ArmId.Should().Be("arm-alpha");
        next.CanaryEffectFault.ArmIntent.OwnerSubject.Should().Be("owner-alpha");
        next.CanaryEffectFault.ArmIntent.SourceOperationKey.Should().BeEquivalentTo(
            SourceOperationKey());
        next.CanaryEffectFault.Directive.Should().BeNull(
            "the target Tool operation does not exist while its source LLM is running");

        var reloaded = NyxIdChatConversationGAgentState.Parser.ParseFrom(next.ToByteArray());
        NyxIdChatCanaryEffectFaultDecisions.TryArm(
                reloaded,
                command.Clone(),
                stateVersion: 18,
                Timestamp.FromDateTimeOffset(Now.AddSeconds(1)),
                out var replay)
            .Should().BeFalse("an exact arm is idempotent even after protobuf reload");
        replay.Should().BeSameAs(reloaded);
        reloaded.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Armed);
        reloaded.CanaryEffectFault.Directive.Should().BeNull();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("conversation")]
    [InlineData("source_conversation")]
    [InlineData("source_operation")]
    [InlineData("generation")]
    [InlineData("service")]
    [InlineData("version")]
    [InlineData("expired")]
    [InlineData("too_long")]
    public void TryArm_ShouldRejectMismatchedOrOutOfWindowSourceIntent(string mismatch)
    {
        var state = ConversationState();
        var command = ArmCommand();
        var stateVersion = 17L;
        switch (mismatch)
        {
            case "owner":
                command.OwnerSubject = "owner-beta";
                break;
            case "conversation":
                command.ConversationActorId = "conversation-beta";
                break;
            case "source_conversation":
                command.SourceOperationKey.ConversationActorId = "conversation-beta";
                break;
            case "source_operation":
                command.SourceOperationKey.OperationId = "operation-llm-beta";
                break;
            case "generation":
                command.SourceOperationKey.OperationGeneration = 2;
                break;
            case "service":
                command.ServiceInstanceId = string.Empty;
                break;
            case "version":
                stateVersion = 18;
                break;
            case "expired":
                command.ExpiresAt = Timestamp.FromDateTimeOffset(Now);
                break;
            case "too_long":
                command.ExpiresAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(16));
                break;
        }

        NyxIdChatCanaryEffectFaultDecisions.TryArm(
                state,
                command,
                stateVersion,
                Timestamp.FromDateTimeOffset(Now),
                out var next)
            .Should().BeFalse();
        next.Should().BeSameAs(state);
        state.CanaryEffectFault.Should().BeNull();
    }

    [Theory]
    [InlineData("active_turn")]
    [InlineData("active_task")]
    [InlineData("turn_id")]
    [InlineData("task_id")]
    [InlineData("active_step")]
    [InlineData("active_operation")]
    [InlineData("step_kind")]
    [InlineData("step_status")]
    [InlineData("operation_phase")]
    [InlineData("duplicate_source_step")]
    public void TryArm_ShouldRequireOneExactActiveRunningLlmSource(string mismatch)
    {
        var state = ConversationState();
        var sourceStep = state.ActiveTask.Steps.Single();
        switch (mismatch)
        {
            case "active_turn":
                state.ActiveTurn.Status = NyxIdChatTurnStatus.Succeeded;
                break;
            case "active_task":
                state.ActiveTask.Status = NyxIdChatTaskStatus.Succeeded;
                break;
            case "turn_id":
                state.ActiveTurn.TurnId = "turn-beta";
                break;
            case "task_id":
                state.ActiveTask.TaskId = "task-beta";
                break;
            case "active_step":
                state.ActiveTask.ActiveStepId = "step-llm-beta";
                break;
            case "active_operation":
                state.ActiveTask.ActiveOperationId = "operation-llm-beta";
                break;
            case "step_kind":
                sourceStep.Kind = NyxIdChatStepKind.Tool;
                break;
            case "step_status":
                sourceStep.Status = NyxIdChatStepStatus.Done;
                break;
            case "operation_phase":
                sourceStep.Operation.Phase = NyxIdChatOperationPhase.Succeeded;
                break;
            case "duplicate_source_step":
                state.ActiveTask.Steps.Add(sourceStep.Clone());
                break;
        }

        NyxIdChatCanaryEffectFaultDecisions.TryArm(
                state,
                ArmCommand(),
                stateVersion: 17,
                Timestamp.FromDateTimeOffset(Now),
                out var next)
            .Should().BeFalse();
        next.Should().BeSameAs(state);
        state.CanaryEffectFault.Should().BeNull();
    }

    [Fact]
    public void TryAttachToDirectToolDispatch_ShouldSealDistinctTargetAndForwardOnlyOnce()
    {
        var state = Arm(ConversationState());
        var sourceOperationKey = SourceOperationKey();
        var dispatch = EffectDispatch();

        var directive = NyxIdChatCanaryEffectFaultDecisions.TryAttachToDirectToolDispatch(
            state,
            sourceOperationKey,
            dispatch,
            Timestamp.FromDateTimeOffset(Now.AddMinutes(1)));

        directive.Should().NotBeNull();
        directive.Should().BeEquivalentTo(state.CanaryEffectFault.Directive);
        directive!.Key.Should().BeEquivalentTo(TargetOperationKey());
        directive.Key.Should().NotBeEquivalentTo(sourceOperationKey);
        directive.CatalogDigest.Should().Be(dispatch.Tool.OperationAdmission.CatalogDigest);
        state.CanaryEffectFault.ArmIntent.SourceOperationKey.Should().BeEquivalentTo(
            sourceOperationKey);
        state.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Forwarded);
        state.CanaryEffectFault.ForwardedAt.Should().NotBeNull();
        state.CanaryEffectFault.ConsumedAt.Should().BeNull();
        dispatch.InputCase.Should().Be(NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
        dispatch.Tool.CanaryEffectFault.Should().BeEquivalentTo(directive);

        NyxIdChatCanaryEffectFaultDecisions.TryAttachToDirectToolDispatch(
                state,
                sourceOperationKey,
                dispatch,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1)))
            .Should().BeNull("the actor-owned directive is forwarded only once");
    }

    [Theory]
    [InlineData("source")]
    [InlineData("owner")]
    [InlineData("service")]
    [InlineData("catalog")]
    [InlineData("generation")]
    [InlineData("effect")]
    [InlineData("admission")]
    [InlineData("read_back")]
    [InlineData("read_back_service")]
    public void TryAttachToDirectToolDispatch_ShouldRejectAnyExactBindingMismatch(
        string mismatch)
    {
        var state = Arm(ConversationState());
        var sourceOperationKey = SourceOperationKey();
        var dispatch = EffectDispatch();
        switch (mismatch)
        {
            case "source":
                sourceOperationKey.OperationId = "operation-llm-beta";
                break;
            case "owner":
                state.OwnerSubject = "owner-beta";
                break;
            case "service":
                dispatch.Tool.OperationAdmission.ServiceInstanceId =
                    "connected-service-beta";
                break;
            case "catalog":
                dispatch.Tool.OperationAdmission.ReadBack.ReadOperation.CatalogDigest =
                    $"sha256:{new string('b', 64)}";
                break;
            case "generation":
                dispatch.Key.OperationGeneration = 2;
                break;
            case "effect":
                dispatch.Tool.MayChangeExternalState = false;
                break;
            case "admission":
                dispatch.Tool.OperationAdmission.ExecutionPolicy.AllowedExecutionModes.Clear();
                break;
            case "read_back":
                dispatch.Tool.OperationAdmission.ReadBack = null;
                break;
            case "read_back_service":
                dispatch.Tool.OperationAdmission.ReadBack.ReadOperation.ServiceInstanceId =
                    "connected-service-beta";
                break;
        }

        NyxIdChatCanaryEffectFaultDecisions.TryAttachToDirectToolDispatch(
                state,
                sourceOperationKey,
                dispatch,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1)))
            .Should().BeNull();
        state.CanaryEffectFault.Status.Should().Be(
            mismatch == "source"
                ? NyxIdChatCanaryEffectFaultStatus.Armed
                : NyxIdChatCanaryEffectFaultStatus.Expired);
        dispatch.Tool.CanaryEffectFault.Should().BeNull();
    }

    [Fact]
    public void TryAttachToDirectToolDispatch_AfterExpiry_ShouldCommitExpiredStatus()
    {
        var state = Arm(ConversationState());

        NyxIdChatCanaryEffectFaultDecisions.TryAttachToDirectToolDispatch(
                state,
                SourceOperationKey(),
                EffectDispatch(),
                Timestamp.FromDateTimeOffset(Now.AddMinutes(6)))
            .Should().BeNull();

        state.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Expired);
        state.CanaryEffectFault.ConsumedAt.Should().BeNull();
    }

    [Fact]
    public void MatchesTurnDispatch_ShouldValidateExactDirectToolCommand()
    {
        var state = ForwardedState(out var dispatch);
        var directive = state.CanaryEffectFault.Directive.Clone();

        NyxIdChatCanaryEffectFaultDecisions.MatchesTurnDispatch(
                directive,
                dispatch,
                dispatch.Tool.ToolContext,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1)))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("key")]
    [InlineData("service")]
    [InlineData("catalog")]
    [InlineData("read_back")]
    [InlineData("read_back_service")]
    [InlineData("admission")]
    [InlineData("effect")]
    [InlineData("generation")]
    [InlineData("expired")]
    public void MatchesTurnDispatch_ShouldFailClosedForAnyDirectiveMismatch(string mismatch)
    {
        var state = ForwardedState(out var dispatch);
        var directive = state.CanaryEffectFault.Directive.Clone();
        var now = Timestamp.FromDateTimeOffset(Now.AddMinutes(1));
        switch (mismatch)
        {
            case "owner":
                dispatch.Tool.ToolContext.Caller.OwnerSubject = "owner-beta";
                break;
            case "key":
                dispatch.Key.OperationId = "operation-tool-beta";
                break;
            case "service":
                dispatch.Tool.OperationAdmission.ServiceInstanceId =
                    "connected-service-beta";
                break;
            case "catalog":
                dispatch.Tool.OperationAdmission.CatalogDigest =
                    $"sha256:{new string('b', 64)}";
                dispatch.Tool.OperationAdmission.ReadBack.ReadOperation.CatalogDigest =
                    dispatch.Tool.OperationAdmission.CatalogDigest;
                break;
            case "read_back":
                dispatch.Tool.OperationAdmission.ReadBack = null;
                break;
            case "read_back_service":
                dispatch.Tool.OperationAdmission.ReadBack.ReadOperation.ServiceInstanceId =
                    "connected-service-beta";
                break;
            case "admission":
                dispatch.Tool.OperationAdmission.ExecutionPolicy.AllowedExecutionModes.Clear();
                break;
            case "effect":
                dispatch.Tool.MayChangeExternalState = false;
                break;
            case "generation":
                dispatch.Key.OperationGeneration = 2;
                directive.Key.OperationGeneration = 2;
                break;
            case "expired":
                now = Timestamp.FromDateTimeOffset(Now.AddMinutes(6));
                break;
        }

        NyxIdChatCanaryEffectFaultDecisions.MatchesTurnDispatch(
                directive,
                dispatch,
                dispatch.Tool.ToolContext,
                now)
            .Should().BeFalse();
    }

    [Fact]
    public void TryMarkConsumed_ShouldRequireExactMaterializedTargetToolStep()
    {
        var state = ForwardedState(out var dispatch);
        var signal = ExactConsumedSignal(dispatch, NyxIdApprovalDecisionMode.Unspecified);

        NyxIdChatCanaryEffectFaultDecisions.TryMarkConsumed(
                state,
                signal,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1).AddSeconds(2)),
                out var consumed)
            .Should().BeTrue();

        consumed.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Consumed);
        consumed.CanaryEffectFault.ConsumedAt.Should().Be(
            Timestamp.FromDateTimeOffset(Now.AddMinutes(1).AddSeconds(2)));
        consumed.CanaryEffectFault.ApprovalRequestId.Should().Be("approval-7001-alpha");
        consumed.CanaryEffectFault.ReceiptStatus.Should().Be(AgentToolReceiptStatus.Denied);
        consumed.CanaryEffectFault.ApprovalDecisionMode.Should().Be(
            NyxIdApprovalDecisionMode.Unspecified);
        consumed.CanaryEffectFault.ApprovalTerminalOutcome.Should().Be(
            NyxIdApprovalTerminalOutcome.Rejected);
        var step = TargetToolStep(consumed);
        step.Source.Tool.OperationAdmission.Should().NotBeNull();
        step.RetryToolInput.Should().NotBeNull();
        step.ApprovalRequestId.Should().Be("approval-7001-alpha");
        step.ApprovalObservation.Should().NotBeNull();
        step.ApprovalObservation.TerminalOutcome.Should().Be(
            NyxIdApprovalTerminalOutcome.Rejected);
        consumed.ProgressSequence.Should().Be(state.ProgressSequence + 1);

        var wrongTurn = signal.Clone();
        wrongTurn.TurnActorId = "turn-actor-beta";
        NyxIdChatCanaryEffectFaultDecisions.TryMarkConsumed(
                state,
                wrongTurn,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1).AddSeconds(2)),
                out _)
            .Should().BeFalse();
        NyxIdChatCanaryEffectFaultDecisions.TryMarkConsumed(
                consumed,
                signal,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1).AddSeconds(2)),
                out _)
            .Should().BeFalse("an exact consumed acknowledgement is idempotent");
    }

    [Fact]
    public void TryMarkConsumed_ShouldNotUseCrossActorClockAsProtocolOrdering()
    {
        var state = ForwardedState(out var dispatch);
        var conversationNow = Timestamp.FromDateTimeOffset(Now.AddMinutes(1));
        var signal = ExactConsumedSignal(dispatch, NyxIdApprovalDecisionMode.PerRequest);
        signal.ConsumedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-10));

        NyxIdChatCanaryEffectFaultDecisions.TryMarkConsumed(
                state,
                signal,
                conversationNow,
                out var consumed)
            .Should().BeTrue();
        consumed.CanaryEffectFault.ConsumedAt.Should().Be(conversationNow);
    }

    [Theory]
    [InlineData(NyxIdApprovalDecisionMode.Unspecified)]
    [InlineData(NyxIdApprovalDecisionMode.PerRequest)]
    public void TryMarkConsumed_ShouldAcceptOnlyNonGrantRejectedApprovalEvidence(
        NyxIdApprovalDecisionMode decisionMode)
    {
        var state = ForwardedState(out var dispatch);
        var signal = ExactConsumedSignal(dispatch, decisionMode);

        NyxIdChatCanaryEffectFaultDecisions.TryMarkConsumed(
                state,
                signal,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1).AddSeconds(2)),
                out var consumed)
            .Should().BeTrue();

        consumed.CanaryEffectFault.ApprovalDecisionMode.Should().Be(decisionMode);
        consumed.CanaryEffectFault.ApprovalTerminalOutcome.Should().Be(
            NyxIdApprovalTerminalOutcome.Rejected);
        TargetToolStep(consumed).ApprovalObservation.DecisionMode.Should().Be(decisionMode);
    }

    [Fact]
    public void TryMarkConsumed_ShouldFailClosedWhenTargetKeyMatchesMultipleSteps()
    {
        var state = ForwardedState(out var dispatch);
        state.ActiveTask.Steps.Add(TargetToolStep(state).Clone());
        var signal = ExactConsumedSignal(dispatch, NyxIdApprovalDecisionMode.Unspecified);

        var consumed = true;
        Action act = () =>
        {
            consumed = NyxIdChatCanaryEffectFaultDecisions.TryMarkConsumed(
                state,
                signal,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1).AddSeconds(2)),
                out _);
        };

        act.Should().NotThrow();
        consumed.Should().BeFalse();
        state.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Forwarded);
    }

    [Theory]
    [InlineData("missing_admission")]
    [InlineData("missing_retry")]
    [InlineData("step_service")]
    [InlineData("step_tool")]
    public void TryMarkConsumed_ShouldRequireTargetToolDispatchFacts(string mismatch)
    {
        var state = ForwardedState(out var dispatch);
        var targetStep = TargetToolStep(state);
        switch (mismatch)
        {
            case "missing_admission":
                targetStep.Source.Tool.OperationAdmission = null;
                break;
            case "missing_retry":
                targetStep.RetryToolInput = null;
                break;
            case "step_service":
                targetStep.Source.Tool.OperationAdmission.ServiceInstanceId =
                    "connected-service-beta";
                break;
            case "step_tool":
                targetStep.Source.Tool.ToolName = "tool-beta";
                break;
        }

        NyxIdChatCanaryEffectFaultDecisions.TryMarkConsumed(
                state,
                ExactConsumedSignal(dispatch, NyxIdApprovalDecisionMode.Unspecified),
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1).AddSeconds(2)),
                out var unchanged)
            .Should().BeFalse();
        unchanged.Should().BeSameAs(state);
        state.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Forwarded);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("request_id")]
    [InlineData("receipt_status")]
    [InlineData("grant_mode")]
    [InlineData("terminal_outcome")]
    [InlineData("subject")]
    [InlineData("call")]
    [InlineData("tool")]
    public void TryMarkConsumed_ShouldRejectIncompleteOrMismatchedApprovalEvidence(
        string mismatch)
    {
        var state = ForwardedState(out var dispatch);
        var signal = ExactConsumedSignal(dispatch, NyxIdApprovalDecisionMode.Unspecified);
        switch (mismatch)
        {
            case "service":
                signal.ServiceInstanceId = "connected-service-beta";
                break;
            case "request_id":
                signal.ApprovalRequestId = string.Empty;
                break;
            case "receipt_status":
                signal.ReceiptStatus = AgentToolReceiptStatus.ApprovalRequired;
                break;
            case "grant_mode":
                signal.ApprovalDecisionMode = NyxIdApprovalDecisionMode.Grant;
                break;
            case "terminal_outcome":
                signal.ApprovalTerminalOutcome = NyxIdApprovalTerminalOutcome.Expired;
                break;
            case "subject":
                signal.ApprovalSubjectId = "connected-service-beta";
                break;
            case "call":
                signal.ApprovalCallId = "call-beta";
                break;
            case "tool":
                signal.ApprovalToolName = "tool-beta";
                break;
        }

        NyxIdChatCanaryEffectFaultDecisions.TryMarkConsumed(
                state,
                signal,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1).AddSeconds(2)),
                out var unchanged)
            .Should().BeFalse();
        unchanged.Should().BeSameAs(state);
        state.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Forwarded);
        TargetToolStep(state).ApprovalObservation.Should().BeNull();
    }

    private static NyxIdChatConversationGAgentState ForwardedState(
        out NyxIdChatOperationDispatchCommand dispatch)
    {
        var state = Arm(ConversationState());
        dispatch = EffectDispatch();
        NyxIdChatCanaryEffectFaultDecisions.TryAttachToDirectToolDispatch(
                state,
                SourceOperationKey(),
                dispatch,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1)))
            .Should().NotBeNull();
        MaterializeTargetToolStep(state, dispatch);
        return state;
    }

    private static NyxIdChatCanaryEffectFaultConsumedSignal ExactConsumedSignal(
        NyxIdChatOperationDispatchCommand dispatch,
        NyxIdApprovalDecisionMode decisionMode) => new()
    {
        ArmId = "arm-alpha",
        Key = dispatch.Key.Clone(),
        TurnActorId = NyxIdChatTurnActorIds.ForTurn("conversation-alpha", "turn-alpha"),
        ConsumedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(1).AddSeconds(1)),
        ServiceInstanceId = "connected-service-alpha",
        ApprovalRequestId = "approval-7001-alpha",
        ReceiptStatus = AgentToolReceiptStatus.Denied,
        ApprovalDecisionMode = decisionMode,
        ApprovalTerminalOutcome = NyxIdApprovalTerminalOutcome.Rejected,
        ApprovalSubjectKind = "nyxid.user-service",
        ApprovalSubjectId = "connected-service-alpha",
        ApprovalCallId = "call-alpha",
        ApprovalToolName = "tool-alpha",
    };

    private static NyxIdChatConversationGAgentState Arm(NyxIdChatConversationGAgentState state)
    {
        NyxIdChatCanaryEffectFaultDecisions.TryArm(
                state,
                ArmCommand(),
                stateVersion: 17,
                Timestamp.FromDateTimeOffset(Now),
                out var next)
            .Should().BeTrue();
        return next;
    }

    private static NyxIdChatConversationGAgentState ConversationState()
    {
        var sourceOperationKey = SourceOperationKey();
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = sourceOperationKey.ConversationActorId,
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
            ProgressSequence = 4,
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = sourceOperationKey.TurnId,
                TaskId = sourceOperationKey.TaskId,
                Status = NyxIdChatTurnStatus.Active,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TaskId = sourceOperationKey.TaskId,
                TurnId = sourceOperationKey.TurnId,
                Status = NyxIdChatTaskStatus.Active,
                ActiveStepId = sourceOperationKey.StepId,
                ActiveOperationId = sourceOperationKey.OperationId,
                PlanId = "plan-alpha",
                PlanRevision = 1,
            },
        };
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = sourceOperationKey.StepId,
            Order = 1,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Operation = new NyxIdChatOperationState
            {
                Key = sourceOperationKey,
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Dispatched,
            },
            Source = new NyxIdChatStepSource
            {
                Llm = new NyxIdChatLLMStepSource { Model = "model-alpha" },
            },
        });
        return state;
    }

    private static void MaterializeTargetToolStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationDispatchCommand dispatch)
    {
        var sourceStep = state.ActiveTask.Steps.Single();
        sourceStep.Status = NyxIdChatStepStatus.Done;
        sourceStep.Operation.Phase = NyxIdChatOperationPhase.Succeeded;
        var targetStep = new NyxIdChatTaskStepState
        {
            StepId = dispatch.Key.StepId,
            Order = 2,
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            MayChangeExternalState = true,
            Operation = new NyxIdChatOperationState
            {
                Key = dispatch.Key.Clone(),
                Kind = NyxIdChatStepKind.Tool,
                Phase = NyxIdChatOperationPhase.Dispatched,
                MayChangeExternalState = true,
                IdempotencyKey = dispatch.Key.OperationId,
            },
            Source = new NyxIdChatStepSource
            {
                Tool = new NyxIdChatToolStepSource
                {
                    ToolName = dispatch.Tool.ToolName,
                    ServiceId = dispatch.Tool.OperationAdmission.ServiceInstanceId,
                    ServiceSlug = dispatch.Tool.OperationAdmission.ServiceSlug,
                    OperationAdmission = dispatch.Tool.OperationAdmission.Clone(),
                },
            },
            RetryToolInput = new NyxIdChatRetryToolInputState
            {
                CallId = dispatch.Tool.CallId,
                ToolName = dispatch.Tool.ToolName,
                Arguments = new Struct(),
                OperationAdmission = dispatch.Tool.OperationAdmission.Clone(),
            },
        };
        targetStep.DependsOn.Add(sourceStep.StepId);
        state.ActiveTask.Steps.Add(targetStep);
        state.ActiveTask.ActiveStepId = targetStep.StepId;
        state.ActiveTask.ActiveOperationId = targetStep.Operation.Key.OperationId;
    }

    private static NyxIdChatTaskStepState TargetToolStep(
        NyxIdChatConversationGAgentState state) =>
        state.ActiveTask.Steps.Single(step =>
            step.Operation?.Key?.Equals(state.CanaryEffectFault.Directive.Key) == true);

    private static NyxIdChatCanaryEffectFaultArmCommand ArmCommand() => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        ArmId = "arm-alpha",
        ClientRequestId = "client-arm-alpha",
        SourceOperationKey = SourceOperationKey(),
        ServiceInstanceId = "connected-service-alpha",
        OwnerSubject = "owner-alpha",
        ExpiresAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(5)),
        ExpectedStateVersion = 17,
    };

    private static NyxIdChatOperationDispatchCommand EffectDispatch() => new()
    {
        Key = TargetOperationKey(),
        Tool = new NyxIdChatToolOperationInput
        {
            CallId = "call-alpha",
            ToolName = "tool-alpha",
            ArgumentsJson = "{\"value\":1}",
            MayChangeExternalState = true,
            Idempotent = false,
            IdempotencyKey = "operation-tool-alpha",
            OperationAdmission = WriteAdmission(),
            ToolContext = new AgentToolExecutionContextPayload
            {
                Caller = new AgentToolCallerContextPayload
                {
                    ScopeId = "scope-alpha",
                    OwnerSubject = "owner-alpha",
                    ResponseId = "response-alpha",
                },
            },
        },
    };

    private static NyxIdChatOperationKey SourceOperationKey() => new()
    {
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = "step-llm-alpha",
        OperationId = "operation-llm-alpha",
        OperationGeneration = 1,
    };

    private static NyxIdChatOperationKey TargetOperationKey() => new()
    {
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = "step-tool-alpha",
        OperationId = "operation-tool-alpha",
        OperationGeneration = 1,
    };

    private static AgentToolOperationAdmissionPayload WriteAdmission()
    {
        var admission = new AgentToolOperationAdmissionPayload
        {
            ServiceInstanceId = "connected-service-alpha",
            ServiceSlug = "service-slug-alpha",
            PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
            {
                EndpointId = "endpoint-effect-alpha",
            },
            AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
            HttpMethod = "POST",
            PathTemplate = "/records",
            ContractDigest = new string('b', 64),
            CatalogDigest = $"sha256:{new string('a', 64)}",
            ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
            {
                Risk = AgentToolOperationRiskPayload.Write,
                Approval = AgentToolOperationApprovalPayload.Required,
                EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
                AllowedExecutionModes =
                {
                    AgentToolOperationExecutionModePayload.Interactive,
                },
            },
        };
        admission.ReadBack = new AgentToolOperationReadBackPayload
        {
            ReadOperation = ReadAdmission(),
            Arguments = new Struct(),
            CheckName = "resource-visible",
            Assertion = new AgentToolReadBackAssertionPayload
            {
                Match = AgentToolReadBackMatchPayload.Exists,
                JsonPointer = "/data",
            },
        };
        return admission;
    }

    private static AgentToolOperationAdmissionPayload ReadAdmission() => new()
    {
        ServiceInstanceId = "connected-service-alpha",
        ServiceSlug = "service-slug-alpha",
        PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
        {
            EndpointId = "endpoint-read-alpha",
        },
        AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
        HttpMethod = "GET",
        PathTemplate = "/records/{record_id}",
        ContractDigest = new string('c', 64),
        CatalogDigest = $"sha256:{new string('a', 64)}",
        ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
        {
            Risk = AgentToolOperationRiskPayload.ReadOnly,
            Approval = AgentToolOperationApprovalPayload.None,
            EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
            AllowedExecutionModes =
            {
                AgentToolOperationExecutionModePayload.Interactive,
                AgentToolOperationExecutionModePayload.Durable,
            },
        },
    };
}
