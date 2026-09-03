using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

public sealed record NyxIdChatNeedsYouDecision<TResolution>(
    bool ShouldCommit,
    bool IsExactReplay,
    NyxIdChatConversationGAgentState State,
    TResolution? Resolution,
    NyxIdChatOperationDispatchCommand? NextCommand = null)
    where TResolution : class, IMessage<TResolution>;

public static class NyxIdChatNeedsYouDecisions
{
    private const int ResolutionHistoryLimit = 32;
    private const string ApprovalExpiryClientRequestId = "nyxid-chat-approval-expiry";

    public static NyxIdChatNeedsYouDecision<NyxIdChatPendingInputState> RequestInput(
        NyxIdChatConversationGAgentState state,
        NyxIdChatInputRequestCommand command,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(now);

        var normalizedOptions = command.Options.Select(NormalizeOption).ToArray();
        var numericThreshold = command.NumericThreshold?.Clone();
        if (!MatchesConversation(state, command.ScopeId, command.ConversationActorId) ||
            !MatchesActiveTask(state, command.TurnId, command.TaskId, command.StepId) ||
            string.IsNullOrWhiteSpace(command.RequestId) ||
            string.IsNullOrWhiteSpace(command.ToolCallId) ||
            string.IsNullOrWhiteSpace(command.Prompt) ||
            (!command.AllowFreeText && normalizedOptions.Length == 0) ||
            normalizedOptions.Any(static option =>
                string.IsNullOrWhiteSpace(option.OptionId) ||
                string.IsNullOrWhiteSpace(option.Label)) ||
            normalizedOptions.Select(static option => option.OptionId)
                .Distinct(StringComparer.Ordinal).Count() != normalizedOptions.Length ||
            !IsValidNumericThreshold(numericThreshold, command.AllowFreeText,
                command.MultiSelect, normalizedOptions.Length))
        {
            return NoCommit<NyxIdChatPendingInputState>(state);
        }

        var pending = new NyxIdChatPendingInputState
        {
            RequestId = command.RequestId.Trim(),
            TurnId = command.TurnId.Trim(),
            TaskId = command.TaskId.Trim(),
            StepId = command.StepId.Trim(),
            Prompt = command.Prompt.Trim(),
            AskedAt = now.Clone(),
            AllowFreeText = command.AllowFreeText,
            MultiSelect = command.MultiSelect,
            ToolCallId = command.ToolCallId.Trim(),
        };
        if (numericThreshold is not null)
            pending.NumericThreshold = numericThreshold;
        pending.Options.AddRange(normalizedOptions);

        if (state.RecentInputResolutions.Any(result =>
                string.Equals(result.RequestId, pending.RequestId, StringComparison.Ordinal)))
        {
            return NoCommit<NyxIdChatPendingInputState>(state);
        }

        if (state.PendingInput is not null)
        {
            var replay = state.PendingInput.Clone();
            replay.AskedAt = pending.AskedAt;
            return replay.Equals(pending)
                ? new(false, true, state.Clone(), state.PendingInput.Clone())
                : NoCommit<NyxIdChatPendingInputState>(state);
        }

        if (state.PendingApproval is not null)
            return NoCommit<NyxIdChatPendingInputState>(state);

        var next = state.Clone();
        next.PendingInput = pending.Clone();
        next.PendingInputRequest = null;
        next.ProgressSequence = checked(Math.Max(0, next.ProgressSequence) + 1);
        next.UpdatedAt = now.Clone();
        return new(true, false, next, pending);
    }

    public static NyxIdChatNeedsYouDecision<NyxIdChatInputResolutionState> ResolveInput(
        NyxIdChatConversationGAgentState state,
        NyxIdChatInputResolveCommand command,
        long currentStateVersion,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(now);

        var answerHash = HashAnswer(command.Answer);
        var replay = state.RecentInputResolutions.FirstOrDefault(result =>
            string.Equals(result.RequestId, command.RequestId?.Trim(), StringComparison.Ordinal));
        if (replay is not null)
        {
            var exact = MatchesConversation(
                            state,
                            command.ScopeId,
                            command.ConversationActorId) &&
                        string.Equals(
                            replay.ClientRequestId,
                            command.ClientRequestId?.Trim(),
                            StringComparison.Ordinal) &&
                        replay.AnswerSha256.Equals(answerHash);
            return new(false, exact, state.Clone(), exact ? replay.Clone() : null);
        }

        if (command.ExpectedStateVersion != currentStateVersion ||
            !MatchesConversation(state, command.ScopeId, command.ConversationActorId) ||
            string.IsNullOrWhiteSpace(command.ClientRequestId) ||
            state.PendingInput is not { } pending ||
            !string.Equals(pending.RequestId, command.RequestId?.Trim(), StringComparison.Ordinal) ||
            !TryNormalizeAnswer(pending, command.Answer, out var normalizedAnswer) ||
            !TryResolveNumericThreshold(pending, normalizedAnswer, out var numericResolution))
        {
            return NoCommit<NyxIdChatInputResolutionState>(state);
        }

        var resolution = new NyxIdChatInputResolutionState
        {
            RequestId = pending.RequestId,
            ClientRequestId = command.ClientRequestId.Trim(),
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
            AnswerSha256 = answerHash,
            Answer = normalizedAnswer.Clone(),
            CommittedAt = now.Clone(),
        };
        if (numericResolution is not null)
            resolution.NumericThreshold = numericResolution;
        var next = state.Clone();
        next.PendingInput = null;
        next.LatestInputResolution = resolution.Clone();
        AppendBounded(next.RecentInputResolutions, resolution);
        next.ProgressSequence = checked(Math.Max(0, next.ProgressSequence) + 1);
        next.UpdatedAt = now.Clone();
        var nextCommand = ResumeAfterInput(next, pending, command, normalizedAnswer, now);
        if (nextCommand is null)
            return NoCommit<NyxIdChatInputResolutionState>(state);
        return new(true, false, next, resolution, nextCommand);
    }

    public static NyxIdChatNeedsYouDecision<NyxIdChatApprovalResolutionState> ResolveApproval(
        NyxIdChatConversationGAgentState state,
        NyxIdChatApprovalResolveCommand command,
        long currentStateVersion,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(now);

        var decisionHash = Hash($"{command.Approved}:{command.Reason?.Trim() ?? string.Empty}");
        var replay = state.RecentApprovalResolutions.FirstOrDefault(result =>
            string.Equals(result.RequestId, command.RequestId?.Trim(), StringComparison.Ordinal));
        if (replay is not null)
        {
            var exact = MatchesConversation(
                            state,
                            command.ScopeId,
                            command.ConversationActorId) &&
                        string.Equals(
                            replay.ClientRequestId,
                            command.ClientRequestId?.Trim(),
                            StringComparison.Ordinal) &&
                        replay.Approved == command.Approved &&
                        replay.DecisionSha256.Equals(decisionHash);
            return new(false, exact, state.Clone(), exact ? replay.Clone() : null);
        }

        if (command.ExpectedStateVersion != currentStateVersion ||
            !MatchesConversation(state, command.ScopeId, command.ConversationActorId) ||
            string.IsNullOrWhiteSpace(command.ClientRequestId) ||
            state.PendingApproval is not { } pending ||
            !string.Equals(pending.ApprovalRequestId, command.RequestId?.Trim(), StringComparison.Ordinal) ||
            !MatchesWaitingStep(state, pending.TurnId, pending.TaskId, pending.StepId))
        {
            return NoCommit<NyxIdChatApprovalResolutionState>(state);
        }

        if (HasApprovalExpired(pending, now))
            return CommitExpiredApproval(state, pending, now);

        var resolution = new NyxIdChatApprovalResolutionState
        {
            RequestId = pending.ApprovalRequestId,
            ClientRequestId = command.ClientRequestId.Trim(),
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
            Approved = command.Approved,
            DecisionSha256 = decisionHash,
            CommittedAt = now.Clone(),
        };
        var next = state.Clone();
        next.PendingApproval = null;
        next.LatestApprovalResolution = resolution.Clone();
        AppendBounded(next.RecentApprovalResolutions, resolution);
        next.ProgressSequence = checked(Math.Max(0, next.ProgressSequence) + 1);
        next.UpdatedAt = now.Clone();
        var nextCommand = ResumeAfterApproval(next, pending, command, now);
        if (nextCommand is null)
            return NoCommit<NyxIdChatApprovalResolutionState>(state);
        return new(true, false, next, resolution, nextCommand);
    }

    public static NyxIdChatNeedsYouDecision<NyxIdChatApprovalResolutionState> ExpireApproval(
        NyxIdChatConversationGAgentState state,
        NyxIdChatToolApprovalExpiredSignal signal,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(now);

        if (state.PendingApproval is not { } pending ||
            !string.Equals(
                pending.ApprovalRequestId,
                signal.ApprovalRequestId,
                StringComparison.Ordinal) ||
            pending.ExpiresAt is null ||
            signal.ExpectedExpiresAt is null ||
            !pending.ExpiresAt.Equals(signal.ExpectedExpiresAt) ||
            !HasApprovalExpired(pending, now))
        {
            return NoCommit<NyxIdChatApprovalResolutionState>(state);
        }

        return CommitExpiredApproval(state, pending, now);
    }

    private static bool HasApprovalExpired(
        NyxIdChatPendingApprovalState pending,
        Timestamp now) =>
        pending.ExpiresAt is not null &&
        now.ToDateTimeOffset() >= pending.ExpiresAt.ToDateTimeOffset();

    // Expiry always fails closed: the committed resolution is a system-authored
    // denial regardless of any late caller decision, and it never carries an
    // approval continuation, so no effect can dispatch.
    private static NyxIdChatNeedsYouDecision<NyxIdChatApprovalResolutionState> CommitExpiredApproval(
        NyxIdChatConversationGAgentState state,
        NyxIdChatPendingApprovalState pending,
        Timestamp now)
    {
        var resolution = new NyxIdChatApprovalResolutionState
        {
            RequestId = pending.ApprovalRequestId,
            ClientRequestId = ApprovalExpiryClientRequestId,
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Expired,
            Approved = false,
            DecisionSha256 = Hash(ApprovalExpiryClientRequestId),
            CommittedAt = now.Clone(),
        };
        var next = state.Clone();
        next.LatestApprovalResolution = resolution.Clone();
        AppendBounded(next.RecentApprovalResolutions, resolution);
        next.ProgressSequence = checked(Math.Max(0, next.ProgressSequence) + 1);
        next.UpdatedAt = now.Clone();
        NyxIdChatTaskLifecycle.ExpirePendingApproval(next, pending, now);
        return new(true, false, next, resolution);
    }

    public static NyxIdChatConversationGAgentState RefreshAttention(
        NyxIdChatConversationGAgentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var next = state.Clone();
        var attention = new NyxIdChatConversationAttentionState
        {
            TaskStatus = next.ActiveTask?.Status ?? NyxIdChatTaskStatus.Unspecified,
            AttentionKind = NyxIdChatAttentionKind.None,
            ActiveStepSummary = ResolveActiveStepSummary(next.ActiveTask),
        };

        if (next.PendingInput is { } input)
        {
            attention.AttentionKind = NyxIdChatAttentionKind.Input;
            attention.AttentionSince = input.AskedAt?.Clone();
        }
        else if (next.PendingApproval is { } approval)
        {
            attention.AttentionKind = NyxIdChatAttentionKind.Approval;
            attention.AttentionSince = approval.AskedAt?.Clone();
        }
        else if (next.PendingWorkflowSignal is { } workflowSignal)
        {
            attention.TaskStatus = NyxIdChatTaskStatus.Active;
            attention.AttentionKind = NyxIdChatAttentionKind.WorkflowSignal;
            attention.AttentionSince = workflowSignal.ObservedAt?.Clone();
            attention.ActiveStepSummary = ResolveWorkflowSignalSummary(workflowSignal);
        }
        else if (ResolveStalledStep(next.ActiveTask) is { } stalledStep)
        {
            attention.AttentionKind = NyxIdChatAttentionKind.Stalled;
            attention.AttentionSince = stalledStep.Operation.StalledAt?.Clone();
        }

        next.Attention = attention;
        return next;
    }

    private static NyxIdChatTaskStepState? ResolveStalledStep(NyxIdChatTaskState? task) =>
        task?.Status == NyxIdChatTaskStatus.Active
            ? task.Steps.FirstOrDefault(static step =>
                step.Status == NyxIdChatStepStatus.Running &&
                step.Operation?.StalledAt is not null)
            : null;

    private static bool MatchesConversation(
        NyxIdChatConversationGAgentState state,
        string? scopeId,
        string? actorId) =>
        !string.IsNullOrWhiteSpace(scopeId) &&
        !string.IsNullOrWhiteSpace(actorId) &&
        string.Equals(state.ScopeId, scopeId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ConversationActorId, actorId.Trim(), StringComparison.Ordinal);

    private static bool MatchesActiveTask(
        NyxIdChatConversationGAgentState state,
        string? turnId,
        string? taskId,
        string? stepId) =>
        state.ActiveTask is { } task &&
        state.ActiveTurn is { } turn &&
        task.Status == NyxIdChatTaskStatus.Active &&
        turn.Status == NyxIdChatTurnStatus.Active &&
        string.Equals(turn.TurnId, turnId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(task.TaskId, taskId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(task.ActiveStepId, stepId?.Trim(), StringComparison.Ordinal) &&
        task.Steps.Any(step =>
            string.Equals(step.StepId, stepId?.Trim(), StringComparison.Ordinal) &&
            step.Status is NyxIdChatStepStatus.Planned or
                NyxIdChatStepStatus.Waiting or
                NyxIdChatStepStatus.Running);

    private static NyxIdChatInputOption NormalizeOption(NyxIdChatInputOption option) =>
        new()
        {
            OptionId = option?.OptionId?.Trim() ?? string.Empty,
            Label = option?.Label?.Trim() ?? string.Empty,
            Description = option?.Description?.Trim() ?? string.Empty,
        };

    private static bool TryNormalizeAnswer(
        NyxIdChatPendingInputState pending,
        NyxIdChatInputAnswer? answer,
        out NyxIdChatInputAnswer normalized)
    {
        normalized = null!;
        if (answer is null)
            return false;

        if (answer.AnswerCase == NyxIdChatInputAnswer.AnswerOneofCase.FreeText)
        {
            if (!pending.AllowFreeText || string.IsNullOrWhiteSpace(answer.FreeText))
                return false;
            normalized = new NyxIdChatInputAnswer { FreeText = answer.FreeText.Trim() };
            return true;
        }

        if (answer.AnswerCase != NyxIdChatInputAnswer.AnswerOneofCase.Selection ||
            answer.Selection is null)
        {
            return false;
        }

        var selected = answer.Selection.OptionIds
            .Select(static value => value?.Trim() ?? string.Empty)
            .ToArray();
        if (selected.Length == 0 ||
            (!pending.MultiSelect && selected.Length != 1) ||
            selected.Any(string.IsNullOrWhiteSpace) ||
            selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
        {
            return false;
        }

        var allowed = pending.Options
            .Select(static option => option.OptionId)
            .ToHashSet(StringComparer.Ordinal);
        if (selected.Any(optionId => !allowed.Contains(optionId)))
            return false;

        normalized = new NyxIdChatInputAnswer
        {
            Selection = new NyxIdChatInputSelectionAnswer(),
        };
        normalized.Selection.OptionIds.AddRange(selected);
        return true;
    }

    private static bool IsValidNumericThreshold(
        NyxIdChatNumericThresholdInputSpec? spec,
        bool allowFreeText,
        bool multiSelect,
        int optionCount) =>
        spec is null ||
        allowFreeText &&
        !multiSelect &&
        optionCount == 0 &&
        spec.MinimumValue <= spec.MaximumValue &&
        spec.SuggestedValue >= spec.MinimumValue &&
        spec.SuggestedValue <= spec.MaximumValue;

    private static bool TryResolveNumericThreshold(
        NyxIdChatPendingInputState pending,
        NyxIdChatInputAnswer answer,
        out NyxIdChatNumericThresholdResolution? resolution)
    {
        resolution = null;
        if (pending.NumericThreshold is null)
            return true;
        if (answer.AnswerCase != NyxIdChatInputAnswer.AnswerOneofCase.FreeText ||
            !long.TryParse(
                answer.FreeText,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var effective) ||
            effective < pending.NumericThreshold.MinimumValue ||
            effective > pending.NumericThreshold.MaximumValue)
        {
            return false;
        }

        resolution = new NyxIdChatNumericThresholdResolution
        {
            SuggestedValue = pending.NumericThreshold.SuggestedValue,
            EffectiveValue = effective,
            Origin = effective == pending.NumericThreshold.SuggestedValue
                ? NyxIdChatThresholdOrigin.Suggested
                : NyxIdChatThresholdOrigin.UserOverride,
        };
        return true;
    }

    private static NyxIdChatOperationDispatchCommand? ResumeAfterInput(
        NyxIdChatConversationGAgentState state,
        NyxIdChatPendingInputState pending,
        NyxIdChatInputResolveCommand command,
        NyxIdChatInputAnswer answer,
        Timestamp now)
    {
        var activeTask = state.ActiveTask;
        var inputStep = activeTask?.Steps.FirstOrDefault(step =>
            string.Equals(step.StepId, pending.StepId, StringComparison.Ordinal) &&
            step.Kind == NyxIdChatStepKind.Input &&
            step.Status == NyxIdChatStepStatus.Waiting &&
            string.Equals(step.Source?.Input?.RequestId, pending.RequestId, StringComparison.Ordinal));
        if (activeTask is null || inputStep is null || string.IsNullOrWhiteSpace(pending.ToolCallId))
            return null;

        inputStep.Status = NyxIdChatStepStatus.Done;
        inputStep.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
        inputStep.UpdatedAt = now.Clone();
        inputStep.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(inputStep);

        var order = activeTask.Steps.Count + 1;
        var stepId = BuildStableIdentity(
            "step",
            state.ConversationActorId,
            pending.TurnId,
            pending.TaskId,
            pending.StepId,
            pending.RequestId,
            order.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "input-continuation");
        var operationKey = BuildOperationKey(state, pending.TurnId, pending.TaskId, stepId, 1);
        var continuationStep = BuildRunningStep(
            stepId,
            order,
            NyxIdChatStepKind.Llm,
            "Continue the assistant response with the selected user input.",
            new NyxIdChatStepSource { Llm = new NyxIdChatLLMStepSource() },
            operationKey,
            mayChangeExternalState: false,
            now);
        continuationStep.AddedBy = NyxIdChatStepAddedBy.Replan;
        continuationStep.DependsOn.Add(inputStep.StepId);
        activeTask.Steps.Add(continuationStep);
        NyxIdChatPlanRevisions.CommitChange(
            activeTask,
            NyxIdChatPlanRevisionCause.ScopeResolution,
            now,
            [continuationStep]);
        ActivateStep(state, continuationStep, now);
        var continuation = new NyxIdChatInputContinuationInput
        {
            RequestId = pending.RequestId,
            ToolCallId = pending.ToolCallId,
            Answer = answer.Clone(),
            ToolContext = command.ToolContext?.Clone(),
            TargetRef = command.TargetRef?.Clone(),
        };
        if (answer.AnswerCase == NyxIdChatInputAnswer.AnswerOneofCase.Selection)
        {
            var selectedIds = answer.Selection.OptionIds.ToHashSet(StringComparer.Ordinal);
            continuation.SelectedOptions.AddRange(pending.Options
                .Where(option => selectedIds.Contains(option.OptionId))
                .Select(static option => option.Clone()));
        }
        return new NyxIdChatOperationDispatchCommand
        {
            Key = operationKey,
            InputContinuation = continuation,
        };
    }

    private static NyxIdChatOperationDispatchCommand? ResumeAfterApproval(
        NyxIdChatConversationGAgentState state,
        NyxIdChatPendingApprovalState pending,
        NyxIdChatApprovalResolveCommand command,
        Timestamp now)
    {
        var step = state.ActiveTask?.Steps.FirstOrDefault(candidate =>
            string.Equals(candidate.StepId, pending.StepId, StringComparison.Ordinal) &&
            candidate.Kind == NyxIdChatStepKind.Tool &&
            candidate.Status == NyxIdChatStepStatus.Waiting &&
            string.Equals(candidate.ApprovalRequestId, pending.ApprovalRequestId, StringComparison.Ordinal));
        if (step?.Operation?.Key is null || string.IsNullOrWhiteSpace(pending.ToolCallId))
            return null;

        var generation = checked(step.Operation.Key.OperationGeneration + 1);
        var key = BuildOperationKey(state, pending.TurnId, pending.TaskId, pending.StepId, generation);
        step.Status = NyxIdChatStepStatus.Running;
        step.FailureCode = string.Empty;
        step.SafeMessage = string.Empty;
        step.Operation = new NyxIdChatOperationState
        {
            Key = key.Clone(),
            Kind = NyxIdChatStepKind.Tool,
            Phase = NyxIdChatOperationPhase.Requested,
            MayChangeExternalState = step.MayChangeExternalState,
            Idempotent = !step.MayChangeExternalState,
            IdempotencyKey = key.OperationId,
            RequestedAt = now.Clone(),
        };
        step.UpdatedAt = now.Clone();
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        ActivateStep(state, step, now);
        return new NyxIdChatOperationDispatchCommand
        {
            Key = key,
            ToolApprovalContinuation = new NyxIdChatToolApprovalContinuationInput
            {
                ApprovalRequestId = pending.ApprovalRequestId,
                Approved = command.Approved,
                ToolContext = command.ToolContext?.Clone(),
                MayChangeExternalState = step.MayChangeExternalState,
                IdempotencyKey = key.OperationId,
                OperationAdmission = step.Source?.Tool?.OperationAdmission?.Clone(),
                ExactServiceApproval = pending.ExactServiceApproval?.Clone(),
                ToolCallId = pending.ToolCallId,
                ToolName = pending.ToolName,
                Presentation = NyxIdChatDurableToolPresentation.Snapshot(
                    step.Source?.Tool?.Presentation,
                    pending.ToolName),
            },
        };
    }

    private static bool MatchesWaitingStep(
        NyxIdChatConversationGAgentState state,
        string turnId,
        string taskId,
        string stepId) =>
        state.ActiveTask is { Status: NyxIdChatTaskStatus.Active } task &&
        state.ActiveTurn is { Status: NyxIdChatTurnStatus.Active } turn &&
        string.Equals(turn.TurnId, turnId, StringComparison.Ordinal) &&
        string.Equals(task.TaskId, taskId, StringComparison.Ordinal) &&
        string.Equals(task.ActiveStepId, stepId, StringComparison.Ordinal) &&
        task.Steps.Any(step =>
            string.Equals(step.StepId, stepId, StringComparison.Ordinal) &&
            step.Status == NyxIdChatStepStatus.Waiting);

    private static NyxIdChatOperationKey BuildOperationKey(
        NyxIdChatConversationGAgentState state,
        string turnId,
        string taskId,
        string stepId,
        long generation) =>
        new()
        {
            ConversationActorId = state.ConversationActorId,
            TurnId = turnId,
            TaskId = taskId,
            StepId = stepId,
            OperationId = BuildStableIdentity(
                "operation",
                state.ConversationActorId,
                turnId,
                taskId,
                stepId,
                generation.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            OperationGeneration = generation,
        };

    private static NyxIdChatTaskStepState BuildRunningStep(
        string stepId,
        int order,
        NyxIdChatStepKind kind,
        string description,
        NyxIdChatStepSource source,
        NyxIdChatOperationKey key,
        bool mayChangeExternalState,
        Timestamp now)
    {
        var step = new NyxIdChatTaskStepState
        {
            StepId = stepId,
            Order = order,
            Kind = kind,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Description = description,
            Source = source,
            MayChangeExternalState = mayChangeExternalState,
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Operation = new NyxIdChatOperationState
            {
                Key = key.Clone(),
                Kind = kind,
                Phase = NyxIdChatOperationPhase.Requested,
                MayChangeExternalState = mayChangeExternalState,
                RequestedAt = now.Clone(),
            },
            UpdatedAt = now.Clone(),
        };
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        return step;
    }

    private static void ActivateStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState step,
        Timestamp now)
    {
        state.ActiveTask.Status = NyxIdChatTaskStatus.Active;
        state.ActiveTask.ActiveStepId = step.StepId;
        state.ActiveTask.ActiveOperationId = step.Operation?.Key?.OperationId ?? string.Empty;
        state.ActiveTask.FailureCode = string.Empty;
        state.ActiveTask.SafeMessage = string.Empty;
        state.ActiveTask.UpdatedAt = now.Clone();
        state.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
        state.ActiveTurn.FailureCode = string.Empty;
        state.ActiveTurn.SafeMessage = string.Empty;
        state.ActiveTurn.TerminalAt = null;
        state.LatestTurn = state.ActiveTurn.Clone();
    }

    private static string ResolveActiveStepSummary(NyxIdChatTaskState? task)
    {
        if (task is null)
            return string.Empty;
        var step = task.Steps.FirstOrDefault(candidate =>
            string.Equals(candidate.StepId, task.ActiveStepId, StringComparison.Ordinal));
        return step?.Description?.Trim() ?? string.Empty;
    }

    private static string ResolveWorkflowSignalSummary(NyxIdChatPendingWorkflowSignalState signal) =>
        !string.IsNullOrWhiteSpace(signal.Prompt)
            ? signal.Prompt.Trim()
            : signal.SignalName;

    private static ByteString Hash(string value) =>
        ByteString.CopyFrom(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ByteString HashAnswer(NyxIdChatInputAnswer? answer)
    {
        if (answer is null)
            return Hash(string.Empty);
        return answer.AnswerCase switch
        {
            NyxIdChatInputAnswer.AnswerOneofCase.FreeText =>
                Hash($"free_text:{answer.FreeText?.Trim() ?? string.Empty}"),
            NyxIdChatInputAnswer.AnswerOneofCase.Selection =>
                Hash(string.Join("\n", answer.Selection?.OptionIds.Select(static value =>
                    $"option:{value?.Trim() ?? string.Empty}") ?? [])),
            _ => Hash(string.Empty),
        };
    }

    private static string BuildStableIdentity(string prefix, params string[] parts)
    {
        var identity = string.Concat(parts.Select(static part => $"{part.Length}:{part}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"{prefix}-{Convert.ToHexStringLower(hash)[..32]}";
    }

    private static void AppendBounded<T>(Google.Protobuf.Collections.RepeatedField<T> items, T item)
        where T : class, IMessage<T>
    {
        items.Add(item);
        while (items.Count > ResolutionHistoryLimit)
            items.RemoveAt(0);
    }

    private static NyxIdChatNeedsYouDecision<T> NoCommit<T>(
        NyxIdChatConversationGAgentState state)
        where T : class, IMessage<T> =>
        new(false, false, state.Clone(), null);
}
