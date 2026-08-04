using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

/// <summary>
/// Copies safe, query-shaped state from the canonical NyxIdChat conversation
/// actor into one actor-scoped current-state document. This projector does not
/// read an earlier document or reconstruct business state.
/// </summary>
public sealed class NyxIdChatConversationCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<NyxIdChatConversationCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public NyxIdChatConversationCurrentStateProjector(
        IProjectionWriteDispatcher<NyxIdChatConversationCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        StudioMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<NyxIdChatConversationGAgentState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null ||
            string.IsNullOrWhiteSpace(state.ConversationActorId) ||
            string.IsNullOrWhiteSpace(state.ScopeId))
        {
            return;
        }

        if (!string.Equals(
                context.RootActorId,
                state.ConversationActorId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "NyxIdChat conversation projection actor identity does not match the committed state root.");
        }

        // OwnerSubject is actor-only authority and must never enter this public document.
        var document = new NyxIdChatConversationCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(
                CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow)),
            ConversationActorId = state.ConversationActorId,
            ScopeId = state.ScopeId,
            ProgressSequence = state.ProgressSequence,
            ActiveTurn = ToTurn(state.ActiveTurn),
            LatestTurn = ToTurn(state.LatestTurn),
            ActiveTask = ToTask(state.ActiveTask),
            PendingApproval = ToPendingApproval(state.PendingApproval),
            PendingInput = ToPendingInput(state.PendingInput),
            LatestInputResolution = ToInputResolution(state.LatestInputResolution),
            LatestApprovalResolution = ToApprovalResolution(state.LatestApprovalResolution),
            TaskStatus = ToWireName(state.Attention?.TaskStatus ?? NyxIdChatTaskStatus.Unspecified),
            AttentionKind = ToWireName(state.Attention?.AttentionKind ?? NyxIdChatAttentionKind.Unspecified),
            AttentionSince = state.Attention?.AttentionSince?.Clone(),
            ActiveStepSummary = state.Attention?.ActiveStepSummary ?? string.Empty,
            ControlFence = ToControlFence(state.ControlFence),
            LatestControlResult = ToControlFence(state.LatestControlResult),
            ContinuationAdmission = ToContinuationAdmission(state.ContinuationAdmission),
        };
        document.RecentTerminalTurns.AddRange(state.RecentTerminalTurns.Select(ToTurn));
        document.PendingActions.AddRange(state.PendingActions.Select(ToAction));

        var result = await _writeDispatcher.UpsertAsync(document, ct).ConfigureAwait(false);
        if (result.IsRejected)
        {
            throw new InvalidOperationException(
                $"NyxIdChat conversation projection rejected state version {stateEvent.Version}: " +
                $"{result.Disposition}.");
        }
    }

    private static NyxIdChatConversationTurnDocument? ToTurn(NyxIdChatTurnState? turn) =>
        turn == null
            ? null
            : new NyxIdChatConversationTurnDocument
            {
                TurnId = turn.TurnId,
                TaskId = turn.TaskId,
                Status = ToWireName(turn.Status),
                FailureCode = turn.FailureCode,
                SafeMessage = turn.SafeMessage,
                CreatedAt = turn.CreatedAt?.Clone(),
                TerminalAt = turn.TerminalAt?.Clone(),
                CommandId = turn.CommandId,
            };

    private static NyxIdChatConversationTurnDocument ToTurn(NyxIdChatTurnSummary turn) =>
        new()
        {
            TurnId = turn.TurnId,
            TaskId = turn.TaskId,
            Status = ToWireName(turn.Status),
            FailureCode = turn.FailureCode,
            SafeMessage = turn.SafeMessage,
            TerminalAt = turn.TerminalAt?.Clone(),
        };

    private static NyxIdChatConversationTaskDocument? ToTask(NyxIdChatTaskState? task)
    {
        if (task == null)
            return null;

        var document = new NyxIdChatConversationTaskDocument
        {
            TaskId = task.TaskId,
            TurnId = task.TurnId,
            Status = ToWireName(task.Status),
            ActiveStepId = task.ActiveStepId,
            ActiveOperationId = task.ActiveOperationId,
            FailureCode = task.FailureCode,
            SafeMessage = task.SafeMessage,
            CreatedAt = task.CreatedAt?.Clone(),
            UpdatedAt = task.UpdatedAt?.Clone(),
        };
        document.Steps.AddRange(task.Steps
            .OrderBy(static step => step.Order)
            .ThenBy(static step => step.StepId, StringComparer.Ordinal)
            .Select(ToStep));
        return document;
    }

    private static NyxIdChatConversationStepDocument ToStep(NyxIdChatTaskStepState step) =>
        new()
        {
            StepId = step.StepId,
            Order = step.Order,
            Kind = ToWireName(step.Kind),
            Status = ToWireName(step.Status),
            Required = step.Required,
            Description = step.Description,
            MayChangeExternalState = step.MayChangeExternalState,
            ExternalEffect = ToWireName(step.ExternalEffect),
            ApprovalRequestId = step.ApprovalRequestId,
            ActionRequestId = step.ActionRequestId,
            FailureCode = step.FailureCode,
            SafeMessage = step.SafeMessage,
            SafeToSkip = step.SafeToSkip,
            AvailableActions = ToAvailableActions(step.AvailableActions),
            UpdatedAt = step.UpdatedAt?.Clone(),
            Operation = ToOperation(step.Operation),
        };

    private static NyxIdChatConversationAvailableActionsDocument? ToAvailableActions(
        NyxIdChatAvailableActions? actions) =>
        actions == null
            ? null
            : new NyxIdChatConversationAvailableActionsDocument
            {
                Retry = actions.Retry,
                Skip = actions.Skip,
                Stop = actions.Stop,
            };

    private static NyxIdChatConversationOperationDocument? ToOperation(
        NyxIdChatOperationState? operation)
    {
        if (operation?.Key == null)
            return null;

        return new NyxIdChatConversationOperationDocument
        {
            ConversationActorId = operation.Key.ConversationActorId,
            TurnId = operation.Key.TurnId,
            TaskId = operation.Key.TaskId,
            StepId = operation.Key.StepId,
            OperationId = operation.Key.OperationId,
            OperationGeneration = operation.Key.OperationGeneration,
            Kind = ToWireName(operation.Kind),
            Phase = ToWireName(operation.Phase),
            MayChangeExternalState = operation.MayChangeExternalState,
            Idempotent = operation.Idempotent,
            LatestProgressSequence = operation.LatestProgressSequence,
            TerminalCode = operation.TerminalCode,
            SafeMessage = operation.SafeMessage,
            RequestedAt = operation.RequestedAt?.Clone(),
            DispatchedAt = operation.DispatchedAt?.Clone(),
            CompletedAt = operation.CompletedAt?.Clone(),
        };
    }

    private static NyxIdChatConversationPendingApprovalDocument? ToPendingApproval(
        NyxIdChatPendingApprovalState? approval) =>
        approval == null
            ? null
            : new NyxIdChatConversationPendingApprovalDocument
            {
                ApprovalRequestId = approval.ApprovalRequestId,
                TurnId = approval.TurnId,
                TaskId = approval.TaskId,
                StepId = approval.StepId,
                ToolName = approval.ToolName,
                ExpiresAt = approval.ExpiresAt?.Clone(),
                AskedAt = approval.AskedAt?.Clone(),
                Action = approval.Presentation?.Action ?? string.Empty,
                Target = approval.Presentation?.Target ?? string.Empty,
                ActorLabel = approval.Presentation?.ActorLabel ?? string.Empty,
                Reversibility = ToWireName(
                    approval.Presentation?.Reversibility ??
                    NyxIdChatApprovalReversibility.Unspecified),
                GrantBoundary = approval.Presentation?.GrantBoundary ?? string.Empty,
                NyxidRequestId = approval.Presentation?.NyxidRequestId ?? string.Empty,
            };

    private static NyxIdChatConversationPendingInputDocument? ToPendingInput(
        NyxIdChatPendingInputState? input)
    {
        if (input is null)
            return null;

        var document = new NyxIdChatConversationPendingInputDocument
        {
            RequestId = input.RequestId,
            TurnId = input.TurnId,
            TaskId = input.TaskId,
            StepId = input.StepId,
            Prompt = input.Prompt,
            AskedAt = input.AskedAt?.Clone(),
            AllowFreeText = input.AllowFreeText,
            MultiSelect = input.MultiSelect,
        };
        document.Options.AddRange(input.Options.Select(static option =>
            new NyxIdChatConversationInputOptionDocument
            {
                OptionId = option.OptionId,
                Label = option.Label,
                Description = option.Description,
            }));
        return document;
    }

    private static NyxIdChatConversationInputResolutionDocument? ToInputResolution(
        NyxIdChatInputResolutionState? resolution) =>
        resolution is null
            ? null
            : new NyxIdChatConversationInputResolutionDocument
            {
                RequestId = resolution.RequestId,
                ClientRequestId = resolution.ClientRequestId,
                Outcome = ToWireName(resolution.Outcome),
                CommittedAt = resolution.CommittedAt?.Clone(),
            };

    private static NyxIdChatConversationApprovalResolutionDocument? ToApprovalResolution(
        NyxIdChatApprovalResolutionState? resolution) =>
        resolution is null
            ? null
            : new NyxIdChatConversationApprovalResolutionDocument
            {
                RequestId = resolution.RequestId,
                ClientRequestId = resolution.ClientRequestId,
                Outcome = ToWireName(resolution.Outcome),
                Approved = resolution.Approved,
                CommittedAt = resolution.CommittedAt?.Clone(),
            };

    private static NyxIdChatConversationControlFenceDocument? ToControlFence(
        NyxIdChatControlFenceState? fence) =>
        fence == null
            ? null
            : new NyxIdChatConversationControlFenceDocument
            {
                Kind = ToWireName(fence.Kind),
                RequestId = fence.RequestId,
                ClientRequestId = fence.ClientRequestId,
                TurnId = fence.TurnId,
                TaskId = fence.TaskId,
                OperationGeneration = fence.OperationGeneration,
                Outcome = ToWireName(fence.Outcome),
                ReasonCode = fence.ReasonCode,
                SafeMessage = fence.SafeMessage,
                CommittedAt = fence.CommittedAt?.Clone(),
            };

    private static NyxIdChatConversationContinuationAdmissionDocument? ToContinuationAdmission(
        NyxIdChatContinuationAdmissionState? admission) =>
        admission == null
            ? null
            : new NyxIdChatConversationContinuationAdmissionDocument
            {
                Kind = ToWireName(admission.Kind),
                RequestId = admission.RequestId,
                ClientRequestId = admission.ClientRequestId,
                OriginTurnId = admission.OriginTurnId,
                ContinuationTurnId = admission.ContinuationTurnId,
                Status = ToWireName(admission.Status),
                ReasonCode = admission.ReasonCode,
                SafeMessage = admission.SafeMessage,
                CommittedAt = admission.CommittedAt?.Clone(),
            };

    private static NyxIdChatConversationActionDocument ToAction(
        NyxIdChatActionRequestState action)
    {
        var document = new NyxIdChatConversationActionDocument
        {
            SchemaVersion = action.SchemaVersion,
            ActionRequestId = action.ActionRequestId,
            OriginTurnId = action.OriginTurnId,
            TaskId = action.TaskId,
            StepId = action.StepId,
            Action = ToWireName(action.Action),
            RequestedAt = action.RequestedAt?.Clone(),
            PostconditionResult = ToPostcondition(action.PostconditionResult),
        };
        document.Reports.AddRange(action.Reports.Select(ToActionReport));
        return document;
    }

    private static NyxIdChatConversationActionReportDocument ToActionReport(
        NyxIdChatActionReport report) =>
        new()
        {
            ActionRequestId = report.ActionRequestId,
            OriginTurnId = report.OriginTurnId,
            Disposition = ToWireName(report.Disposition),
            Resource = ToResource(report.Resource),
            SafeMessage = report.SafeMessage,
            ReportedAt = report.ReportedAt?.Clone(),
        };

    private static NyxIdChatConversationActionPostconditionDocument? ToPostcondition(
        NyxIdChatActionPostconditionResult? result) =>
        result == null
            ? null
            : new NyxIdChatConversationActionPostconditionDocument
            {
                ActionRequestId = result.ActionRequestId,
                Disposition = ToWireName(result.Disposition),
                Verified = result.Verified,
                Resource = ToResource(result.Resource),
                FailureCode = result.FailureCode,
                SafeMessage = result.SafeMessage,
            };

    private static NyxIdChatConversationResourceDocument? ToResource(
        NyxIdChatSafeResourceRef? resource)
    {
        if (resource == null)
            return null;

        return resource.ResourceCase switch
        {
            NyxIdChatSafeResourceRef.ResourceOneofCase.UserService => new()
            {
                UserServiceId = resource.UserService.UserServiceId,
            },
            NyxIdChatSafeResourceRef.ResourceOneofCase.Key => new()
            {
                KeyId = resource.Key.KeyId,
            },
            NyxIdChatSafeResourceRef.ResourceOneofCase.Node => new()
            {
                NodeId = resource.Node.NodeId,
            },
            NyxIdChatSafeResourceRef.ResourceOneofCase.ServiceAccount => new()
            {
                ServiceAccountId = resource.ServiceAccount.ServiceAccountId,
            },
            NyxIdChatSafeResourceRef.ResourceOneofCase.DeveloperApp => new()
            {
                ClientId = resource.DeveloperApp.ClientId,
            },
            NyxIdChatSafeResourceRef.ResourceOneofCase.Device => new()
            {
                DeviceId = resource.Device.DeviceId,
            },
            _ => null,
        };
    }

    private static string ToWireName(NyxIdChatTurnStatus status) => status switch
    {
        NyxIdChatTurnStatus.Active => "active",
        NyxIdChatTurnStatus.Succeeded => "succeeded",
        NyxIdChatTurnStatus.Failed => "failed",
        NyxIdChatTurnStatus.Stopped => "stopped",
        NyxIdChatTurnStatus.Blocked => "blocked",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatTaskStatus status) => status switch
    {
        NyxIdChatTaskStatus.Active => "active",
        NyxIdChatTaskStatus.Succeeded => "succeeded",
        NyxIdChatTaskStatus.Failed => "failed",
        NyxIdChatTaskStatus.Stopped => "stopped",
        NyxIdChatTaskStatus.Blocked => "blocked",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatStepStatus status) => status switch
    {
        NyxIdChatStepStatus.Planned => "planned",
        NyxIdChatStepStatus.Waiting => "waiting",
        NyxIdChatStepStatus.Running => "running",
        NyxIdChatStepStatus.Done => "done",
        NyxIdChatStepStatus.Failed => "failed",
        NyxIdChatStepStatus.Skipped => "skipped",
        NyxIdChatStepStatus.Cancelled => "cancelled",
        NyxIdChatStepStatus.Uncertain => "uncertain",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatEffectEvidence evidence) => evidence switch
    {
        NyxIdChatEffectEvidence.NotStarted => "not_started",
        NyxIdChatEffectEvidence.NotApplied => "not_applied",
        NyxIdChatEffectEvidence.Confirmed => "confirmed",
        NyxIdChatEffectEvidence.MayHaveChanged => "may_have_changed",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatStepKind kind) => kind switch
    {
        NyxIdChatStepKind.Llm => "llm",
        NyxIdChatStepKind.Tool => "tool",
        NyxIdChatStepKind.BrowserAction => "browser_action",
        NyxIdChatStepKind.Postcondition => "postcondition",
        NyxIdChatStepKind.Input => "input",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatAttentionKind kind) => kind switch
    {
        NyxIdChatAttentionKind.None => "none",
        NyxIdChatAttentionKind.Input => "input",
        NyxIdChatAttentionKind.Approval => "approval",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatApprovalReversibility value) => value switch
    {
        NyxIdChatApprovalReversibility.Reversible => "reversible",
        NyxIdChatApprovalReversibility.Irreversible => "irreversible",
        NyxIdChatApprovalReversibility.Unknown => "unknown",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatNeedsYouResolutionOutcome outcome) => outcome switch
    {
        NyxIdChatNeedsYouResolutionOutcome.Accepted => "accepted",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatOperationPhase phase) => phase switch
    {
        NyxIdChatOperationPhase.Requested => "requested",
        NyxIdChatOperationPhase.Dispatched => "dispatched",
        NyxIdChatOperationPhase.Running => "running",
        NyxIdChatOperationPhase.Succeeded => "succeeded",
        NyxIdChatOperationPhase.Failed => "failed",
        NyxIdChatOperationPhase.Cancelled => "cancelled",
        NyxIdChatOperationPhase.Uncertain => "uncertain",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatControlKind kind) => kind switch
    {
        NyxIdChatControlKind.Stop => "stop",
        NyxIdChatControlKind.Steering => "steering",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatControlOutcome outcome) => outcome switch
    {
        NyxIdChatControlOutcome.Accepted => "accepted",
        NyxIdChatControlOutcome.Rejected => "rejected",
        NyxIdChatControlOutcome.AlreadyTerminal => "already_terminal",
        NyxIdChatControlOutcome.Uncancellable => "uncancellable",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatContinuationKind kind) => kind switch
    {
        NyxIdChatContinuationKind.Steering => "steering",
        NyxIdChatContinuationKind.Action => "action",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatContinuationAdmissionStatus status) => status switch
    {
        NyxIdChatContinuationAdmissionStatus.Requested => "requested",
        NyxIdChatContinuationAdmissionStatus.Accepted => "accepted",
        NyxIdChatContinuationAdmissionStatus.AcceptedForLater => "accepted_for_later",
        NyxIdChatContinuationAdmissionStatus.Rejected => "rejected",
        NyxIdChatContinuationAdmissionStatus.Started => "started",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatActionDisposition disposition) => disposition switch
    {
        NyxIdChatActionDisposition.Completed => "completed",
        NyxIdChatActionDisposition.Declined => "declined",
        NyxIdChatActionDisposition.Failed => "failed",
        NyxIdChatActionDisposition.Cancelled => "cancelled",
        NyxIdChatActionDisposition.Expired => "expired",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdAssistantActionKind action) => action switch
    {
        NyxIdAssistantActionKind.ServiceConnect => "service.connect",
        NyxIdAssistantActionKind.ServiceReauthorize => "service.reauthorize",
        NyxIdAssistantActionKind.ProviderSetAppCredentials => "provider.set_app_credentials",
        NyxIdAssistantActionKind.KeyCreate => "key.create",
        NyxIdAssistantActionKind.KeyRotate => "key.rotate",
        NyxIdAssistantActionKind.NodeRegisterToken => "node.register_token",
        NyxIdAssistantActionKind.NodeRotateToken => "node.rotate_token",
        NyxIdAssistantActionKind.NodeInjectCredential => "node.inject_credential",
        NyxIdAssistantActionKind.ServiceAccountCreate => "service_account.create",
        NyxIdAssistantActionKind.ServiceAccountRotateSecret => "service_account.rotate_secret",
        NyxIdAssistantActionKind.DeveloperAppCreate => "developer_app.create",
        NyxIdAssistantActionKind.DeveloperAppRotateSecret => "developer_app.rotate_secret",
        NyxIdAssistantActionKind.AccountMfaSetup => "account.mfa_setup",
        NyxIdAssistantActionKind.DeviceOnboard => "device.onboard",
        _ => string.Empty,
    };
}
