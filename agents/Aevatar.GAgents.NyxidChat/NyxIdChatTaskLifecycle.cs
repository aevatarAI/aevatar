using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

public sealed record NyxIdChatTaskLifecycleDecision(
    NyxIdChatTransitionOutcome Outcome,
    string ReasonCode,
    string SafeMessage,
    NyxIdChatConversationGAgentState State,
    NyxIdChatOperationDispatchCommand? NextCommand,
    NyxIdChatInputRequestCommand? InputRequest = null);

/// <summary>
/// Pure actor-owned derivation for one reconciled NyxIdChat operation. The
/// returned successor command is an actor-turn-local dispatch instruction; it
/// must never be copied into durable state or a committed event.
/// </summary>
public static class NyxIdChatTaskLifecycle
{
    public const string ToolSafetyRequired = "NYXID_CHAT_TOOL_SAFETY_REQUIRED";
    public const string ToolCallInvalid = "NYXID_CHAT_TOOL_CALL_INVALID";
    public const string MultipleToolCallsUnsupported =
        "NYXID_CHAT_MULTIPLE_TOOL_CALLS_UNSUPPORTED";
    public const string InputRequestInvalid = "NYXID_CHAT_INPUT_REQUEST_INVALID";

    private const string ToolSafetyRequiredMessage =
        "The authorized tool provider did not supply the required safety classification.";
    private const string ToolCallInvalidMessage =
        "The model returned an invalid typed tool call.";
    private const string MultipleToolCallsUnsupportedMessage =
        "This chat version can execute only one authorized tool call at a time.";
    private const string InputRequestInvalidMessage =
        "The assistant returned an invalid structured input request.";

    public static NyxIdChatTaskLifecycleDecision ApplyOperationResult(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(now);

        var currentStep = FindCurrentStep(state, signal.Key);
        if (currentStep is null)
            return FromTransition(
                NyxIdChatTaskTransitionPolicy.ReconcileOperation(state, signal),
                nextCommand: null);

        var operationKey = signal.Key!;

        if (signal.ResultCase == NyxIdChatOperationResultSignal.ResultOneofCase.Llm &&
            signal.Llm.ToolCalls.Count > 0)
        {
            return ApplyLlmToolPlan(state, signal, operationKey, currentStep, now);
        }

        var normalizedSignal = NormalizeUncertainToolResult(signal);
        var transition = NyxIdChatTaskTransitionPolicy.ReconcileOperation(
            state,
            normalizedSignal);
        if (transition.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return FromTransition(transition, nextCommand: null);

        var next = transition.State.Clone();
        StampReconciledState(next, operationKey, now);
        ApplyPendingApproval(next, normalizedSignal, currentStep, now);

        NyxIdChatOperationDispatchCommand? successor = null;
        if (normalizedSignal.ResultCase ==
                NyxIdChatOperationResultSignal.ResultOneofCase.Tool &&
            normalizedSignal.Tool.Receipt?.Status == AgentToolReceiptStatus.Success &&
            next.ActiveTask.Status == NyxIdChatTaskStatus.Succeeded)
        {
            successor = AppendContinuationLlmStep(next, normalizedSignal.Key, now);
        }

        FinalizeDerivedState(next, now);
        return new NyxIdChatTaskLifecycleDecision(
            transition.Outcome,
            transition.ReasonCode,
            transition.SafeMessage,
            next,
            successor);
    }

    private static NyxIdChatTaskLifecycleDecision ApplyLlmToolPlan(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        NyxIdChatOperationKey operationKey,
        NyxIdChatTaskStepState currentStep,
        Timestamp now)
    {
        if (signal.Llm.ToolCalls.Count != 1)
        {
            return FailClosed(
                state,
                operationKey,
                MultipleToolCallsUnsupported,
                MultipleToolCallsUnsupportedMessage,
                now);
        }

        var toolCall = signal.Llm.ToolCalls[0];
        if (string.IsNullOrWhiteSpace(toolCall.CallId) ||
            string.IsNullOrWhiteSpace(toolCall.ToolName))
        {
            return FailClosed(
                state,
                operationKey,
                ToolCallInvalid,
                ToolCallInvalidMessage,
                now);
        }

        if (toolCall.Safety is null)
        {
            return FailClosed(
                state,
                operationKey,
                ToolSafetyRequired,
                ToolSafetyRequiredMessage,
                now);
        }

        if (NyxIdChatAskUserContract.IsAskUser(toolCall))
            return ApplyLlmInputPlan(state, signal, operationKey, currentStep, toolCall, now);

        var transition = NyxIdChatTaskTransitionPolicy.ReconcileOperation(state, signal);
        if (transition.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return FromTransition(transition, nextCommand: null);

        var next = transition.State.Clone();
        StampReconciledState(next, operationKey, now);
        var toolStep = BuildToolStep(
            next,
            currentStep,
            toolCall,
            now);
        next.ActiveTask.Steps.Add(toolStep);
        ActivateStep(next, toolStep, now);

        var command = new NyxIdChatOperationDispatchCommand
        {
            Key = toolStep.Operation.Key.Clone(),
            Tool = new NyxIdChatToolOperationInput
            {
                CallId = toolCall.CallId,
                ToolName = toolCall.ToolName,
                ArgumentsJson = toolCall.ArgumentsJson,
                MayChangeExternalState = toolCall.Safety.MayChangeExternalState,
            },
        };
        FinalizeDerivedState(next, now);
        return new NyxIdChatTaskLifecycleDecision(
            transition.Outcome,
            transition.ReasonCode,
            transition.SafeMessage,
            next,
            command);
    }

    private static NyxIdChatTaskLifecycleDecision ApplyLlmInputPlan(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        NyxIdChatOperationKey operationKey,
        NyxIdChatTaskStepState currentStep,
        NyxIdChatToolCall toolCall,
        Timestamp now)
    {
        var requestId = BuildStableIdentity(
            "input-request",
            state.ConversationActorId,
            operationKey.TurnId,
            operationKey.TaskId,
            currentStep.StepId,
            toolCall.CallId);
        if (!NyxIdChatAskUserContract.TryParse(requestId, toolCall.ArgumentsJson, out var input))
        {
            return FailClosed(
                state,
                operationKey,
                InputRequestInvalid,
                InputRequestInvalidMessage,
                now);
        }

        var transition = NyxIdChatTaskTransitionPolicy.ReconcileOperation(state, signal);
        if (transition.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return FromTransition(transition, nextCommand: null);

        var next = transition.State.Clone();
        StampReconciledState(next, operationKey, now);
        var order = next.ActiveTask.Steps.Count + 1;
        var stepId = BuildStableIdentity(
            "step",
            next.ConversationActorId,
            operationKey.TurnId,
            operationKey.TaskId,
            currentStep.StepId,
            toolCall.CallId,
            order.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "input");
        var inputStep = new NyxIdChatTaskStepState
        {
            StepId = stepId,
            Order = order,
            Kind = NyxIdChatStepKind.Input,
            Status = NyxIdChatStepStatus.Waiting,
            Required = true,
            Description = input.Prompt,
            Source = new NyxIdChatStepSource
            {
                Input = new NyxIdChatInputStepSource { RequestId = requestId },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            UpdatedAt = now.Clone(),
        };
        inputStep.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(inputStep);
        next.ActiveTask.Steps.Add(inputStep);
        next.ActiveTask.Status = NyxIdChatTaskStatus.Active;
        next.ActiveTask.ActiveStepId = stepId;
        next.ActiveTask.ActiveOperationId = string.Empty;
        next.ActiveTask.UpdatedAt = now.Clone();
        next.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
        next.ActiveTurn.TerminalAt = null;

        var request = new NyxIdChatInputRequestCommand
        {
            ScopeId = next.ScopeId,
            ConversationActorId = next.ConversationActorId,
            TurnId = operationKey.TurnId,
            TaskId = operationKey.TaskId,
            StepId = stepId,
            RequestId = requestId,
            ToolCallId = toolCall.CallId,
            Prompt = input.Prompt,
            AllowFreeText = false,
            MultiSelect = input.MultiSelect,
        };
        request.Options.AddRange(input.Options.Select(static option => option.Clone()));
        next.PendingInputRequest = request.Clone();
        FinalizeDerivedState(next, now);
        return new NyxIdChatTaskLifecycleDecision(
            transition.Outcome,
            transition.ReasonCode,
            transition.SafeMessage,
            next,
            NextCommand: null,
            InputRequest: request);
    }

    private static NyxIdChatTaskLifecycleDecision FailClosed(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key,
        string failureCode,
        string safeMessage,
        Timestamp now)
    {
        var failure = new NyxIdChatOperationResultSignal
        {
            Key = key.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = failureCode,
                SafeMessage = safeMessage,
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            },
        };
        var transition = NyxIdChatTaskTransitionPolicy.ReconcileOperation(state, failure);
        if (transition.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return FromTransition(transition, nextCommand: null);

        var next = transition.State.Clone();
        StampReconciledState(next, key, now);
        FinalizeDerivedState(next, now);
        return new NyxIdChatTaskLifecycleDecision(
            transition.Outcome,
            failureCode,
            safeMessage,
            next,
            NextCommand: null);
    }

    private static NyxIdChatOperationResultSignal NormalizeUncertainToolResult(
        NyxIdChatOperationResultSignal signal)
    {
        if (signal.ResultCase != NyxIdChatOperationResultSignal.ResultOneofCase.Tool ||
            signal.Tool.ExternalEffect != NyxIdChatEffectEvidence.MayHaveChanged)
        {
            return signal;
        }

        var receipt = signal.Tool.Receipt;
        return new NyxIdChatOperationResultSignal
        {
            Key = signal.Key?.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = string.IsNullOrWhiteSpace(receipt?.ErrorCode)
                    ? "NYXID_CHAT_TOOL_OUTCOME_UNCERTAIN"
                    : receipt.ErrorCode,
                SafeMessage = string.IsNullOrWhiteSpace(receipt?.ErrorMessage)
                    ? "The external tool outcome could not be confirmed."
                    : receipt.ErrorMessage,
                ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
            },
        };
    }

    private static NyxIdChatTaskStepState BuildToolStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState sourceStep,
        NyxIdChatToolCall call,
        Timestamp now)
    {
        var order = state.ActiveTask.Steps.Count + 1;
        var stepId = BuildStableIdentity(
            "step",
            state.ConversationActorId,
            state.ActiveTurn.TurnId,
            state.ActiveTask.TaskId,
            sourceStep.StepId,
            call.CallId,
            order.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "tool");
        var operationId = BuildStableIdentity(
            "operation",
            state.ConversationActorId,
            state.ActiveTurn.TurnId,
            state.ActiveTask.TaskId,
            stepId,
            "1");
        var operationKey = new NyxIdChatOperationKey
        {
            ConversationActorId = state.ConversationActorId,
            TurnId = state.ActiveTurn.TurnId,
            TaskId = state.ActiveTask.TaskId,
            StepId = stepId,
            OperationId = operationId,
            OperationGeneration = 1,
        };
        var step = new NyxIdChatTaskStepState
        {
            StepId = stepId,
            Order = order,
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Description = $"Run authorized tool {call.ToolName}.",
            Source = new NyxIdChatStepSource
            {
                Tool = new NyxIdChatToolStepSource { ToolName = call.ToolName },
            },
            MayChangeExternalState = call.Safety.MayChangeExternalState,
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Operation = new NyxIdChatOperationState
            {
                Key = operationKey,
                Kind = NyxIdChatStepKind.Tool,
                Phase = NyxIdChatOperationPhase.Requested,
                MayChangeExternalState = call.Safety.MayChangeExternalState,
                RequestedAt = now.Clone(),
            },
            UpdatedAt = now.Clone(),
        };
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        return step;
    }

    private static NyxIdChatOperationDispatchCommand AppendContinuationLlmStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey completedToolKey,
        Timestamp now)
    {
        var order = state.ActiveTask.Steps.Count + 1;
        var stepId = BuildStableIdentity(
            "step",
            state.ConversationActorId,
            completedToolKey.TurnId,
            completedToolKey.TaskId,
            completedToolKey.StepId,
            order.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "llm-continuation");
        var operationId = BuildStableIdentity(
            "operation",
            state.ConversationActorId,
            completedToolKey.TurnId,
            completedToolKey.TaskId,
            stepId,
            "1");
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = state.ConversationActorId,
            TurnId = completedToolKey.TurnId,
            TaskId = completedToolKey.TaskId,
            StepId = stepId,
            OperationId = operationId,
            OperationGeneration = 1,
        };
        var step = new NyxIdChatTaskStepState
        {
            StepId = stepId,
            Order = order,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Description = "Continue the assistant response with the typed tool result.",
            Source = new NyxIdChatStepSource { Llm = new NyxIdChatLLMStepSource() },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Operation = new NyxIdChatOperationState
            {
                Key = key.Clone(),
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Requested,
                RequestedAt = now.Clone(),
            },
            UpdatedAt = now.Clone(),
        };
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        state.ActiveTask.Steps.Add(step);
        ActivateStep(state, step, now);
        return new NyxIdChatOperationDispatchCommand
        {
            Key = key,
            Llm = new NyxIdChatLLMOperationInput { ContinueSession = true },
        };
    }

    private static void ApplyPendingApproval(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        NyxIdChatTaskStepState previousStep,
        Timestamp now)
    {
        var receipt = signal.Tool?.Receipt;
        if (receipt?.Status != AgentToolReceiptStatus.ApprovalRequired ||
            string.IsNullOrWhiteSpace(receipt.ApprovalRequestId))
        {
            if (state.ActiveTask.Status != NyxIdChatTaskStatus.Active)
                state.PendingApproval = null;
            return;
        }

        var step = FindCurrentStep(state, signal.Key);
        if (step is null)
            return;

        step.ApprovalRequestId = receipt.ApprovalRequestId;
        step.UpdatedAt = now.Clone();
        state.PendingApproval = new NyxIdChatPendingApprovalState
        {
            ApprovalRequestId = receipt.ApprovalRequestId,
            TurnId = signal.Key.TurnId,
            TaskId = signal.Key.TaskId,
            StepId = signal.Key.StepId,
            ToolCallId = receipt.CallId,
            ToolName = string.IsNullOrWhiteSpace(receipt.ToolName)
                ? previousStep.Source?.Tool?.ToolName ?? string.Empty
                : receipt.ToolName,
            AskedAt = now.Clone(),
            Presentation = new NyxIdChatApprovalPresentation
            {
                Action = string.IsNullOrWhiteSpace(receipt.SideEffectKind)
                    ? receipt.ToolName ?? string.Empty
                    : receipt.SideEffectKind,
                Target = ResolveApprovalTarget(receipt),
                ActorLabel = NyxIdChatServiceDefaults.DisplayName,
                Reversibility = receipt.IsDestructive
                    ? NyxIdChatApprovalReversibility.Irreversible
                    : NyxIdChatApprovalReversibility.Unknown,
                GrantBoundary = "within_grant",
            },
        };
    }

    private static string ResolveApprovalTarget(AgentToolReceipt receipt)
    {
        var kind = receipt.SubjectKind?.Trim() ?? string.Empty;
        var id = receipt.SubjectId?.Trim() ?? string.Empty;
        return (kind, id) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{kind}:{id}",
            (_, { Length: > 0 }) => id,
            ({ Length: > 0 }, _) => kind,
            _ => receipt.ToolName?.Trim() ?? string.Empty,
        };
    }

    private static void ActivateStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState step,
        Timestamp now)
    {
        state.ActiveTask.Status = NyxIdChatTaskStatus.Active;
        state.ActiveTask.FailureCode = string.Empty;
        state.ActiveTask.SafeMessage = string.Empty;
        state.ActiveTask.ActiveStepId = step.StepId;
        state.ActiveTask.ActiveOperationId = step.Operation.Key.OperationId;
        state.ActiveTask.UpdatedAt = now.Clone();
        state.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
        state.ActiveTurn.FailureCode = string.Empty;
        state.ActiveTurn.SafeMessage = string.Empty;
        state.ActiveTurn.TerminalAt = null;
    }

    private static void StampReconciledState(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key,
        Timestamp now)
    {
        var step = FindCurrentStep(state, key);
        if (step?.Operation is not null)
        {
            step.Operation.CompletedAt = now.Clone();
            step.UpdatedAt = now.Clone();
            step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        }
        state.ActiveTask.UpdatedAt = now.Clone();
    }

    private static void FinalizeDerivedState(
        NyxIdChatConversationGAgentState state,
        Timestamp now)
    {
        if (state.ActiveTurn.Status != NyxIdChatTurnStatus.Active)
        {
            state.ActiveTurn.TerminalAt = now.Clone();
            AddTerminalSummary(state, state.ActiveTurn);
        }

        state.LatestTurn = state.ActiveTurn.Clone();
        state.UpdatedAt = now.Clone();
    }

    private static void AddTerminalSummary(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTurnState turn)
    {
        const int historyLimit = 32;
        var existing = state.RecentTerminalTurns.FirstOrDefault(summary =>
            string.Equals(summary.TurnId, turn.TurnId, StringComparison.Ordinal));
        if (existing is null)
        {
            state.RecentTerminalTurns.Add(new NyxIdChatTurnSummary
            {
                TurnId = turn.TurnId,
                TaskId = turn.TaskId,
                Status = turn.Status,
                FailureCode = turn.FailureCode,
                SafeMessage = turn.SafeMessage,
                TerminalAt = turn.TerminalAt?.Clone(),
            });
        }
        else
        {
            existing.TaskId = turn.TaskId;
            existing.Status = turn.Status;
            existing.FailureCode = turn.FailureCode;
            existing.SafeMessage = turn.SafeMessage;
            existing.TerminalAt = turn.TerminalAt?.Clone();
        }

        while (state.RecentTerminalTurns.Count > historyLimit)
            state.RecentTerminalTurns.RemoveAt(0);
    }

    private static NyxIdChatTaskStepState? FindCurrentStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey? key)
    {
        if (key is null || state.ActiveTask is null)
            return null;
        return state.ActiveTask.Steps.FirstOrDefault(step =>
            KeysEqual(step.Operation?.Key, key));
    }

    private static bool KeysEqual(NyxIdChatOperationKey? left, NyxIdChatOperationKey? right) =>
        left is not null &&
        right is not null &&
        string.Equals(left.ConversationActorId, right.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) &&
        string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) &&
        left.OperationGeneration == right.OperationGeneration;

    private static string BuildStableIdentity(string prefix, params string[] parts)
    {
        var identity = string.Concat(parts.Select(static part => $"{part.Length}:{part}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"{prefix}-{Convert.ToHexStringLower(hash)[..32]}";
    }

    private static NyxIdChatTaskLifecycleDecision FromTransition(
        NyxIdChatTransitionDecision decision,
        NyxIdChatOperationDispatchCommand? nextCommand) =>
        new(
            decision.Outcome,
            decision.ReasonCode,
            decision.SafeMessage,
            decision.State,
            nextCommand);
}
