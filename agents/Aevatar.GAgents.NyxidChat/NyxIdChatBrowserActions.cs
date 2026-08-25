using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

public sealed record NyxIdChatBrowserActionDecision(
    bool ShouldCommit,
    bool ShouldDispatch,
    NyxIdChatTransitionOutcome Outcome,
    string ReasonCode,
    string SafeMessage,
    NyxIdChatConversationGAgentState State,
    NyxIdChatActionRequestState Request,
    NyxIdChatContinuationAdmissionState Admission,
    NyxIdChatOperationDispatchCommand? NextCommand);

/// <summary>
/// Pure browser-action request and continuation policy. NyxID owns the browser
/// journey and mutation; this policy owns only Aevatar correlation, durable
/// task facts, continuation admission, and typed postcondition reconciliation.
/// </summary>
public static class NyxIdChatBrowserActions
{
    public const string ActionRequested = "NYXID_ACTION_REQUESTED";
    public const string ActionRequestConflict = "NYXID_ACTION_REQUEST_CONFLICT";
    public const string ActionRequestInvalid = "NYXID_ACTION_REQUEST_INVALID";
    public const string ActionContinuationAccepted = "NYXID_ACTION_CONTINUATION_ACCEPTED";
    public const string ActionContinuationConflict = "NYXID_ACTION_CONTINUATION_CONFLICT";
    public const string ActionContinuationInvalid = "NYXID_ACTION_CONTINUATION_INVALID";
    public const string ActionContinuationActiveTurn = "NYXID_ACTION_CONTINUATION_ACTIVE_TURN";
    public const string ActionPostconditionVerified = "NYXID_ACTION_POSTCONDITION_VERIFIED";
    public const string ActionPostconditionUnverified = "NYXID_ACTION_POSTCONDITION_UNVERIFIED";

    private const string BrowserActionBlockedMessage =
        "Complete the requested action in NyxID, then continue this conversation.";

    // A service-access review means the service is already connected and only
    // this session's authorization is missing; the generic blocked message
    // reads as "connect the service again" and hides both facts, so the
    // review action carries its own honest instruction (issue #3532).
    private const string ServiceAccessReviewBlockedMessage =
        "This service is already connected to your NyxID; this chat session just " +
        "is not authorized to use it yet. Approve the service access review to " +
        "grant it — in NyxID, open Settings → Account → Manage service access — " +
        "then continue this conversation.";
    private const string ServiceAccessRequiredReasonCode =
        "USER_SERVICE_ACCESS_REQUIRED";
    private const int HistoryLimit = 32;

    public static NyxIdChatBrowserActionDecision RequestAuthorization(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        NyxIdAssistantActionRegistry registry,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(now);

        var signalKey = signal.Key;
        var receipt = signal.Tool?.Receipt;
        var blocker = receipt?.AuthorizationRequired;
        var sourceStep = FindStep(state, signalKey?.StepId);
        var serviceAccessReviewRequested = string.Equals(
            blocker?.ReasonCode,
            ServiceAccessRequiredReasonCode,
            StringComparison.Ordinal);
        var hasServiceAccessReview =
            serviceAccessReviewRequested &&
            !string.IsNullOrWhiteSpace(blocker?.UserServiceId) &&
            !string.IsNullOrWhiteSpace(blocker?.ServiceSlug) &&
            !string.IsNullOrWhiteSpace(blocker?.ResourceUri);
        var hasCatalogServiceConnect =
            !serviceAccessReviewRequested &&
            !string.IsNullOrWhiteSpace(blocker?.ServiceSlug);
        var hasKeyCreate = blocker?.KeyCreate is not null;
        var hasKeyRotate = blocker?.KeyRotate is not null;
        if (receipt?.Status != AgentToolReceiptStatus.AuthorizationRequired ||
            blocker is null ||
            signalKey is null ||
            sourceStep?.Operation?.Key is null ||
            !KeysEqual(sourceStep.Operation.Key, signalKey) ||
            (hasServiceAccessReview ? 1 : 0) +
            (hasCatalogServiceConnect ? 1 : 0) +
            (hasKeyCreate ? 1 : 0) +
            (hasKeyRotate ? 1 : 0) != 1 ||
            state.ActiveTurn is null ||
            state.ActiveTask is null)
        {
            return Reject(state, ActionRequestInvalid);
        }

        var actionState = state;
        if (sourceStep.Status != NyxIdChatStepStatus.Waiting)
        {
            var waiting = NyxIdChatTaskLifecycle.ApplyOperationResult(state, signal, now);
            if (waiting.Outcome != NyxIdChatTransitionOutcome.Accepted ||
                FindStep(waiting.State, signalKey.StepId)?.Status != NyxIdChatStepStatus.Waiting)
            {
                return Reject(state, ActionRequestInvalid);
            }

            actionState = waiting.State;
            sourceStep = FindStep(actionState, signalKey.StepId)!;
        }

        var validated = hasServiceAccessReview
            ? registry.ResolveServiceAccessReview(
                blocker.UserServiceId,
                blocker.ServiceSlug,
                blocker.ResourceUri)
            : hasKeyCreate
            ? registry.ResolveKeyCreate(blocker.KeyCreate)
            : hasKeyRotate
                ? registry.ResolveKeyRotate(blocker.KeyRotate)
                : registry.ResolveCatalogServiceConnect(
                    blocker.ServiceSlug,
                    blocker.RequestedScopes);
        var actionRequestId = BuildStableIdentity(
            "action",
            actionState.ConversationActorId,
            actionState.ActiveTurn.TurnId,
            actionState.ActiveTask.TaskId,
            sourceStep.StepId,
            validated.Definition.WireAction,
            validated.Params.ToByteString().ToBase64());
        var actionStepId = BuildStableIdentity(
            "step",
            state.ConversationActorId,
            state.ActiveTurn.TurnId,
            state.ActiveTask.TaskId,
            actionRequestId,
            "browser-action");
        var request = new NyxIdChatActionRequestState
        {
            SchemaVersion = validated.Definition.SchemaVersion,
            RegistryRevision = validated.Definition.RegistryRevision,
            ConversationActorId = actionState.ConversationActorId,
            OriginTurnId = actionState.ActiveTurn.TurnId,
            TaskId = actionState.ActiveTask.TaskId,
            StepId = actionStepId,
            ActionRequestId = actionRequestId,
            Action = validated.Definition.Action,
            Params = validated.Params.Clone(),
            AdvisoryRisk = validated.Definition.AdvisoryRisk,
            RememberEligible = validated.Definition.RememberEligible,
            RequestedAt = now.Clone(),
            SourceToolStepId = sourceStep.StepId,
        };
        return CommitRequest(actionState, request, now);
    }

    public static NyxIdChatBrowserActionDecision CommitRequest(
        NyxIdChatConversationGAgentState state,
        NyxIdChatActionRequestState request,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(now);

        var existing = state.PendingActions
            .Concat(state.RecentActions)
            .FirstOrDefault(candidate => string.Equals(
                candidate.ActionRequestId,
                request.ActionRequestId,
                StringComparison.Ordinal));
        if (existing is not null)
        {
            return RequestContentEqual(existing, request)
                ? NoCommit(
                    state,
                    NyxIdChatTransitionOutcome.Idempotent,
                    ActionRequested,
                    existing)
                : Reject(state, ActionRequestConflict, request);
        }

        if (!RequestMatchesState(state, request))
            return Reject(state, ActionRequestInvalid, request);

        var next = state.Clone();
        var task = next.ActiveTask;
        var turn = next.ActiveTurn;
        var previousStepCount = task.Steps.Count;
        var supersededContinuation = FindSourceToolContinuation(
            task,
            request.HasSourceToolStepId ? request.SourceToolStepId : string.Empty);
        var actionStep = new NyxIdChatTaskStepState
        {
            StepId = request.StepId,
            Order = task.Steps.Count + 1,
            Kind = NyxIdChatStepKind.BrowserAction,
            Status = NyxIdChatStepStatus.Waiting,
            Required = true,
            Description = "Complete the browser-owned NyxID action.",
            Source = new NyxIdChatStepSource
            {
                BrowserAction = new NyxIdChatBrowserActionStepSource
                {
                    Action = request.Action,
                    ActionRequestId = request.ActionRequestId,
                },
            },
            ActionRequestId = request.ActionRequestId,
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            AddedBy = task.Steps.Count == 0
                ? NyxIdChatStepAddedBy.Initial
                : NyxIdChatStepAddedBy.Replan,
            UpdatedAt = now.Clone(),
        };
        if (request.HasSourceToolStepId)
            actionStep.DependsOn.Add(request.SourceToolStepId);
        actionStep.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(actionStep);
        var postconditionStep = BuildPostconditionStep(
            next,
            task,
            request,
            task.Steps.Count + 2,
            now);
        postconditionStep.AddedBy = actionStep.AddedBy;
        NyxIdChatTaskStepState? actionContinuationStep = null;
        if (supersededContinuation is not null)
        {
            CancelSupersededContinuation(supersededContinuation, now);
            actionContinuationStep = BuildActionContinuationStep(
                next,
                task,
                request,
                postconditionStep,
                task.Steps.Count + 3,
                now);
            actionContinuationStep.AddedBy = actionStep.AddedBy;
        }
        task.Steps.Add(actionStep);
        task.Steps.Add(postconditionStep);
        if (actionContinuationStep is not null)
            task.Steps.Add(actionContinuationStep);
        List<NyxIdChatTaskStepState> addedSteps = [actionStep, postconditionStep];
        if (actionContinuationStep is not null)
            addedSteps.Add(actionContinuationStep);
        if (previousStepCount == 0 &&
            task.PlanRevision <= 1 &&
            task.PlanRevisions.Count == 0)
        {
            NyxIdChatPlanRevisions.CommitInitial(task, now, addedSteps.ToArray());
        }
        else
        {
            NyxIdChatPlanRevisions.CommitChange(
                task,
                NyxIdChatPlanRevisionCause.ScopeResolution,
                now,
                addedSteps,
                supersededContinuation is null ? null : [supersededContinuation]);
        }
        var blockedMessage = BlockedMessageForAction(request.Action);
        task.Status = NyxIdChatTaskStatus.Blocked;
        task.ActiveStepId = actionStep.StepId;
        task.ActiveOperationId = string.Empty;
        task.FailureCode = ActionRequested;
        task.SafeMessage = blockedMessage;
        task.UpdatedAt = now.Clone();
        turn.Status = NyxIdChatTurnStatus.Blocked;
        turn.FailureCode = ActionRequested;
        turn.SafeMessage = blockedMessage;
        turn.TerminalAt = now.Clone();
        next.LatestTurn = turn.Clone();
        next.PendingActions.Add(request.Clone());
        AddTerminalSummary(next, turn);
        next.ProgressSequence++;
        next.UpdatedAt = now.Clone();
        return new NyxIdChatBrowserActionDecision(
            ShouldCommit: true,
            ShouldDispatch: false,
            NyxIdChatTransitionOutcome.Accepted,
            ActionRequested,
            blockedMessage,
            next,
            request.Clone(),
            new NyxIdChatContinuationAdmissionState(),
            NextCommand: null);
    }

    private static string BlockedMessageForAction(NyxIdAssistantActionKind action) =>
        action == NyxIdAssistantActionKind.ServiceAccessReview
            ? ServiceAccessReviewBlockedMessage
            : BrowserActionBlockedMessage;

    public static NyxIdChatBrowserActionDecision Continue(
        NyxIdChatConversationGAgentState state,
        NyxIdChatActionContinueCommand command,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(now);

        var isStateChangeWake = command.Actions.Count == 0;
        if (!ValidContinuationIdentity(state, command, isStateChangeWake))
            return RejectContinuation(state, ActionContinuationInvalid);
        if (!isStateChangeWake &&
            command.Actions.Any(report => !ValidReport(report, command.OriginTurnId)))
        {
            return RejectContinuation(state, ActionContinuationInvalid);
        }

        if (!TryNormalizeReports(command.Actions, now, out var sanitizedReports))
            return RejectContinuation(state, ActionContinuationConflict);

        var existingAdmission = state.ContinuationAdmission;
        if (existingAdmission is
            {
                Kind: NyxIdChatContinuationKind.Action,
                ClientRequestId.Length: > 0,
            } && string.Equals(
                existingAdmission.ClientRequestId,
                Normalize(command.ClientRequestId),
                StringComparison.Ordinal))
        {
            if (!AdmissionMatches(existingAdmission, command, sanitizedReports))
                return RejectContinuation(state, ActionContinuationConflict);

            var replay = TryBuildReplayDispatch(
                state,
                existingAdmission,
                command.ToolContext);
            return new NyxIdChatBrowserActionDecision(
                ShouldCommit: false,
                ShouldDispatch:
                    existingAdmission.Status !=
                        NyxIdChatContinuationAdmissionStatus.Rejected &&
                    replay is not null,
                NyxIdChatTransitionOutcome.Idempotent,
                string.IsNullOrWhiteSpace(existingAdmission.ReasonCode)
                    ? ActionContinuationAccepted
                    : existingAdmission.ReasonCode,
                existingAdmission.SafeMessage,
                state.Clone(),
                FindRequestForReports(state, existingAdmission.ActionReports) ??
                    new NyxIdChatActionRequestState(),
                existingAdmission.Clone(),
                replay);
        }

        if (state.ActiveTurn?.Status == NyxIdChatTurnStatus.Active)
        {
            var rejectedAdmission = new NyxIdChatContinuationAdmissionState
            {
                Kind = NyxIdChatContinuationKind.Action,
                RequestId = Normalize(command.CommandId),
                ClientRequestId = Normalize(command.ClientRequestId),
                OriginTurnId = Normalize(command.OriginTurnId),
                ContinuationTurnId = Normalize(command.ContinuationTurnId),
                Status = NyxIdChatContinuationAdmissionStatus.Rejected,
                ReasonCode = ActionContinuationActiveTurn,
                SafeMessage = "Another conversation turn is active.",
                CommittedAt = now.Clone(),
                OwnerSubject = Normalize(command.OwnerSubject),
            };
            rejectedAdmission.ActionReports.Add(
                sanitizedReports.Select(static report => report.Clone()));
            var rejectedState = state.Clone();
            rejectedState.ContinuationAdmission = rejectedAdmission.Clone();
            rejectedState.ProgressSequence++;
            rejectedState.UpdatedAt = now.Clone();
            return new NyxIdChatBrowserActionDecision(
                ShouldCommit: true,
                ShouldDispatch: false,
                NyxIdChatTransitionOutcome.Rejected,
                ActionContinuationActiveTurn,
                rejectedAdmission.SafeMessage,
                rejectedState,
                FindRequestForReports(state, sanitizedReports) ??
                    new NyxIdChatActionRequestState(),
                rejectedAdmission,
                NextCommand: null);
        }

        var pendingById = state.PendingActions.ToDictionary(
            static action => action.ActionRequestId,
            StringComparer.Ordinal);
        if (command.Actions.Any(report =>
                !pendingById.TryGetValue(report.ActionRequestId, out var pending) ||
                !string.Equals(pending.OriginTurnId, command.OriginTurnId, StringComparison.Ordinal) ||
                !string.Equals(pending.ConversationActorId, command.ConversationActorId, StringComparison.Ordinal) ||
                !ResourceMatchesAction(pending.Action, report.Disposition, report.Resource)))
        {
            return RejectContinuation(state, ActionContinuationInvalid);
        }

        var next = state.Clone();
        foreach (var report in sanitizedReports)
        {
            var pending = next.PendingActions.Single(action =>
                string.Equals(action.ActionRequestId, report.ActionRequestId, StringComparison.Ordinal));
            var prior = pending.Reports.FirstOrDefault(existing =>
                ReportsEqual(existing, report));
            if (prior is null)
                pending.Reports.Add(report.Clone());
        }

        var admission = new NyxIdChatContinuationAdmissionState
        {
            Kind = NyxIdChatContinuationKind.Action,
            RequestId = Normalize(command.CommandId),
            ClientRequestId = Normalize(command.ClientRequestId),
            OriginTurnId = Normalize(command.OriginTurnId),
            ContinuationTurnId = Normalize(command.ContinuationTurnId),
            Status = NyxIdChatContinuationAdmissionStatus.Accepted,
            ReasonCode = ActionContinuationAccepted,
            CommittedAt = now.Clone(),
            OwnerSubject = Normalize(command.OwnerSubject),
        };
        admission.ActionReports.Add(sanitizedReports.Select(static report => report.Clone()));
        next.ContinuationAdmission = admission.Clone();

        var originTurn = next.ActiveTurn?.Clone();
        var previousTask = next.ActiveTask?.Clone();
        var continuationTaskId = string.IsNullOrWhiteSpace(previousTask?.TaskId)
            ? BuildStableIdentity(
                "task",
                next.ConversationActorId,
                admission.OriginTurnId,
                admission.ClientRequestId,
                "action-continuation")
            : previousTask.TaskId;
        var turn = new NyxIdChatTurnState
        {
            TurnId = admission.ContinuationTurnId,
            TaskId = continuationTaskId,
            ClientRequestId = admission.ClientRequestId,
            Status = NyxIdChatTurnStatus.Active,
            Prompt = originTurn?.Prompt ?? string.Empty,
            CommandId = admission.RequestId,
            Intent = originTurn?.Intent ?? NyxIdChatTurnIntent.Unspecified,
            AgentProfileTurnAuthority = originTurn?.AgentProfileTurnAuthority?.Clone(),
            CreatedAt = previousTask?.CreatedAt?.Clone() ?? now.Clone(),
        };
        if (originTurn is not null)
            turn.InputParts.Add(originTurn.InputParts.Select(static part => part.Clone()));
        var task = previousTask ?? new NyxIdChatTaskState
        {
            TaskId = continuationTaskId,
            CreatedAt = now.Clone(),
            SchemaVersion = 5,
            ActorId = next.ConversationActorId,
            PlanId = continuationTaskId,
            PlanRevision = 1,
            Title = "Verify the requested NyxID action",
        };
        task.TurnId = turn.TurnId;
        task.Status = NyxIdChatTaskStatus.Active;
        task.UpdatedAt = now.Clone();

        NyxIdChatOperationDispatchCommand? firstDispatch = null;
        if (isStateChangeWake)
        {
            var pending = next.PendingActions
                .OrderBy(static request => request.ActionRequestId, StringComparer.Ordinal)
                .ToArray();
            for (var index = 0; index < pending.Length; index++)
            {
                var step = FindPostconditionStep(task, pending[index].ActionRequestId);
                if (step is null)
                    return RejectContinuation(state, ActionContinuationInvalid);
                if (firstDispatch is not null)
                    continue;

                ActivatePostcondition(task, step, now);
                firstDispatch = BuildPostconditionCommand(
                    next.ScopeId,
                    admission.OwnerSubject,
                    pending[index],
                    report: null,
                    step.Operation.Key,
                    command.ToolContext);
            }
        }
        for (var reportIndex = 0; reportIndex < sanitizedReports.Length; reportIndex++)
        {
            var report = sanitizedReports[reportIndex];
            var request = next.PendingActions.Single(action =>
                string.Equals(action.ActionRequestId, report.ActionRequestId, StringComparison.Ordinal));
            if (report.Disposition == NyxIdChatActionDisposition.Completed)
            {
                var step = FindPostconditionStep(task, request.ActionRequestId);
                if (step is null)
                    return RejectContinuation(state, ActionContinuationInvalid);
                if (firstDispatch is null)
                {
                    ActivatePostcondition(task, step, now);
                    firstDispatch = BuildPostconditionCommand(
                        next.ScopeId,
                        admission.OwnerSubject,
                        request,
                        report,
                        step.Operation.Key,
                        command.ToolContext);
                }
                continue;
            }

            ApplyReportedActionOutcome(task, request, report, now);
            CancelPostcondition(task, request.ActionRequestId, report, now);
            MoveActionToRecent(next, request.ActionRequestId);
        }

        if (firstDispatch is null)
        {
            if (next.PendingActions.Count == 0)
                CompleteContinuationTask(next, task, turn, now);
            else
                BlockForPendingAction(next, task, turn, now);
        }

        next.ActiveTurn = turn;
        next.LatestTurn = turn.Clone();
        next.ActiveTask = task;
        next.ProgressSequence++;
        next.UpdatedAt = now.Clone();
        return new NyxIdChatBrowserActionDecision(
            ShouldCommit: true,
            ShouldDispatch: firstDispatch is not null,
            NyxIdChatTransitionOutcome.Accepted,
            ActionContinuationAccepted,
            string.Empty,
            next,
            next.PendingActions.FirstOrDefault() ?? next.RecentActions.LastOrDefault() ??
                new NyxIdChatActionRequestState(),
            admission,
            firstDispatch);
    }

    public static NyxIdChatBrowserActionDecision ReconcilePostcondition(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal,
        Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(now);

        var result = signal.ActionPostcondition;
        var step = FindStep(state, signal.Key?.StepId);
        var request = result is null
            ? null
            : state.PendingActions.FirstOrDefault(action => string.Equals(
                action.ActionRequestId,
                result.ActionRequestId,
                StringComparison.Ordinal));
        if (result is null ||
            step?.Operation?.Key is null ||
            !KeysEqual(step.Operation.Key, signal.Key) ||
            step.Kind != NyxIdChatStepKind.Postcondition ||
            request is null ||
            !PostconditionResourceMatchesAction(request.Action, result))
        {
            return RejectContinuation(state, ActionContinuationInvalid);
        }

        var next = state.Clone();
        var durableRequest = next.PendingActions.Single(action =>
            string.Equals(action.ActionRequestId, result.ActionRequestId, StringComparison.Ordinal));
        durableRequest.PostconditionResult = result.Clone();
        var durableStep = FindStep(next, signal.Key!.StepId)!;
        durableStep.UpdatedAt = now.Clone();
        durableStep.Operation.CompletedAt = now.Clone();
        durableStep.Operation.TerminalCode = result.FailureCode;
        durableStep.Operation.SafeMessage = result.SafeMessage;
        next.ActiveTask.ActiveOperationId = string.Empty;

        if (result.Verified && result.Disposition == NyxIdChatActionDisposition.Completed)
        {
            durableStep.Status = NyxIdChatStepStatus.Done;
            durableStep.Operation.Phase = NyxIdChatOperationPhase.Succeeded;
            durableStep.ExternalEffect = NyxIdChatEffectEvidence.Confirmed;
            durableStep.FailureCode = string.Empty;
            durableStep.SafeMessage = string.Empty;
            durableStep.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(durableStep);
            var actionStep = FindStep(next, durableRequest.StepId);
            if (actionStep is not null)
            {
                actionStep.Status = NyxIdChatStepStatus.Done;
                actionStep.ExternalEffect = NyxIdChatEffectEvidence.Confirmed;
                actionStep.FailureCode = string.Empty;
                actionStep.SafeMessage = string.Empty;
                actionStep.AvailableActions =
                    NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(actionStep);
                actionStep.UpdatedAt = now.Clone();
            }
            CompleteSourceToolStep(next, durableRequest, now);
            MoveActionToRecent(next, result.ActionRequestId);

            var successor = StartNextPlannedPostcondition(next, now);
            if (successor is null)
            {
                if (next.PendingActions.Count == 0)
                {
                    successor = StartActionContinuation(next, durableRequest, now);
                    if (successor is null)
                        CompleteContinuationTask(next, next.ActiveTask, next.ActiveTurn, now);
                }
                else
                {
                    BlockForPendingAction(next, next.ActiveTask, next.ActiveTurn, now);
                }
            }
            next.ActiveTask.UpdatedAt = now.Clone();
            next.LatestTurn = next.ActiveTurn.Clone();
            next.ProgressSequence++;
            next.UpdatedAt = now.Clone();
            return new NyxIdChatBrowserActionDecision(
                true,
                successor is not null,
                NyxIdChatTransitionOutcome.Accepted,
                ActionPostconditionVerified,
                string.Empty,
                next,
                durableRequest.Clone(),
                next.ContinuationAdmission?.Clone() ?? new NyxIdChatContinuationAdmissionState(),
                successor);
        }

        durableStep.Status = NyxIdChatStepStatus.Waiting;
        durableStep.Operation.Phase = NyxIdChatOperationPhase.Failed;
        durableStep.ExternalEffect = NyxIdChatEffectEvidence.NotStarted;
        durableStep.FailureCode = NormalizeFailureCode(
            result.FailureCode,
            "NYXID_ACTION_POSTCONDITION_NOT_VERIFIED");
        durableStep.SafeMessage = NormalizeSafeMessage(
            result.SafeMessage,
            "The NyxID action postcondition could not be verified.");
        durableStep.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(durableStep);
        next.ActiveTask.Status = NyxIdChatTaskStatus.Blocked;
        next.ActiveTask.ActiveStepId = durableStep.StepId;
        next.ActiveTask.FailureCode = durableStep.FailureCode;
        next.ActiveTask.SafeMessage = durableStep.SafeMessage;
        next.ActiveTask.UpdatedAt = now.Clone();
        next.ActiveTurn.Status = NyxIdChatTurnStatus.Blocked;
        next.ActiveTurn.FailureCode = durableStep.FailureCode;
        next.ActiveTurn.SafeMessage = durableStep.SafeMessage;
        next.ActiveTurn.TerminalAt = now.Clone();
        next.LatestTurn = next.ActiveTurn.Clone();
        AddTerminalSummary(next, next.ActiveTurn);
        next.ProgressSequence++;
        next.UpdatedAt = now.Clone();
        return new NyxIdChatBrowserActionDecision(
            true,
            false,
            NyxIdChatTransitionOutcome.Accepted,
            ActionPostconditionUnverified,
            durableStep.SafeMessage,
            next,
            durableRequest.Clone(),
            next.ContinuationAdmission?.Clone() ?? new NyxIdChatContinuationAdmissionState(),
            null);
    }

    private static NyxIdChatTaskStepState BuildPostconditionStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskState task,
        NyxIdChatActionRequestState request,
        int order,
        Timestamp now)
    {
        var stepId = BuildStableIdentity(
            "step",
            state.ConversationActorId,
            request.OriginTurnId,
            task.TaskId,
            request.ActionRequestId,
            "postcondition");
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = state.ConversationActorId,
            TurnId = request.OriginTurnId,
            TaskId = task.TaskId,
            StepId = stepId,
            OperationId = BuildStableIdentity(
                "operation",
                state.ConversationActorId,
                request.OriginTurnId,
                task.TaskId,
                stepId,
                "1"),
            OperationGeneration = 1,
        };
        var step = new NyxIdChatTaskStepState
        {
            StepId = stepId,
            Order = order,
            Kind = NyxIdChatStepKind.Postcondition,
            Status = NyxIdChatStepStatus.Planned,
            Required = true,
            Description = "Verify the typed NyxID action postcondition.",
            Source = new NyxIdChatStepSource
            {
                Postcondition = new NyxIdChatPostconditionStepSource
                {
                    ActionRequestId = request.ActionRequestId,
                    Check = request.Action.ToString(),
                },
            },
            ActionRequestId = request.ActionRequestId,
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            AddedBy = NyxIdChatStepAddedBy.Replan,
            DependsOn = { request.StepId },
            Operation = new NyxIdChatOperationState
            {
                Key = key,
                Kind = NyxIdChatStepKind.Postcondition,
                Phase = NyxIdChatOperationPhase.Requested,
                RequestedAt = now.Clone(),
            },
            UpdatedAt = now.Clone(),
        };
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        return step;
    }

    private static NyxIdChatTaskStepState BuildActionContinuationStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskState task,
        NyxIdChatActionRequestState request,
        NyxIdChatTaskStepState postconditionStep,
        int order,
        Timestamp now)
    {
        var stepId = BuildStableIdentity(
            "step",
            state.ConversationActorId,
            request.OriginTurnId,
            task.TaskId,
            request.ActionRequestId,
            "llm-continuation");
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = state.ConversationActorId,
            TurnId = request.OriginTurnId,
            TaskId = task.TaskId,
            StepId = stepId,
            OperationId = BuildStableIdentity(
                "operation",
                state.ConversationActorId,
                request.OriginTurnId,
                task.TaskId,
                stepId,
                "1"),
            OperationGeneration = 1,
        };
        var step = new NyxIdChatTaskStepState
        {
            StepId = stepId,
            Order = order,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Planned,
            Required = true,
            Description = "Continue the original request after verified NyxID authorization.",
            Source = new NyxIdChatStepSource
            {
                Llm = new NyxIdChatLLMStepSource
                {
                    ActionRequestId = request.ActionRequestId,
                    ResumeRequirement = ResolveAuthorizationResumeRequirement(
                        state.ActiveTurn,
                        request),
                },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            AddedBy = NyxIdChatStepAddedBy.Replan,
            DependsOn = { postconditionStep.StepId },
            Operation = new NyxIdChatOperationState
            {
                Key = key,
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Requested,
                RequestedAt = now.Clone(),
            },
            UpdatedAt = now.Clone(),
        };
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        return step;
    }

    private static NyxIdChatTaskStepState? FindSourceToolContinuation(
        NyxIdChatTaskState task,
        string sourceToolStepId)
    {
        if (string.IsNullOrWhiteSpace(sourceToolStepId))
            return null;

        return task.Steps.SingleOrDefault(step =>
            step.Kind == NyxIdChatStepKind.Llm &&
            step.Status == NyxIdChatStepStatus.Planned &&
            step.Source?.Llm is not null &&
            step.Operation?.Key is not null &&
            step.DependsOn.Count == 1 &&
            string.Equals(step.DependsOn[0], sourceToolStepId, StringComparison.Ordinal));
    }

    private static void CancelSupersededContinuation(
        NyxIdChatTaskStepState step,
        Timestamp now)
    {
        step.Required = false;
        step.Status = NyxIdChatStepStatus.Cancelled;
        step.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
        step.Operation.Phase = NyxIdChatOperationPhase.Cancelled;
        step.Operation.CompletedAt = now.Clone();
        step.UpdatedAt = now.Clone();
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
    }

    private static void CompleteSourceToolStep(
        NyxIdChatConversationGAgentState state,
        NyxIdChatActionRequestState request,
        Timestamp now)
    {
        if (!request.HasSourceToolStepId)
            return;

        var source = FindStep(state, request.SourceToolStepId);
        if (source?.Kind != NyxIdChatStepKind.Tool)
            return;

        source.Status = NyxIdChatStepStatus.Done;
        source.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
        source.FailureCode = string.Empty;
        source.SafeMessage = string.Empty;
        if (source.Operation is not null)
        {
            source.Operation.Phase = NyxIdChatOperationPhase.Succeeded;
            source.Operation.TerminalCode = string.Empty;
            source.Operation.SafeMessage = string.Empty;
            source.Operation.CompletedAt ??= now.Clone();
        }
        source.UpdatedAt = now.Clone();
        source.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(source);
    }

    private static NyxIdChatTaskStepState? FindPostconditionStep(
        NyxIdChatTaskState task,
        string actionRequestId) =>
        task.Steps.FirstOrDefault(step =>
            step.Kind == NyxIdChatStepKind.Postcondition &&
            string.Equals(step.ActionRequestId, actionRequestId, StringComparison.Ordinal));

    private static void ActivatePostcondition(
        NyxIdChatTaskState task,
        NyxIdChatTaskStepState step,
        Timestamp now)
    {
        step.Status = NyxIdChatStepStatus.Running;
        step.Operation.Phase = NyxIdChatOperationPhase.Requested;
        step.Operation.RequestedAt = now.Clone();
        step.UpdatedAt = now.Clone();
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        task.ActiveStepId = step.StepId;
        task.ActiveOperationId = step.Operation.Key.OperationId;
    }

    private static void CancelPostcondition(
        NyxIdChatTaskState task,
        string actionRequestId,
        NyxIdChatActionReport report,
        Timestamp now)
    {
        var step = FindPostconditionStep(task, actionRequestId);
        if (step is null)
            return;

        var (code, message) = DispositionFailure(report.Disposition);
        step.Status = report.Disposition == NyxIdChatActionDisposition.Failed
            ? NyxIdChatStepStatus.Failed
            : NyxIdChatStepStatus.Cancelled;
        step.Operation.Phase = report.Disposition == NyxIdChatActionDisposition.Failed
            ? NyxIdChatOperationPhase.Failed
            : NyxIdChatOperationPhase.Cancelled;
        step.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
        step.FailureCode = code;
        step.SafeMessage = message;
        step.UpdatedAt = now.Clone();
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
    }

    private static void ApplyReportedActionOutcome(
        NyxIdChatTaskState task,
        NyxIdChatActionRequestState request,
        NyxIdChatActionReport report,
        Timestamp now)
    {
        var actionStep = task.Steps.FirstOrDefault(step =>
            string.Equals(step.StepId, request.StepId, StringComparison.Ordinal));
        if (actionStep is null)
            return;

        var (code, message) = DispositionFailure(report.Disposition);
        actionStep.Status = report.Disposition is
            NyxIdChatActionDisposition.Declined or
            NyxIdChatActionDisposition.Cancelled or
            NyxIdChatActionDisposition.Expired
                ? NyxIdChatStepStatus.Cancelled
                : NyxIdChatStepStatus.Failed;
        actionStep.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
        actionStep.FailureCode = code;
        actionStep.SafeMessage = message;
        actionStep.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(actionStep);
        actionStep.UpdatedAt = now.Clone();
    }

    private static void CompleteContinuationTask(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskState task,
        NyxIdChatTurnState turn,
        Timestamp now)
    {
        task.ActiveStepId = string.Empty;
        task.ActiveOperationId = string.Empty;
        var failed = task.Steps.FirstOrDefault(static step =>
            step.Required && step.Status is
                NyxIdChatStepStatus.Failed or
                NyxIdChatStepStatus.Cancelled or
                NyxIdChatStepStatus.Uncertain);
        if (failed is null)
        {
            task.Status = NyxIdChatTaskStatus.Succeeded;
            task.FailureCode = string.Empty;
            task.SafeMessage = string.Empty;
            turn.Status = NyxIdChatTurnStatus.Succeeded;
            turn.FailureCode = string.Empty;
            turn.SafeMessage = string.Empty;
        }
        else
        {
            task.Status = NyxIdChatTaskStatus.Failed;
            task.FailureCode = failed.FailureCode;
            task.SafeMessage = failed.SafeMessage;
            turn.Status = NyxIdChatTurnStatus.Failed;
            turn.FailureCode = failed.FailureCode;
            turn.SafeMessage = failed.SafeMessage;
        }

        task.UpdatedAt = now.Clone();
        turn.TerminalAt = now.Clone();
        state.LatestTurn = turn.Clone();
        AddTerminalSummary(state, turn);
    }

    private static void BlockForPendingAction(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTaskState task,
        NyxIdChatTurnState turn,
        Timestamp now)
    {
        var pending = state.PendingActions
            .OrderBy(static request => request.ActionRequestId, StringComparer.Ordinal)
            .First();
        var pendingBlockedMessage = BlockedMessageForAction(pending.Action);
        task.Status = NyxIdChatTaskStatus.Blocked;
        task.ActiveStepId = pending.StepId;
        task.ActiveOperationId = string.Empty;
        task.FailureCode = ActionRequested;
        task.SafeMessage = pendingBlockedMessage;
        task.UpdatedAt = now.Clone();
        turn.Status = NyxIdChatTurnStatus.Blocked;
        turn.FailureCode = ActionRequested;
        turn.SafeMessage = pendingBlockedMessage;
        turn.TerminalAt = now.Clone();
        state.LatestTurn = turn.Clone();
        AddTerminalSummary(state, turn);
    }

    private static NyxIdChatOperationDispatchCommand BuildPostconditionCommand(
        string scopeId,
        string ownerSubject,
        NyxIdChatActionRequestState request,
        NyxIdChatActionReport? report,
        NyxIdChatOperationKey key,
        AgentToolExecutionContextPayload? toolContext = null) =>
        new()
        {
            Key = key.Clone(),
            ActionPostcondition = new NyxIdChatActionPostconditionInput
            {
                ScopeId = scopeId,
                OwnerSubject = ownerSubject,
                OriginTurnId = request.OriginTurnId,
                ActionRequestId = request.ActionRequestId,
                Action = request.Action,
                ReportedDisposition = report?.Disposition ?? NyxIdChatActionDisposition.Unspecified,
                ResourceHint = report?.Resource?.Clone(),
                Params = request.Params?.Clone(),
                RequestedAt = request.RequestedAt?.Clone(),
                ToolContext = toolContext?.Clone(),
            },
        };

    private static NyxIdChatOperationDispatchCommand? StartNextPlannedPostcondition(
        NyxIdChatConversationGAgentState state,
        Timestamp now)
    {
        var step = state.ActiveTask.Steps.FirstOrDefault(candidate =>
            candidate.Kind == NyxIdChatStepKind.Postcondition &&
            candidate.Status == NyxIdChatStepStatus.Planned &&
            FindAdmissionReport(
                state.ContinuationAdmission,
                candidate.ActionRequestId) is not null);
        if (step is null)
            return null;

        var request = state.PendingActions.First(action => string.Equals(
            action.ActionRequestId,
            step.ActionRequestId,
            StringComparison.Ordinal));
        var report = FindAdmissionReport(state.ContinuationAdmission, request.ActionRequestId);
        step.Status = NyxIdChatStepStatus.Running;
        step.Operation.Phase = NyxIdChatOperationPhase.Requested;
        step.Operation.RequestedAt = now.Clone();
        step.UpdatedAt = now.Clone();
        state.ActiveTask.Status = NyxIdChatTaskStatus.Active;
        state.ActiveTask.ActiveStepId = step.StepId;
        state.ActiveTask.ActiveOperationId = step.Operation.Key.OperationId;
        state.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
        state.ActiveTurn.TerminalAt = null;
        return BuildPostconditionCommand(
            state.ScopeId,
            state.ContinuationAdmission.OwnerSubject,
            request,
            report,
            step.Operation.Key);
    }

    private static NyxIdChatOperationDispatchCommand? StartActionContinuation(
        NyxIdChatConversationGAgentState state,
        NyxIdChatActionRequestState request,
        Timestamp now)
    {
        var step = state.ActiveTask.Steps.SingleOrDefault(candidate =>
            candidate.Kind == NyxIdChatStepKind.Llm &&
            candidate.Status == NyxIdChatStepStatus.Planned &&
            string.Equals(
                candidate.Source?.Llm?.ActionRequestId,
                request.ActionRequestId,
                StringComparison.Ordinal));
        if (step?.Operation?.Key is null ||
            !NyxIdChatActionContinuationCorrelation.TryMatch(
                state,
                state.ActiveTask,
                state.ActiveTurn,
                step.Operation.Key,
                out var correlation))
            return null;

        var verifiedAuthorization =
            NyxIdChatActionContinuationCorrelation.BuildVerifiedAuthorizationContinuation(
                correlation,
                now);

        step.Status = NyxIdChatStepStatus.Running;
        step.Operation.Phase = NyxIdChatOperationPhase.Requested;
        step.Operation.RequestedAt = now.Clone();
        step.UpdatedAt = now.Clone();
        state.ActiveTask.Status = NyxIdChatTaskStatus.Active;
        state.ActiveTask.ActiveStepId = step.StepId;
        state.ActiveTask.ActiveOperationId = step.Operation.Key.OperationId;
        state.ActiveTask.FailureCode = string.Empty;
        state.ActiveTask.SafeMessage = string.Empty;
        state.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
        state.ActiveTurn.FailureCode = string.Empty;
        state.ActiveTurn.SafeMessage = string.Empty;
        state.ActiveTurn.TerminalAt = null;
        return new NyxIdChatOperationDispatchCommand
        {
            Key = step.Operation.Key.Clone(),
            Llm = new NyxIdChatLLMOperationInput
            {
                ContinueSession = true,
                RematerializeTurnCatalog = true,
                AgentProfile = state.AgentProfile?.Clone(),
                AgentProfileTurnAuthority = state.ActiveTurn.AgentProfileTurnAuthority?.Clone(),
                Intent = state.ActiveTurn.Intent,
                VerifiedAuthorizationContinuation = verifiedAuthorization,
            },
        };
    }

    private static NyxIdChatAuthorizationResumeRequirement ResolveAuthorizationResumeRequirement(
        NyxIdChatTurnState? originTurn,
        NyxIdChatActionRequestState request)
    {
        var isConnectedServiceAuthorization = request.Action is
            NyxIdAssistantActionKind.ServiceConnect or
            NyxIdAssistantActionKind.ServiceAccessReview;
        return isConnectedServiceAuthorization &&
               originTurn?.Intent != NyxIdChatTurnIntent.ServiceConnect
            ? NyxIdChatAuthorizationResumeRequirement.CompleteOriginalServiceRequest
            : NyxIdChatAuthorizationResumeRequirement.CommunicateAuthorizationCompletion;
    }

    private static NyxIdChatOperationDispatchCommand? TryBuildReplayDispatch(
        NyxIdChatConversationGAgentState state,
        NyxIdChatContinuationAdmissionState admission,
        AgentToolExecutionContextPayload? toolContext = null)
    {
        var step = state.ActiveTask?.Steps.FirstOrDefault(candidate =>
            candidate.Kind == NyxIdChatStepKind.Postcondition &&
            candidate.Status == NyxIdChatStepStatus.Running &&
            candidate.Operation?.Phase == NyxIdChatOperationPhase.Requested);
        if (step is null)
            return null;
        var request = state.PendingActions.FirstOrDefault(candidate => string.Equals(
            candidate.ActionRequestId,
            step.ActionRequestId,
            StringComparison.Ordinal));
        var report = request is null
            ? null
            : FindAdmissionReport(admission, request.ActionRequestId);
        return request is null
            ? null
            : BuildPostconditionCommand(
                state.ScopeId,
                admission.OwnerSubject,
                request,
                report,
                step.Operation.Key,
                toolContext);
    }

    internal static NyxIdChatOperationDispatchCommand? TryBuildRecoveryDispatch(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationKey key)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(key);

        if (state.ActiveTurn?.Status != NyxIdChatTurnStatus.Active ||
            state.ActiveTask?.Status != NyxIdChatTaskStatus.Active ||
            state.ContinuationAdmission is not
            {
                Kind: NyxIdChatContinuationKind.Action,
                Status: NyxIdChatContinuationAdmissionStatus.Accepted,
                OwnerSubject.Length: > 0,
            } admission)
        {
            return null;
        }

        var step = state.ActiveTask.Steps.FirstOrDefault(candidate =>
            candidate.Kind == NyxIdChatStepKind.Postcondition &&
            candidate.Status == NyxIdChatStepStatus.Running &&
            candidate.Operation?.Phase == NyxIdChatOperationPhase.Requested &&
            KeysEqual(candidate.Operation.Key, key));
        if (step is null ||
            !string.Equals(state.ActiveTask.TaskId, key.TaskId, StringComparison.Ordinal) ||
            !string.Equals(
                admission.ContinuationTurnId,
                state.ActiveTurn.TurnId,
                StringComparison.Ordinal))
        {
            return null;
        }

        var request = state.PendingActions.FirstOrDefault(candidate => string.Equals(
            candidate.ActionRequestId,
            step.ActionRequestId,
            StringComparison.Ordinal));
        if (request is null ||
            !string.Equals(request.OriginTurnId, key.TurnId, StringComparison.Ordinal))
        {
            return null;
        }
        var report = request is null
            ? null
            : FindAdmissionReport(admission, request.ActionRequestId);
        return request is null
            ? null
            : BuildPostconditionCommand(
                state.ScopeId,
                admission.OwnerSubject,
                request,
                report,
                key);
    }

    private static bool RequestMatchesState(
        NyxIdChatConversationGAgentState state,
        NyxIdChatActionRequestState request) =>
        state.ActiveTurn is not null &&
        state.ActiveTask is not null &&
        request.SchemaVersion == NyxIdAssistantActionRegistry.SupportedSchemaVersion &&
        NyxIdAssistantActionRegistry.IsActionExecutable(
            request.RegistryRevision,
            request.Action) &&
        RequestParamsMatchAction(request) &&
        !string.IsNullOrWhiteSpace(request.ActionRequestId) &&
        !string.IsNullOrWhiteSpace(request.StepId) &&
        (!request.HasSourceToolStepId ||
         !string.IsNullOrWhiteSpace(request.SourceToolStepId)) &&
        string.Equals(state.ConversationActorId, request.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn.TurnId, request.OriginTurnId, StringComparison.Ordinal) &&
        string.Equals(state.ActiveTask.TaskId, request.TaskId, StringComparison.Ordinal) &&
        SourceToolMatchesState(state, request);

    private static bool SourceToolMatchesState(
        NyxIdChatConversationGAgentState state,
        NyxIdChatActionRequestState request)
    {
        if (!request.HasSourceToolStepId)
            return true;

        var source = FindStep(state, request.SourceToolStepId);
        return source is
               {
                   Kind: NyxIdChatStepKind.Tool,
                   Status: NyxIdChatStepStatus.Waiting,
                   Operation.Key: not null,
               } &&
               string.Equals(
                   source.Operation.Key.ConversationActorId,
                   request.ConversationActorId,
                   StringComparison.Ordinal) &&
               string.Equals(source.Operation.Key.TurnId, request.OriginTurnId, StringComparison.Ordinal) &&
               string.Equals(source.Operation.Key.TaskId, request.TaskId, StringComparison.Ordinal);
    }

    private static bool RequestParamsMatchAction(NyxIdChatActionRequestState request) =>
        request.Action switch
        {
            NyxIdAssistantActionKind.ServiceConnect => request.Params?.ParamsCase is
                NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect or
                NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect,
            NyxIdAssistantActionKind.ServiceAccessReview =>
                IsValidServiceAccessReviewParams(request.Params?.ServiceAccessReview),
            NyxIdAssistantActionKind.KeyCreate => IsValidKeyCreateParams(request.Params?.KeyCreate),
            NyxIdAssistantActionKind.KeyRotate => IsValidKeyRotateParams(request.Params?.KeyRotate),
            _ => false,
        };

    private static bool IsValidKeyCreateParams(NyxIdKeyCreateParams? value)
    {
        if (value is null ||
            !IsNormalizedActionValue(value.Name, 256) ||
            !IsNormalizedActionValue(value.Platform, 128) ||
            value.AllowedServiceIds.Count is < 1 or > 64)
        {
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        return value.AllowedServiceIds.All(id =>
            IsNormalizedActionValue(id, 256) && ids.Add(id));
    }

    private static bool IsValidServiceAccessReviewParams(
        NyxIdServiceAccessReviewParams? value)
    {
        if (value is null ||
            !IsNormalizedActionValue(value.UserServiceId, 256) ||
            value.UserServiceId.Any(char.IsWhiteSpace) ||
            !IsNormalizedActionValue(value.ServiceSlug, 128) ||
            !value.ServiceSlug.All(static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.') ||
            !IsNormalizedActionValue(value.ResourceUri, 512) ||
            !Uri.TryCreate(value.ResourceUri, UriKind.Absolute, out var resourceUri) ||
            !string.Equals(resourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(resourceUri.UserInfo) ||
            !string.IsNullOrEmpty(resourceUri.Query) ||
            !string.IsNullOrEmpty(resourceUri.Fragment))
        {
            return false;
        }

        return resourceUri.AbsolutePath.EndsWith(
            $"/api/v1/proxy/s/{Uri.EscapeDataString(value.ServiceSlug)}",
            StringComparison.Ordinal);
    }

    private static bool IsValidKeyRotateParams(NyxIdKeyRotateParams? value) =>
        value is not null &&
        IsNormalizedActionValue(value.KeyId, 256) &&
        !value.KeyId.Any(char.IsWhiteSpace) &&
        !value.KeyId.Any(static character => character is '/' or '\\' or '?' or '#');

    private static bool IsNormalizedActionValue(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maxLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static NyxIdChatActionReport? FindAdmissionReport(
        NyxIdChatContinuationAdmissionState admission,
        string actionRequestId) =>
        admission.ActionReports.FirstOrDefault(report =>
            string.Equals(report.ActionRequestId, actionRequestId, StringComparison.Ordinal) &&
            report.Disposition == NyxIdChatActionDisposition.Completed);

    private static bool ValidContinuationIdentity(
        NyxIdChatConversationGAgentState state,
        NyxIdChatActionContinueCommand command,
        bool isStateChangeWake) =>
        !string.IsNullOrWhiteSpace(command.ScopeId) &&
        !string.IsNullOrWhiteSpace(command.ConversationActorId) &&
        (isStateChangeWake
            ? string.IsNullOrWhiteSpace(command.OriginTurnId)
            : !string.IsNullOrWhiteSpace(command.OriginTurnId)) &&
        !string.IsNullOrWhiteSpace(command.ContinuationTurnId) &&
        !string.IsNullOrWhiteSpace(command.OwnerSubject) &&
        !string.IsNullOrWhiteSpace(command.ClientRequestId) &&
        !string.IsNullOrWhiteSpace(command.CommandId) &&
        !string.IsNullOrWhiteSpace(command.CorrelationId) &&
        string.Equals(state.ScopeId, command.ScopeId.Trim(), StringComparison.Ordinal) &&
        string.Equals(
            state.ConversationActorId,
            command.ConversationActorId.Trim(),
            StringComparison.Ordinal);

    private static bool ValidReport(NyxIdChatActionReport report, string originTurnId) =>
        !string.IsNullOrWhiteSpace(report.ActionRequestId) &&
        !string.IsNullOrWhiteSpace(report.OriginTurnId) &&
        string.Equals(report.OriginTurnId.Trim(), originTurnId.Trim(), StringComparison.Ordinal) &&
        report.Disposition is
            NyxIdChatActionDisposition.Completed or
            NyxIdChatActionDisposition.Declined or
            NyxIdChatActionDisposition.Failed or
            NyxIdChatActionDisposition.Cancelled or
            NyxIdChatActionDisposition.Expired &&
        ValidResource(report.Resource);

    private static bool ValidResource(NyxIdChatSafeResourceRef? resource) =>
        resource is null || resource.ResourceCase switch
        {
            NyxIdChatSafeResourceRef.ResourceOneofCase.None => true,
            NyxIdChatSafeResourceRef.ResourceOneofCase.UserService =>
                ValidId(resource.UserService.UserServiceId),
            NyxIdChatSafeResourceRef.ResourceOneofCase.Key => ValidId(resource.Key.KeyId),
            NyxIdChatSafeResourceRef.ResourceOneofCase.Node => ValidId(resource.Node.NodeId),
            NyxIdChatSafeResourceRef.ResourceOneofCase.ServiceAccount =>
                ValidId(resource.ServiceAccount.ServiceAccountId),
            NyxIdChatSafeResourceRef.ResourceOneofCase.DeveloperApp =>
                ValidId(resource.DeveloperApp.ClientId),
            NyxIdChatSafeResourceRef.ResourceOneofCase.Device => ValidId(resource.Device.DeviceId),
            _ => false,
        };

    internal static bool ResourceMatchesAction(
        NyxIdAssistantActionKind action,
        NyxIdChatActionDisposition disposition,
        NyxIdChatSafeResourceRef? resource)
    {
        if (resource is null ||
            resource.ResourceCase == NyxIdChatSafeResourceRef.ResourceOneofCase.None)
        {
            return disposition != NyxIdChatActionDisposition.Completed;
        }

        return action switch
        {
            NyxIdAssistantActionKind.ServiceConnect or
            NyxIdAssistantActionKind.ServiceReauthorize or
            NyxIdAssistantActionKind.ServiceAccessReview =>
                resource.ResourceCase == NyxIdChatSafeResourceRef.ResourceOneofCase.UserService,
            NyxIdAssistantActionKind.KeyCreate or
            NyxIdAssistantActionKind.KeyRotate =>
                resource.ResourceCase == NyxIdChatSafeResourceRef.ResourceOneofCase.Key,
            _ => false,
        };
    }

    private static bool PostconditionResourceMatchesAction(
        NyxIdAssistantActionKind action,
        NyxIdChatActionPostconditionResult result) =>
        (!result.Verified &&
         (result.Resource is null ||
          result.Resource.ResourceCase == NyxIdChatSafeResourceRef.ResourceOneofCase.None)) ||
        ResourceMatchesAction(action, result.Disposition, result.Resource);

    private static bool ValidId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value == value.Trim() &&
        !value.Any(char.IsControl);

    private static NyxIdChatActionReport SanitizeReport(
        NyxIdChatActionReport report,
        Timestamp now) =>
        new()
        {
            ActionRequestId = Normalize(report.ActionRequestId),
            OriginTurnId = Normalize(report.OriginTurnId),
            Disposition = report.Disposition,
            Resource = report.Resource?.Clone(),
            SafeMessage = string.Empty,
            ReportedAt = now.Clone(),
        };

    private static bool AdmissionMatches(
        NyxIdChatContinuationAdmissionState admission,
        NyxIdChatActionContinueCommand command,
        IReadOnlyList<NyxIdChatActionReport> sanitizedReports)
    {
        if (!string.Equals(admission.OriginTurnId, Normalize(command.OriginTurnId), StringComparison.Ordinal) ||
            !string.Equals(admission.ContinuationTurnId, Normalize(command.ContinuationTurnId), StringComparison.Ordinal) ||
            !string.Equals(admission.OwnerSubject, Normalize(command.OwnerSubject), StringComparison.Ordinal) ||
            admission.ActionReports.Count != sanitizedReports.Count)
        {
            return false;
        }

        return admission.ActionReports
            .Zip(sanitizedReports, ReportsEqual)
            .All(static equal => equal);
    }

    private static bool TryNormalizeReports(
        IEnumerable<NyxIdChatActionReport> reports,
        Timestamp now,
        out NyxIdChatActionReport[] normalized)
    {
        var byIdentity = new Dictionary<string, NyxIdChatActionReport>(StringComparer.Ordinal);
        foreach (var report in reports)
        {
            var sanitized = SanitizeReport(report, now);
            if (byIdentity.TryGetValue(sanitized.ActionRequestId, out var existing))
            {
                if (!ReportsEqual(existing, sanitized))
                {
                    normalized = [];
                    return false;
                }

                continue;
            }

            byIdentity.Add(sanitized.ActionRequestId, sanitized);
        }

        normalized = byIdentity.Values
            .OrderBy(static report => report.ActionRequestId, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    private static bool ReportsEqual(NyxIdChatActionReport left, NyxIdChatActionReport right) =>
        string.Equals(left.ActionRequestId, right.ActionRequestId, StringComparison.Ordinal) &&
        string.Equals(left.OriginTurnId, right.OriginTurnId, StringComparison.Ordinal) &&
        left.Disposition == right.Disposition &&
        ResourceEqual(left.Resource, right.Resource);

    private static bool ResourceEqual(
        NyxIdChatSafeResourceRef? left,
        NyxIdChatSafeResourceRef? right) =>
        left is null && right is null ||
        left is not null && right is not null && left.ToByteString().Equals(right.ToByteString());

    private static bool RequestContentEqual(
        NyxIdChatActionRequestState left,
        NyxIdChatActionRequestState right) =>
        left.SchemaVersion == right.SchemaVersion &&
        string.Equals(left.RegistryRevision, right.RegistryRevision, StringComparison.Ordinal) &&
        string.Equals(left.ConversationActorId, right.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(left.OriginTurnId, right.OriginTurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) &&
        string.Equals(left.ActionRequestId, right.ActionRequestId, StringComparison.Ordinal) &&
        left.Action == right.Action &&
        EqualsBytes(left.Params, right.Params) &&
        left.AdvisoryRisk == right.AdvisoryRisk &&
        left.RememberEligible == right.RememberEligible &&
        left.HasSourceToolStepId == right.HasSourceToolStepId &&
        string.Equals(left.SourceToolStepId, right.SourceToolStepId, StringComparison.Ordinal);

    private static bool EqualsBytes(IMessage? left, IMessage? right) =>
        left is null && right is null ||
        left is not null && right is not null && left.ToByteString().Equals(right.ToByteString());

    private static NyxIdChatActionRequestState? FindRequestForReports(
        NyxIdChatConversationGAgentState state,
        IEnumerable<NyxIdChatActionReport> reports)
    {
        var id = reports.FirstOrDefault()?.ActionRequestId;
        return state.PendingActions.Concat(state.RecentActions).FirstOrDefault(action =>
            string.Equals(action.ActionRequestId, id, StringComparison.Ordinal));
    }

    private static void MoveActionToRecent(
        NyxIdChatConversationGAgentState state,
        string actionRequestId)
    {
        var index = -1;
        for (var current = 0; current < state.PendingActions.Count; current++)
        {
            if (string.Equals(
                    state.PendingActions[current].ActionRequestId,
                    actionRequestId,
                    StringComparison.Ordinal))
            {
                index = current;
                break;
            }
        }

        if (index < 0)
            return;
        var action = state.PendingActions[index].Clone();
        state.PendingActions.RemoveAt(index);
        var existing = state.RecentActions.FirstOrDefault(candidate => string.Equals(
            candidate.ActionRequestId,
            actionRequestId,
            StringComparison.Ordinal));
        if (existing is null)
            state.RecentActions.Add(action);
        else
            existing.MergeFrom(action);
        while (state.RecentActions.Count > HistoryLimit)
            state.RecentActions.RemoveAt(0);
    }

    private static void AddTerminalSummary(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTurnState turn)
    {
        var summary = state.RecentTerminalTurns.FirstOrDefault(candidate => string.Equals(
            candidate.TurnId,
            turn.TurnId,
            StringComparison.Ordinal));
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
        while (state.RecentTerminalTurns.Count > HistoryLimit)
            state.RecentTerminalTurns.RemoveAt(0);
    }

    private static NyxIdChatTaskStepState? FindStep(
        NyxIdChatConversationGAgentState state,
        string? stepId) =>
        state.ActiveTask?.Steps.FirstOrDefault(step => string.Equals(
            step.StepId,
            stepId,
            StringComparison.Ordinal));

    private static bool KeysEqual(NyxIdChatOperationKey? left, NyxIdChatOperationKey? right) =>
        left is not null &&
        right is not null &&
        string.Equals(left.ConversationActorId, right.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) &&
        string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) &&
        left.OperationGeneration == right.OperationGeneration;

    private static (string Code, string Message) DispositionFailure(
        NyxIdChatActionDisposition disposition) => disposition switch
    {
        NyxIdChatActionDisposition.Declined =>
            ("NYXID_ACTION_DECLINED", "The NyxID action was declined."),
        NyxIdChatActionDisposition.Cancelled =>
            ("NYXID_ACTION_CANCELLED", "The NyxID action was cancelled."),
        NyxIdChatActionDisposition.Expired =>
            ("NYXID_ACTION_EXPIRED", "The NyxID action expired."),
        _ => ("NYXID_ACTION_FAILED", "The NyxID action failed."),
    };

    private static NyxIdChatBrowserActionDecision Reject(
        NyxIdChatConversationGAgentState state,
        string code,
        NyxIdChatActionRequestState? request = null) =>
        new(
            false,
            false,
            NyxIdChatTransitionOutcome.Rejected,
            code,
            string.Empty,
            state.Clone(),
            request?.Clone() ?? new NyxIdChatActionRequestState(),
            new NyxIdChatContinuationAdmissionState(),
            null);

    private static NyxIdChatBrowserActionDecision RejectContinuation(
        NyxIdChatConversationGAgentState state,
        string code) =>
        new(
            false,
            false,
            NyxIdChatTransitionOutcome.Rejected,
            code,
            string.Empty,
            state.Clone(),
            new NyxIdChatActionRequestState(),
            state.ContinuationAdmission?.Clone() ?? new NyxIdChatContinuationAdmissionState(),
            null);

    private static NyxIdChatBrowserActionDecision NoCommit(
        NyxIdChatConversationGAgentState state,
        NyxIdChatTransitionOutcome outcome,
        string reasonCode,
        NyxIdChatActionRequestState request) =>
        new(
            false,
            false,
            outcome,
            reasonCode,
            string.Empty,
            state.Clone(),
            request.Clone(),
            new NyxIdChatContinuationAdmissionState(),
            null);

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static string NormalizeFailureCode(string? value, string fallback)
    {
        var normalized = Normalize(value);
        return normalized.Length is > 0 and <= 128 &&
               normalized.All(static character =>
                   char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? normalized
            : fallback;
    }

    private static string NormalizeSafeMessage(string? value, string fallback)
    {
        var normalized = Normalize(value);
        return normalized.Length is > 0 and <= 512 && !normalized.Any(char.IsControl)
            ? normalized
            : fallback;
    }

    private static string BuildStableIdentity(string prefix, params string[] parts)
    {
        var identity = string.Concat(parts.Select(static part => $"{part.Length}:{part}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"{prefix}-{Convert.ToHexStringLower(hash)[..32]}";
    }
}
