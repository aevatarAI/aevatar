using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions.ToolProviders;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

public sealed record NyxIdChatPlanResolutionDecision(
    bool ShouldCommit,
    bool IsExactReplay,
    NyxIdChatConversationGAgentState State,
    NyxIdChatPlanResolutionState? Resolution,
    NyxIdChatOperationDispatchCommand? NextCommand = null);

public sealed record NyxIdChatPlanGateExpirationDecision(
    bool ShouldCommit,
    NyxIdChatConversationGAgentState State);

public static class NyxIdChatPlanGateDecisions
{
    private const int ResolutionHistoryLimit = 32;

    public static bool RequiresConfirmation(
        NyxIdChatToolCall call,
        IEnumerable<NyxIdChatTaskStepState> plannedSteps,
        int thresholdSeconds)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(plannedSteps);
        if (thresholdSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(thresholdSeconds));
        if (call.Safety is null)
            return true;

        return call.OperationAdmission?.ExecutionPolicy is
                   {
                       Risk: AgentToolOperationRiskPayload.Write,
                       Approval: AgentToolOperationApprovalPayload.Required,
                   } ||
               call.Safety.RequiresApproval ||
               call.Safety.IsDestructive ||
               ExceedsEstimatedDurationThreshold(plannedSteps, thresholdSeconds);
    }

    public static NyxIdChatPlanGate BuildToolGate(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskStepState step,
        NyxIdChatToolCall call,
        bool requiresConfirmation)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(call);

        var gate = new NyxIdChatPlanGate
        {
            Mode = requiresConfirmation
                ? NyxIdChatPlanGateMode.Confirm
                : NyxIdChatPlanGateMode.Auto,
            Status = requiresConfirmation
                ? NyxIdChatPlanGateStatus.Pending
                : NyxIdChatPlanGateStatus.Satisfied,
            Reason = requiresConfirmation
                ? "This plan contains an operation that requires local confirmation."
                : "This plan contains only locally auto-admitted operations.",
            TaskId = state.ActiveTask.TaskId,
            PlanId = state.ActiveTask.PlanId,
            PlanRevision = state.ActiveTask.PlanRevision,
        };
        if (requiresConfirmation)
        {
            gate.RequestId = BuildStableIdentity(
                "plan-gate",
                state.ConversationActorId,
                state.ActiveTask.TaskId,
                state.ActiveTask.PlanId,
                state.ActiveTask.PlanRevision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        }

        gate.Admissions.Add(new NyxIdChatPlanOperationAdmission
        {
            Key = step.Operation.Key.Clone(),
            ToolCallId = call.CallId,
            ToolName = call.ToolName,
            ArgumentsSha256 = HashArguments(call.ArgumentsJson),
        });
        return gate;
    }

    public static NyxIdChatPlanGate BuildActionGate(
        NyxIdChatConversationGAgentState state,
        NyxIdChatActionRequestState request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);

        var requiresConfirmation = request.AdvisoryRisk is
            NyxIdAssistantActionRisk.Grant or
            NyxIdAssistantActionRisk.Destructive;
        var gate = new NyxIdChatPlanGate
        {
            Mode = requiresConfirmation
                ? NyxIdChatPlanGateMode.Confirm
                : NyxIdChatPlanGateMode.Auto,
            Status = requiresConfirmation
                ? NyxIdChatPlanGateStatus.Pending
                : NyxIdChatPlanGateStatus.Satisfied,
            Reason = requiresConfirmation
                ? "This plan contains a browser-owned NyxID action that requires local confirmation."
                : "This browser-owned action does not require a separate local confirmation.",
            RequestId = requiresConfirmation
                ? BuildStableIdentity(
                    "plan-gate",
                    state.ConversationActorId,
                    state.ActiveTask.TaskId,
                    state.ActiveTask.PlanId,
                    state.ActiveTask.PlanRevision.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    request.ActionRequestId)
                : string.Empty,
            TaskId = state.ActiveTask.TaskId,
            PlanId = state.ActiveTask.PlanId,
            PlanRevision = state.ActiveTask.PlanRevision,
        };
        gate.Admissions.Add(new NyxIdChatPlanOperationAdmission
        {
            ActionRequestId = request.ActionRequestId,
            Action = request.Action,
            ActionParamsSha256 = HashActionParams(request.Params),
        });
        return gate;
    }

    public static NyxIdChatPlanResolutionDecision Resolve(
        NyxIdChatConversationGAgentState state,
        NyxIdChatPlanResolveCommand command,
        long currentStateVersion,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(now);

        var replay = state.RecentPlanResolutions.FirstOrDefault(candidate =>
            string.Equals(candidate.RequestId, command.RequestId?.Trim(), StringComparison.Ordinal));
        if (replay is not null)
        {
            var exact = MatchesConversation(state, command) &&
                        string.Equals(
                            replay.ClientRequestId,
                            command.ClientRequestId?.Trim(),
                            StringComparison.Ordinal) &&
                        replay.Confirmed == command.Confirmed &&
                        string.Equals(replay.TaskId, command.TaskId?.Trim(), StringComparison.Ordinal) &&
                        string.Equals(replay.PlanId, command.PlanId?.Trim(), StringComparison.Ordinal) &&
                        replay.PlanRevision == command.PlanRevision;
            return new(false, exact, state.Clone(), exact ? replay.Clone() : null);
        }

        var activeTask = state.ActiveTask;
        var gate = activeTask?.Gate;
        if (command.ExpectedStateVersion != currentStateVersion ||
            !MatchesConversation(state, command) ||
            string.IsNullOrWhiteSpace(command.ClientRequestId) ||
            string.IsNullOrWhiteSpace(command.RequestId) ||
            string.IsNullOrWhiteSpace(command.PlanId) ||
            activeTask is null ||
            gate is not
            {
                Mode: NyxIdChatPlanGateMode.Confirm,
                Status: NyxIdChatPlanGateStatus.Pending,
            } ||
            !string.Equals(gate.RequestId, command.RequestId.Trim(), StringComparison.Ordinal) ||
            !string.Equals(gate.TaskId, command.TaskId?.Trim(), StringComparison.Ordinal) ||
            !string.Equals(gate.PlanId, command.PlanId?.Trim(), StringComparison.Ordinal) ||
            gate.PlanRevision != command.PlanRevision ||
            !string.Equals(activeTask.TaskId, gate.TaskId, StringComparison.Ordinal) ||
            !string.Equals(activeTask.PlanId, gate.PlanId, StringComparison.Ordinal) ||
            activeTask.PlanRevision != gate.PlanRevision)
        {
            return NoCommit(state);
        }

        var next = state.Clone();
        var nextGate = next.ActiveTask.Gate;
        nextGate.Status = command.Confirmed
            ? NyxIdChatPlanGateStatus.Satisfied
            : NyxIdChatPlanGateStatus.Rejected;
        nextGate.DecidedAt = now.Clone();

        var resolution = new NyxIdChatPlanResolutionState
        {
            RequestId = nextGate.RequestId,
            ClientRequestId = command.ClientRequestId.Trim(),
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
            Confirmed = command.Confirmed,
            TaskId = nextGate.TaskId,
            PlanId = nextGate.PlanId,
            PlanRevision = nextGate.PlanRevision,
            CommittedAt = now.Clone(),
        };

        NyxIdChatOperationDispatchCommand? nextCommand = null;
        if (command.Confirmed)
        {
            var admission = AdmitPlan(next, nextGate, command, now);
            if (!admission.IsValid)
                return NoCommit(state);
            nextCommand = admission.NextCommand;
        }
        else
        {
            RejectPlan(next, nextGate, now);
        }

        next.LatestPlanResolution = resolution.Clone();
        next.RecentPlanResolutions.Add(resolution.Clone());
        while (next.RecentPlanResolutions.Count > ResolutionHistoryLimit)
            next.RecentPlanResolutions.RemoveAt(0);
        next.ProgressSequence = checked(Math.Max(0, next.ProgressSequence) + 1);
        next.UpdatedAt = now.Clone();
        return new(true, false, next, resolution, nextCommand);
    }

    public static ByteString HashArguments(string? argumentsJson) =>
        ByteString.CopyFrom(SHA256.HashData(Encoding.UTF8.GetBytes(argumentsJson ?? string.Empty)));

    public static ByteString HashActionParams(NyxIdAssistantActionParams? actionParams) =>
        ByteString.CopyFrom(SHA256.HashData(actionParams?.ToByteArray() ?? []));

    public static NyxIdChatTurnPlanGateAdmissionCommand? BuildTurnAdmission(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey sourceOperationKey,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(sourceOperationKey);
        ArgumentNullException.ThrowIfNull(now);

        var task = state.ActiveTask;
        var gate = task?.Gate;
        if (task is null ||
            gate is not
            {
                Mode: NyxIdChatPlanGateMode.Confirm,
                Status: NyxIdChatPlanGateStatus.Pending,
            } ||
            gate.Admissions.Count != 1 ||
            !string.Equals(task.TaskId, sourceOperationKey.TaskId, StringComparison.Ordinal))
        {
            return null;
        }

        var admitted = gate.Admissions[0];
        if (!string.IsNullOrWhiteSpace(admitted.ActionRequestId) ||
            admitted.Key is null ||
            admitted.ArgumentsSha256.Length != SHA256.HashSizeInBytes)
        {
            return null;
        }

        var step = task.Steps.SingleOrDefault(candidate =>
            candidate.Status == NyxIdChatStepStatus.Planned &&
            KeysEqual(candidate.Operation?.Key, admitted.Key));
        if (step?.Source?.Tool is null ||
            step.RematerializeDurableAuthorization &&
            !KeysEqual(step.RetryAuthorizationSourceKey, sourceOperationKey))
            return null;

        return new NyxIdChatTurnPlanGateAdmissionCommand
        {
            SourceOperationKey = sourceOperationKey.Clone(),
            Admission = new NyxIdChatTurnPlanGateAdmissionState
            {
                Key = admitted.Key.Clone(),
                GateRequestId = gate.RequestId,
                TaskId = gate.TaskId,
                PlanId = gate.PlanId,
                PlanRevision = gate.PlanRevision,
                ToolCallId = admitted.ToolCallId,
                ToolName = admitted.ToolName,
                ArgumentsSha256 = admitted.ArgumentsSha256,
                MayChangeExternalState = step.MayChangeExternalState,
                OperationAdmission = step.Source.Tool.OperationAdmission?.Clone(),
                AdmittedAt = now.Clone(),
                RematerializeDurableAuthorization =
                    step.RematerializeDurableAuthorization,
            },
        };
    }

    public static NyxIdChatPlanGateExpirationDecision ExpireCapability(
        NyxIdChatConversationGAgentState state,
        NyxIdChatPlanGateCapabilityExpiredSignal signal,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(now);

        var admission = signal.Admission;
        var gate = state.ActiveTask?.Gate;
        if (admission?.Key is null ||
            string.IsNullOrWhiteSpace(signal.FailureCode) ||
            string.IsNullOrWhiteSpace(signal.SafeMessage) ||
            gate is not
            {
                Mode: NyxIdChatPlanGateMode.Confirm,
                Status: NyxIdChatPlanGateStatus.Pending,
            } ||
            !MatchesGateAdmission(gate, admission))
        {
            return new NyxIdChatPlanGateExpirationDecision(false, state.Clone());
        }

        var next = state.Clone();
        var step = next.ActiveTask.Steps.SingleOrDefault(candidate =>
            candidate.Status == NyxIdChatStepStatus.Planned &&
            KeysEqual(candidate.Operation?.Key, admission.Key));
        if (step?.Operation is null)
            return new NyxIdChatPlanGateExpirationDecision(false, state.Clone());

        next.ActiveTask.Gate.Status = NyxIdChatPlanGateStatus.Rejected;
        next.ActiveTask.Gate.DecidedAt = now.Clone();
        step.Status = NyxIdChatStepStatus.Failed;
        step.FailureCode = signal.FailureCode;
        step.SafeMessage = signal.SafeMessage;
        step.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
        step.Operation.Phase = NyxIdChatOperationPhase.Failed;
        step.Operation.TerminalCode = signal.FailureCode;
        step.Operation.SafeMessage = signal.SafeMessage;
        step.Operation.CompletedAt = now.Clone();
        step.UpdatedAt = now.Clone();
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        CancelDependentSteps(next.ActiveTask, step.StepId, now);
        next.ActiveTask.Status = NyxIdChatTaskStatus.Failed;
        next.ActiveTask.ActiveStepId = string.Empty;
        next.ActiveTask.ActiveOperationId = string.Empty;
        next.ActiveTask.FailureCode = signal.FailureCode;
        next.ActiveTask.SafeMessage = signal.SafeMessage;
        next.ActiveTask.UpdatedAt = now.Clone();
        next.ActiveTurn.Status = NyxIdChatTurnStatus.Failed;
        next.ActiveTurn.FailureCode = signal.FailureCode;
        next.ActiveTurn.SafeMessage = signal.SafeMessage;
        next.ActiveTurn.TerminalAt = now.Clone();
        next.LatestTurn = next.ActiveTurn.Clone();
        AddTerminalSummary(next, next.ActiveTurn);
        next.ProgressSequence = checked(Math.Max(0, next.ProgressSequence) + 1);
        next.UpdatedAt = now.Clone();
        return new NyxIdChatPlanGateExpirationDecision(true, next);
    }

    public static bool CanPublishAction(
        NyxIdChatConversationGAgentState state,
        NyxIdChatActionRequestState request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);

        var task = state.ActiveTask;
        var gate = task?.Gate;
        if (task is null || gate is null)
            return true;

        var admission = gate.Admissions.SingleOrDefault(candidate =>
            string.Equals(candidate.ActionRequestId, request.ActionRequestId, StringComparison.Ordinal));
        if (admission is null)
        {
            return !string.Equals(request.TaskId, task.TaskId, StringComparison.Ordinal);
        }

        return gate.Status == NyxIdChatPlanGateStatus.Satisfied &&
               string.Equals(gate.TaskId, task.TaskId, StringComparison.Ordinal) &&
               string.Equals(gate.PlanId, task.PlanId, StringComparison.Ordinal) &&
               gate.PlanRevision == task.PlanRevision &&
               admission.Action == request.Action &&
               admission.ActionParamsSha256.Length == SHA256.HashSizeInBytes &&
               CryptographicOperations.FixedTimeEquals(
                   admission.ActionParamsSha256.Span,
                   HashActionParams(request.Params).Span);
    }

    private static bool ExceedsEstimatedDurationThreshold(
        IEnumerable<NyxIdChatTaskStepState> plannedSteps,
        int thresholdSeconds)
    {
        long totalSeconds = 0;
        foreach (var step in plannedSteps)
        {
            if (step.Estimate is not
                {
                    Kind: NyxIdChatStepEstimateKind.Duration,
                    Seconds: > 0,
                } estimate)
            {
                continue;
            }

            totalSeconds += estimate.Seconds;
            if (totalSeconds > thresholdSeconds)
                return true;
        }

        return false;
    }

    private static bool MatchesGateAdmission(
        NyxIdChatPlanGate gate,
        NyxIdChatTurnPlanGateAdmissionState admission)
    {
        if (!string.Equals(gate.RequestId, admission.GateRequestId, StringComparison.Ordinal) ||
            !string.Equals(gate.TaskId, admission.TaskId, StringComparison.Ordinal) ||
            !string.Equals(gate.PlanId, admission.PlanId, StringComparison.Ordinal) ||
            gate.PlanRevision != admission.PlanRevision)
        {
            return false;
        }

        var expected = gate.Admissions.SingleOrDefault(candidate =>
            KeysEqual(candidate.Key, admission.Key));
        return expected is not null &&
               string.Equals(expected.ToolCallId, admission.ToolCallId, StringComparison.Ordinal) &&
               string.Equals(expected.ToolName, admission.ToolName, StringComparison.Ordinal) &&
               expected.ArgumentsSha256.Length == admission.ArgumentsSha256.Length &&
               CryptographicOperations.FixedTimeEquals(
                   expected.ArgumentsSha256.Span,
                   admission.ArgumentsSha256.Span);
    }

    private static (bool IsValid, NyxIdChatOperationDispatchCommand? NextCommand) AdmitPlan(
        NyxIdChatConversationGAgentState state,
        NyxIdChatPlanGate gate,
        NyxIdChatPlanResolveCommand command,
        Timestamp now)
    {
        if (gate.Admissions.Count != 1)
            return (false, null);

        var admission = gate.Admissions[0];
        if (!string.IsNullOrWhiteSpace(admission.ActionRequestId))
        {
            var pendingAction = state.PendingActions.SingleOrDefault(candidate =>
                string.Equals(
                    candidate.ActionRequestId,
                    admission.ActionRequestId,
                    StringComparison.Ordinal));
            var actionStep = state.ActiveTask.Steps.SingleOrDefault(candidate =>
                candidate.Kind == NyxIdChatStepKind.BrowserAction &&
                candidate.Status == NyxIdChatStepStatus.Waiting &&
                string.Equals(
                    candidate.ActionRequestId,
                    admission.ActionRequestId,
                    StringComparison.Ordinal));
            var actionParamsHash = pendingAction is null
                ? ByteString.Empty
                : HashActionParams(pendingAction.Params);
            return pendingAction is not null &&
                   actionStep is not null &&
                   admission.Action == pendingAction.Action &&
                   !admission.ActionParamsSha256.IsEmpty &&
                   CryptographicOperations.FixedTimeEquals(
                       actionParamsHash.Span,
                       admission.ActionParamsSha256.Span)
                ? (true, null)
                : (false, null);
        }

        var step = state.ActiveTask.Steps.FirstOrDefault(candidate =>
            candidate.Status == NyxIdChatStepStatus.Planned &&
            candidate.Operation?.Key is not null &&
            KeysEqual(candidate.Operation.Key, admission.Key));
        var hasAgentProfile = state.AgentProfile is not null;
        var hasAgentProfileTurnAuthority =
            state.ActiveTurn?.AgentProfileTurnAuthority is not null;
        if (state.ActiveTurn is null ||
            step is null ||
            step.Kind != NyxIdChatStepKind.Tool ||
            string.IsNullOrWhiteSpace(admission.ToolCallId) ||
            string.IsNullOrWhiteSpace(admission.ToolName) ||
            admission.ArgumentsSha256.IsEmpty ||
            step.RematerializeDurableAuthorization &&
            (step.RetryAuthorizationSourceKey is null ||
             step.RetryToolInput?.Arguments is null ||
             hasAgentProfile != hasAgentProfileTurnAuthority))
        {
            return (false, null);
        }

        var sealedToolContext = command.ToolContext?.Clone();
        if (step.RematerializeDurableAuthorization &&
            !TrySealDurableRetryToolContext(state, command, out sealedToolContext))
        {
            return (false, null);
        }

        step.Status = NyxIdChatStepStatus.Running;
        step.Operation.Phase = NyxIdChatOperationPhase.Requested;
        step.Operation.RequestedAt = now.Clone();
        step.UpdatedAt = now.Clone();
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        state.ActiveTask.Status = NyxIdChatTaskStatus.Active;
        state.ActiveTask.ActiveStepId = step.StepId;
        state.ActiveTask.ActiveOperationId = step.Operation.Key.OperationId;
        state.ActiveTask.UpdatedAt = now.Clone();
        state.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
        state.ActiveTurn.TerminalAt = null;
        state.LatestTurn = state.ActiveTurn.Clone();

        var continuation = new NyxIdChatPlanGateContinuationInput
        {
            GateRequestId = gate.RequestId,
            TaskId = gate.TaskId,
            PlanId = gate.PlanId,
            PlanRevision = gate.PlanRevision,
            ToolCallId = admission.ToolCallId,
            ToolName = admission.ToolName,
            ArgumentsSha256 = admission.ArgumentsSha256,
            ToolContext = sealedToolContext,
            MayChangeExternalState = step.MayChangeExternalState,
            IdempotencyKey = step.Operation.Key.OperationId,
            OperationAdmission = step.Source?.Tool?.OperationAdmission?.Clone(),
        };
        if (step.RematerializeDurableAuthorization &&
            step.RetryToolInput?.Arguments is not null)
        {
            continuation.RetryArguments = step.RetryToolInput.Arguments.Clone();
            continuation.AgentProfile = state.AgentProfile?.Clone();
            continuation.AgentProfileTurnAuthority =
                state.ActiveTurn.AgentProfileTurnAuthority?.Clone();
            continuation.RematerializeDurableAuthorization = true;
        }

        return (true, new NyxIdChatOperationDispatchCommand
        {
            Key = step.Operation.Key.Clone(),
            PlanGateContinuation = continuation,
        });
    }

    private static bool TrySealDurableRetryToolContext(
        NyxIdChatConversationGAgentState state,
        NyxIdChatPlanResolveCommand command,
        out Aevatar.AI.Abstractions.AgentToolExecutionContextPayload? sealedContext)
    {
        sealedContext = null;
        var scopeId = state.ScopeId?.Trim();
        var actorId = state.ConversationActorId?.Trim();
        var ownerSubject = state.OwnerSubject?.Trim();
        var requestId = command.RequestId?.Trim();
        if (scopeId is null or "" ||
            actorId is null or "" ||
            ownerSubject is null or "" ||
            requestId is null or "" ||
            command.ToolContext is null ||
            !string.Equals(command.OwnerSubject?.Trim(), ownerSubject, StringComparison.Ordinal))
        {
            return false;
        }

        var supplied = AgentToolExecutionContextMapper.FromPayload(command.ToolContext);
        if ((string.IsNullOrWhiteSpace(supplied.Credentials.NyxIdAccessToken) &&
             string.IsNullOrWhiteSpace(supplied.Credentials.NyxIdOrgToken)) ||
            !string.Equals(supplied.Request.RequestId, requestId, StringComparison.Ordinal) ||
            !string.Equals(supplied.Caller.ScopeId, scopeId, StringComparison.Ordinal) ||
            !string.Equals(supplied.Caller.OwnerScopeId, scopeId, StringComparison.Ordinal) ||
            !string.Equals(supplied.Caller.OwnerSubject, ownerSubject, StringComparison.Ordinal) ||
            !string.Equals(supplied.Caller.ResponseId, requestId, StringComparison.Ordinal) ||
            !string.Equals(
                supplied.Channel.Platform,
                NyxIdChatServiceDefaults.ServiceId,
                StringComparison.Ordinal) ||
            !string.Equals(supplied.Channel.SenderId, ownerSubject, StringComparison.Ordinal) ||
            !string.Equals(supplied.Channel.RegistrationScopeId, scopeId, StringComparison.Ordinal) ||
            supplied.ExecutionOwner.Kind != AgentToolExecutionOwnerKind.Actor ||
            !string.Equals(supplied.ExecutionOwner.OwnerId, actorId, StringComparison.Ordinal))
        {
            return false;
        }

        sealedContext = (AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(requestId, null),
            Credentials = supplied.Credentials,
            Caller = new AgentToolCallerContext(scopeId, ownerSubject, requestId, scopeId),
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                ownerSubject,
                scopeId,
                null,
                null),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                string.Empty,
                ownerSubject,
                "proxy"),
            ExecutionOwner = AgentToolExecutionOwners.Actor(actorId),
        }).ToPayload();
        return true;
    }

    private static void RejectPlan(
        NyxIdChatConversationGAgentState state,
        NyxIdChatPlanGate gate,
        Timestamp now)
    {
        foreach (var admission in gate.Admissions)
        {
            if (!string.IsNullOrWhiteSpace(admission.ActionRequestId))
            {
                foreach (var actionStep in state.ActiveTask.Steps.Where(candidate =>
                             string.Equals(
                                 candidate.ActionRequestId,
                                 admission.ActionRequestId,
                                 StringComparison.Ordinal) &&
                             candidate.Status is
                                 NyxIdChatStepStatus.Planned or
                                 NyxIdChatStepStatus.Waiting))
                {
                    actionStep.Status = NyxIdChatStepStatus.Cancelled;
                    actionStep.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
                    actionStep.UpdatedAt = now.Clone();
                    if (actionStep.Operation is not null)
                        actionStep.Operation.Phase = NyxIdChatOperationPhase.Cancelled;
                    actionStep.AvailableActions =
                        NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(actionStep);
                    CancelDependentSteps(state.ActiveTask, actionStep.StepId, now);
                }

                var pendingAction = state.PendingActions.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.ActionRequestId,
                        admission.ActionRequestId,
                        StringComparison.Ordinal));
                if (pendingAction is not null)
                {
                    state.PendingActions.Remove(pendingAction);
                    state.RecentActions.Add(pendingAction.Clone());
                    while (state.RecentActions.Count > ResolutionHistoryLimit)
                        state.RecentActions.RemoveAt(0);
                }
                continue;
            }

            var step = state.ActiveTask.Steps.FirstOrDefault(candidate =>
                KeysEqual(candidate.Operation?.Key, admission.Key));
            if (step is null || step.Status != NyxIdChatStepStatus.Planned)
                continue;
            step.Status = NyxIdChatStepStatus.Cancelled;
            step.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
            step.UpdatedAt = now.Clone();
            if (step.Operation is not null)
                step.Operation.Phase = NyxIdChatOperationPhase.Cancelled;
            step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
            CancelDependentSteps(state.ActiveTask, step.StepId, now);
        }

        const string message = "The plan was not confirmed and no operation was dispatched.";
        state.ActiveTask.Status = NyxIdChatTaskStatus.Stopped;
        state.ActiveTask.ActiveStepId = string.Empty;
        state.ActiveTask.ActiveOperationId = string.Empty;
        state.ActiveTask.SafeMessage = message;
        state.ActiveTask.UpdatedAt = now.Clone();
        state.ActiveTurn.Status = NyxIdChatTurnStatus.Stopped;
        state.ActiveTurn.SafeMessage = message;
        state.ActiveTurn.TerminalAt = now.Clone();
        state.LatestTurn = state.ActiveTurn.Clone();
        AddTerminalSummary(state, state.ActiveTurn);
    }

    private static void CancelDependentSteps(
        NyxIdChatTaskState task,
        string sourceStepId,
        Timestamp now)
    {
        var cancelledStepIds = new HashSet<string>(StringComparer.Ordinal) { sourceStepId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var dependent in task.Steps.Where(candidate =>
                         candidate.Status is
                             NyxIdChatStepStatus.Planned or
                             NyxIdChatStepStatus.Waiting &&
                         candidate.DependsOn.Any(cancelledStepIds.Contains)))
            {
                dependent.Status = NyxIdChatStepStatus.Cancelled;
                dependent.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
                dependent.UpdatedAt = now.Clone();
                if (dependent.Operation is not null)
                    dependent.Operation.Phase = NyxIdChatOperationPhase.Cancelled;
                dependent.AvailableActions =
                    NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(dependent);
                changed |= cancelledStepIds.Add(dependent.StepId);
            }
        }
    }

    private static void AddTerminalSummary(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTurnState turn)
    {
        const int historyLimit = 32;
        var summary = state.RecentTerminalTurns.FirstOrDefault(candidate =>
            string.Equals(candidate.TurnId, turn.TurnId, StringComparison.Ordinal));
        if (summary is null)
        {
            summary = new NyxIdChatTurnSummary { TurnId = turn.TurnId };
            state.RecentTerminalTurns.Add(summary);
        }

        summary.TaskId = turn.TaskId;
        summary.Status = turn.Status;
        summary.FailureCode = turn.FailureCode;
        summary.SafeMessage = turn.SafeMessage;
        summary.TerminalAt = turn.TerminalAt?.Clone();
        while (state.RecentTerminalTurns.Count > historyLimit)
            state.RecentTerminalTurns.RemoveAt(0);
    }

    private static bool MatchesConversation(
        NyxIdChatConversationGAgentState state,
        NyxIdChatPlanResolveCommand command) =>
        !string.IsNullOrWhiteSpace(command.ScopeId) &&
        !string.IsNullOrWhiteSpace(command.ConversationActorId) &&
        string.Equals(state.ScopeId, command.ScopeId.Trim(), StringComparison.Ordinal) &&
        string.Equals(
            state.ConversationActorId,
            command.ConversationActorId.Trim(),
            StringComparison.Ordinal);

    private static bool KeysEqual(NyxIdChatOperationKey? left, NyxIdChatOperationKey? right) =>
        left is not null &&
        right is not null &&
        left.Equals(right);

    private static string BuildStableIdentity(string prefix, params string[] parts)
    {
        var identity = string.Concat(parts.Select(static part => $"{part.Length}:{part}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"{prefix}-{Convert.ToHexStringLower(hash)[..32]}";
    }

    private static NyxIdChatPlanResolutionDecision NoCommit(
        NyxIdChatConversationGAgentState state) =>
        new(false, false, state.Clone(), null);
}
