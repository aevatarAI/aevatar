using Aevatar.AI.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatTaskPlanWireMapper
{
    public static NyxIdChatTaskPlan FromState(NyxIdChatTaskState task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var plan = new NyxIdChatTaskPlan
        {
            TaskId = task.TaskId,
            TurnId = task.TurnId,
            Status = task.Status,
            ActiveStepId = task.ActiveStepId,
            ActiveOperationId = task.ActiveOperationId,
            FailureCode = task.FailureCode,
            SafeMessage = task.SafeMessage,
            CreatedAt = task.CreatedAt?.Clone(),
            UpdatedAt = task.UpdatedAt?.Clone(),
            SchemaVersion = task.SchemaVersion,
            ActorId = task.ActorId,
            PlanId = task.PlanId,
            PlanRevision = task.PlanRevision,
            PlanRevisionHistoryStart = task.PlanRevisionHistoryStart,
            Title = task.Title,
        };
        plan.Steps.AddRange(task.Steps
            .OrderBy(static step => step.Order)
            .ThenBy(static step => step.StepId, StringComparer.Ordinal)
            .Select(FromState));
        plan.PlanRevisions.AddRange(task.PlanRevisions.Select(static revision => revision.Clone()));
        return plan;
    }

    public static NyxIdChatTaskPlan FromSnapshot(NyxIdChatConversationTaskSnapshot task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var plan = new NyxIdChatTaskPlan
        {
            TaskId = task.TaskId,
            TurnId = task.TurnId,
            Status = ParseTaskStatus(task.Status),
            ActiveStepId = task.ActiveStepId ?? string.Empty,
            ActiveOperationId = task.ActiveOperationId ?? string.Empty,
            FailureCode = task.FailureCode ?? string.Empty,
            SafeMessage = task.SafeMessage ?? string.Empty,
            CreatedAt = ToTimestamp(task.CreatedAt),
            UpdatedAt = ToTimestamp(task.UpdatedAt),
            SchemaVersion = task.SchemaVersion,
            ActorId = task.ActorId ?? string.Empty,
            PlanId = task.PlanId ?? string.Empty,
            PlanRevision = task.PlanRevision,
            PlanRevisionHistoryStart = task.PlanRevisionHistoryStart,
            Title = task.Title ?? string.Empty,
        };
        plan.Steps.AddRange(task.Steps
            .OrderBy(static step => step.Order)
            .ThenBy(static step => step.StepId, StringComparer.Ordinal)
            .Select(FromSnapshot));
        if (task.PlanRevisions is not null)
        {
            plan.PlanRevisions.AddRange(task.PlanRevisions.Select(static revision =>
            {
                var result = new NyxIdChatPlanRevisionRecord
                {
                    PlanRevision = revision.PlanRevision,
                    RevisionCause = ParsePlanRevisionCause(revision.RevisionCause),
                    CommittedAt = ToTimestamp(revision.CommittedAt),
                };
                result.AddedStepIds.AddRange(revision.AddedStepIds);
                result.CancelledStepIds.AddRange(revision.CancelledStepIds);
                return result;
            }));
        }
        return plan;
    }

    public static NyxIdChatTaskPlanStep FromState(NyxIdChatTaskStepState step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var result = new NyxIdChatTaskPlanStep
        {
            StepId = step.StepId,
            Order = step.Order,
            Kind = step.Kind,
            Status = step.Status,
            Required = step.Required,
            Description = step.Description,
            Source = step.Source?.Clone(),
            MayChangeExternalState = step.MayChangeExternalState,
            ExternalEffect = step.ExternalEffect,
            Operation = FromState(step.Operation),
            ApprovalRequestId = step.ApprovalRequestId,
            ActionRequestId = step.ActionRequestId,
            FailureCode = step.FailureCode,
            SafeMessage = step.SafeMessage,
            SafeToSkip = step.SafeToSkip,
            AvailableActions = step.AvailableActions?.Clone(),
            UpdatedAt = step.UpdatedAt?.Clone(),
            AddedBy = step.AddedBy,
            AddedInPlanRevision = step.AddedInPlanRevision,
            CancelledInPlanRevision = step.CancelledInPlanRevision,
            Estimate = step.Estimate?.Clone(),
            ApprovalObservation = ToPublicApprovalObservation(step.ApprovalObservation),
            Guard = step.Guard?.Clone(),
        };
        result.DependsOn.AddRange(step.DependsOn);
        result.Substeps.AddRange(step.Substeps.Select(static substep => substep.Clone()));
        return result;
    }

    private static NyxIdChatPostReturnApprovalObservation? ToPublicApprovalObservation(
        NyxIdChatPostReturnApprovalObservation? observation) =>
        observation is null
            ? null
            : new NyxIdChatPostReturnApprovalObservation
            {
                ApprovalRequestId = observation.ApprovalRequestId,
                DecisionMode = observation.DecisionMode,
                ReceiptStatus = observation.ReceiptStatus,
                ObservedAt = observation.ObservedAt?.Clone(),
                TerminalOutcome = observation.TerminalOutcome,
                SubjectKind = observation.SubjectKind,
            };

    public static NyxIdChatTaskPlanStepChanged FromState(NyxIdChatTaskStepChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);

        var result = new NyxIdChatTaskPlanStepChanged
        {
            TaskId = changed.TaskId,
            PlanRevision = changed.PlanRevision,
            ChangeKind = changed.ChangeKind,
        };
        if (changed.Step is not null)
            result.Step = FromState(changed.Step);
        return result;
    }

    private static NyxIdChatTaskPlanStep FromSnapshot(NyxIdChatConversationStepSnapshot step)
    {
        var result = new NyxIdChatTaskPlanStep
        {
            StepId = step.StepId,
            Order = step.Order,
            Kind = ParseStepKind(step.Kind),
            Status = ParseStepStatus(step.Status),
            Required = step.Required,
            Description = step.Description ?? string.Empty,
            Source = FromSnapshot(step.Source),
            MayChangeExternalState = step.MayChangeExternalState,
            ExternalEffect = ParseEffectEvidence(step.ExternalEffect),
            Operation = FromSnapshot(step.Operation),
            ApprovalRequestId = step.ApprovalRequestId ?? string.Empty,
            ActionRequestId = step.ActionRequestId ?? string.Empty,
            FailureCode = step.FailureCode ?? string.Empty,
            SafeMessage = step.SafeMessage ?? string.Empty,
            SafeToSkip = step.SafeToSkip,
            AvailableActions = FromSnapshot(step.AvailableActions),
            UpdatedAt = ToTimestamp(step.UpdatedAt),
            AddedBy = ParseStepAddedBy(step.AddedBy),
            AddedInPlanRevision = step.AddedInPlanRevision,
            CancelledInPlanRevision = step.CancelledInPlanRevision,
            Estimate = FromSnapshot(step.Estimate),
            ApprovalObservation = FromSnapshot(step.ApprovalObservation),
            Guard = step.Guard is null
                ? null
                : new NyxIdChatStepGuard
                {
                    ConditionStepId = step.Guard.ConditionStepId,
                    RequiredOutcome = ParseConditionOutcome(step.Guard.RequiredOutcome),
                },
        };
        if (step.DependsOn is not null)
            result.DependsOn.AddRange(step.DependsOn);
        if (step.Substeps is not null)
        {
            result.Substeps.AddRange(step.Substeps.Select(static substep =>
                new NyxIdChatSubstepState
                {
                    SubstepId = substep.SubstepId,
                    Title = substep.Title,
                    Status = ParseSubstepStatus(substep.Status),
                }));
        }
        return result;
    }

    private static NyxIdChatTaskPlanOperation? FromState(NyxIdChatOperationState? operation)
    {
        if (operation is null)
            return null;

        var key = operation.Key;
        return new NyxIdChatTaskPlanOperation
        {
            ConversationActorId = key?.ConversationActorId ?? string.Empty,
            TurnId = key?.TurnId ?? string.Empty,
            TaskId = key?.TaskId ?? string.Empty,
            StepId = key?.StepId ?? string.Empty,
            OperationId = key?.OperationId ?? string.Empty,
            OperationGeneration = key?.OperationGeneration ?? 0,
            Kind = operation.Kind,
            Phase = operation.Phase,
            MayChangeExternalState = operation.MayChangeExternalState,
            Idempotent = operation.Idempotent,
            LatestProgressSequence = operation.LatestProgressSequence,
            TerminalCode = operation.TerminalCode,
            SafeMessage = operation.SafeMessage,
            RequestedAt = operation.RequestedAt?.Clone(),
            DispatchedAt = operation.DispatchedAt?.Clone(),
            CompletedAt = operation.CompletedAt?.Clone(),
            LastProgressAt = operation.LastProgressAt?.Clone(),
            StalledAt = operation.StalledAt?.Clone(),
        };
    }

    private static NyxIdChatTaskPlanOperation? FromSnapshot(
        NyxIdChatConversationOperationSnapshot? operation) =>
        operation is null
            ? null
            : new NyxIdChatTaskPlanOperation
            {
                ConversationActorId = operation.ConversationActorId,
                TurnId = operation.TurnId,
                TaskId = operation.TaskId,
                StepId = operation.StepId,
                OperationId = operation.OperationId,
                OperationGeneration = operation.OperationGeneration,
                Kind = ParseStepKind(operation.Kind),
                Phase = ParseOperationPhase(operation.Phase),
                MayChangeExternalState = operation.MayChangeExternalState,
                Idempotent = operation.Idempotent,
                LatestProgressSequence = operation.LatestProgressSequence,
                TerminalCode = operation.TerminalCode ?? string.Empty,
                SafeMessage = operation.SafeMessage ?? string.Empty,
                RequestedAt = ToTimestamp(operation.RequestedAt),
                DispatchedAt = ToTimestamp(operation.DispatchedAt),
                CompletedAt = ToTimestamp(operation.CompletedAt),
                LastProgressAt = ToTimestamp(operation.LastProgressAt),
                StalledAt = ToTimestamp(operation.StalledAt),
            };

    private static NyxIdChatStepSource? FromSnapshot(
        NyxIdChatConversationStepSourceSnapshot? source)
    {
        if (source?.Llm is not null)
        {
            return new NyxIdChatStepSource
            {
                Llm = new NyxIdChatLLMStepSource { Model = source.Llm.Model },
            };
        }

        if (source?.Tool is not null)
        {
            var tool = new NyxIdChatToolStepSource
            {
                ToolName = source.Tool.ToolName,
                ServiceSlug = source.Tool.ServiceSlug ?? string.Empty,
                ServiceId = source.Tool.ServiceId ?? string.Empty,
                ProviderResourceId = source.Tool.ProviderResourceId ?? string.Empty,
                Presentation = source.Tool.Presentation?.Clone(),
            };
            if (source.Tool.ReadinessCapabilityId is not null)
                tool.ReadinessCapabilityId = source.Tool.ReadinessCapabilityId;
            return new NyxIdChatStepSource { Tool = tool };
        }

        if (source?.BrowserAction is not null)
        {
            return new NyxIdChatStepSource
            {
                BrowserAction = new NyxIdChatBrowserActionStepSource
                {
                    Action = ParseAssistantAction(source.BrowserAction.Action),
                    ActionRequestId = source.BrowserAction.ActionRequestId ?? string.Empty,
                },
            };
        }

        if (source?.Postcondition is not null)
        {
            return new NyxIdChatStepSource
            {
                Postcondition = new NyxIdChatPostconditionStepSource
                {
                    ActionRequestId = source.Postcondition.ActionRequestId ?? string.Empty,
                    Check = source.Postcondition.Check ?? string.Empty,
                    ProviderResourceId = source.Postcondition.ProviderResourceId ?? string.Empty,
                },
            };
        }

        if (source?.Input is not null)
        {
            return new NyxIdChatStepSource
            {
                Input = new NyxIdChatInputStepSource
                {
                    RequestId = source.Input.RequestId ?? string.Empty,
                },
            };
        }

        if (source?.Approval is not null)
        {
            return new NyxIdChatStepSource
            {
                Approval = new NyxIdChatApprovalStepSource
                {
                    ApprovalRequestId = source.Approval.ApprovalRequestId ?? string.Empty,
                },
            };
        }

        if (source?.Condition is not null)
        {
            var condition = source.Condition.Condition;
            return new NyxIdChatStepSource
            {
                Condition = new NyxIdChatConditionStepSource
                {
                    Condition = new NyxIdChatNumericConditionState
                    {
                        ConditionId = condition.ConditionId,
                        SourceInputRequestId = condition.SourceInputRequestId,
                        SuggestedThreshold = condition.SuggestedThreshold,
                        EffectiveThreshold = condition.EffectiveThreshold,
                        ThresholdOrigin = ParseThresholdOrigin(condition.ThresholdOrigin),
                        ObservedValue = condition.ObservedValue,
                        Comparison = ParseIntegerComparison(condition.Comparison),
                        Outcome = ParseConditionOutcome(condition.Outcome),
                        EvaluatedAt = ToTimestamp(condition.EvaluatedAt),
                        GuardedToolName = condition.GuardedToolName,
                    },
                },
            };
        }

        return source?.Web is null
            ? null
            : new NyxIdChatStepSource { Web = new NyxIdChatWebStepSource() };
    }

    private static NyxIdChatConditionOutcome ParseConditionOutcome(string? value) => value switch
    {
        "true" => NyxIdChatConditionOutcome.True,
        "false" => NyxIdChatConditionOutcome.False,
        _ => NyxIdChatConditionOutcome.Unspecified,
    };

    private static NyxIdChatThresholdOrigin ParseThresholdOrigin(string? value) => value switch
    {
        "suggested" => NyxIdChatThresholdOrigin.Suggested,
        "user_override" => NyxIdChatThresholdOrigin.UserOverride,
        _ => NyxIdChatThresholdOrigin.Unspecified,
    };

    private static NyxIdChatIntegerComparison ParseIntegerComparison(string? value) => value switch
    {
        "gte" => NyxIdChatIntegerComparison.Gte,
        _ => NyxIdChatIntegerComparison.Unspecified,
    };

    private static NyxIdChatAvailableActions? FromSnapshot(
        NyxIdChatAvailableActionsSnapshot? actions) =>
        actions is null
            ? null
            : new NyxIdChatAvailableActions
            {
                Retry = actions.Retry,
                Skip = actions.Skip,
                Stop = actions.Stop,
            };

    private static NyxIdChatStepEstimate? FromSnapshot(
        NyxIdChatConversationStepEstimateSnapshot? estimate) =>
        estimate is null
            ? null
            : new NyxIdChatStepEstimate
            {
                Kind = ParseStepEstimateKind(estimate.Kind),
                Seconds = estimate.Seconds,
            };

    private static NyxIdChatPostReturnApprovalObservation? FromSnapshot(
        NyxIdChatPostReturnApprovalObservationSnapshot? observation) =>
        observation is null
            ? null
            : new NyxIdChatPostReturnApprovalObservation
            {
                ApprovalRequestId = observation.ApprovalRequestId,
                DecisionMode = ParseApprovalDecisionMode(observation.DecisionMode),
                ReceiptStatus = ParseReceiptStatus(observation.ReceiptStatus),
                ObservedAt = ToTimestamp(observation.ObservedAt),
                TerminalOutcome = ParseApprovalTerminalOutcome(observation.TerminalOutcome),
                SubjectKind = observation.SubjectKind ?? string.Empty,
            };

    private static Timestamp? ToTimestamp(DateTimeOffset? value) =>
        value.HasValue ? Timestamp.FromDateTimeOffset(value.Value) : null;

    private static NyxIdChatTaskStatus ParseTaskStatus(string value) => value switch
    {
        "active" => NyxIdChatTaskStatus.Active,
        "succeeded" => NyxIdChatTaskStatus.Succeeded,
        "failed" => NyxIdChatTaskStatus.Failed,
        "stopped" => NyxIdChatTaskStatus.Stopped,
        "blocked" => NyxIdChatTaskStatus.Blocked,
        _ => NyxIdChatTaskStatus.Unspecified,
    };

    private static NyxIdChatStepStatus ParseStepStatus(string value) => value switch
    {
        "planned" => NyxIdChatStepStatus.Planned,
        "waiting" => NyxIdChatStepStatus.Waiting,
        "running" => NyxIdChatStepStatus.Running,
        "done" => NyxIdChatStepStatus.Done,
        "failed" => NyxIdChatStepStatus.Failed,
        "skipped" => NyxIdChatStepStatus.Skipped,
        "cancelled" => NyxIdChatStepStatus.Cancelled,
        "uncertain" => NyxIdChatStepStatus.Uncertain,
        _ => NyxIdChatStepStatus.Unspecified,
    };

    private static NyxIdChatEffectEvidence ParseEffectEvidence(string value) => value switch
    {
        "not_started" => NyxIdChatEffectEvidence.NotStarted,
        "not_applied" => NyxIdChatEffectEvidence.NotApplied,
        "confirmed" => NyxIdChatEffectEvidence.Confirmed,
        "may_have_changed" => NyxIdChatEffectEvidence.MayHaveChanged,
        _ => NyxIdChatEffectEvidence.Unspecified,
    };

    private static NyxIdApprovalDecisionMode ParseApprovalDecisionMode(string value) =>
        value switch
        {
            "per_request" => NyxIdApprovalDecisionMode.PerRequest,
            "grant" => NyxIdApprovalDecisionMode.Grant,
            _ => NyxIdApprovalDecisionMode.Unspecified,
        };

    private static AgentToolReceiptStatus ParseReceiptStatus(string value) => value switch
    {
        "approval_required" => AgentToolReceiptStatus.ApprovalRequired,
        "denied" => AgentToolReceiptStatus.Denied,
        _ => AgentToolReceiptStatus.Unspecified,
    };

    private static NyxIdApprovalTerminalOutcome ParseApprovalTerminalOutcome(string? value) => value switch
    {
        "rejected" => NyxIdApprovalTerminalOutcome.Rejected,
        "expired" => NyxIdApprovalTerminalOutcome.Expired,
        "timed_out" => NyxIdApprovalTerminalOutcome.TimedOut,
        _ => NyxIdApprovalTerminalOutcome.Unspecified,
    };

    private static NyxIdChatStepKind ParseStepKind(string value) => value switch
    {
        "llm" => NyxIdChatStepKind.Llm,
        "tool" => NyxIdChatStepKind.Tool,
        "browser_action" => NyxIdChatStepKind.BrowserAction,
        "postcondition" => NyxIdChatStepKind.Postcondition,
        "input" => NyxIdChatStepKind.Input,
        "approval" => NyxIdChatStepKind.Approval,
        "web" => NyxIdChatStepKind.Web,
        "condition" => NyxIdChatStepKind.Condition,
        _ => NyxIdChatStepKind.Unspecified,
    };

    private static NyxIdChatStepAddedBy ParseStepAddedBy(string? value) => value switch
    {
        "initial" => NyxIdChatStepAddedBy.Initial,
        "replan" => NyxIdChatStepAddedBy.Replan,
        "steering" => NyxIdChatStepAddedBy.Steering,
        _ => NyxIdChatStepAddedBy.Unspecified,
    };

    private static NyxIdChatPlanRevisionCause ParsePlanRevisionCause(string? value) => value switch
    {
        "initial" => NyxIdChatPlanRevisionCause.Initial,
        "scope_resolution" => NyxIdChatPlanRevisionCause.ScopeResolution,
        "failure_recovery" => NyxIdChatPlanRevisionCause.FailureRecovery,
        "steering" => NyxIdChatPlanRevisionCause.Steering,
        "user_revision" => NyxIdChatPlanRevisionCause.UserRevision,
        _ => NyxIdChatPlanRevisionCause.Unspecified,
    };

    private static NyxIdChatStepEstimateKind ParseStepEstimateKind(string value) => value switch
    {
        "duration" => NyxIdChatStepEstimateKind.Duration,
        _ => NyxIdChatStepEstimateKind.Unspecified,
    };

    private static NyxIdChatSubstepStatus ParseSubstepStatus(string value) => value switch
    {
        "running" => NyxIdChatSubstepStatus.Running,
        "done" => NyxIdChatSubstepStatus.Done,
        "failed" => NyxIdChatSubstepStatus.Failed,
        _ => NyxIdChatSubstepStatus.Unspecified,
    };

    private static NyxIdChatOperationPhase ParseOperationPhase(string value) => value switch
    {
        "requested" => NyxIdChatOperationPhase.Requested,
        "dispatched" => NyxIdChatOperationPhase.Dispatched,
        "running" => NyxIdChatOperationPhase.Running,
        "succeeded" => NyxIdChatOperationPhase.Succeeded,
        "failed" => NyxIdChatOperationPhase.Failed,
        "cancelled" => NyxIdChatOperationPhase.Cancelled,
        "uncertain" => NyxIdChatOperationPhase.Uncertain,
        _ => NyxIdChatOperationPhase.Unspecified,
    };

    private static NyxIdAssistantActionKind ParseAssistantAction(string value) =>
        value.Replace('.', '_') switch
        {
            "service_connect" => NyxIdAssistantActionKind.ServiceConnect,
            "service_reauthorize" => NyxIdAssistantActionKind.ServiceReauthorize,
            "service_access_review" => NyxIdAssistantActionKind.ServiceAccessReview,
            "provider_set_app_credentials" => NyxIdAssistantActionKind.ProviderSetAppCredentials,
            "key_create" => NyxIdAssistantActionKind.KeyCreate,
            "key_rotate" => NyxIdAssistantActionKind.KeyRotate,
            "node_register_token" => NyxIdAssistantActionKind.NodeRegisterToken,
            "node_rotate_token" => NyxIdAssistantActionKind.NodeRotateToken,
            "node_inject_credential" => NyxIdAssistantActionKind.NodeInjectCredential,
            "service_account_create" => NyxIdAssistantActionKind.ServiceAccountCreate,
            "service_account_rotate_secret" => NyxIdAssistantActionKind.ServiceAccountRotateSecret,
            "developer_app_create" => NyxIdAssistantActionKind.DeveloperAppCreate,
            "developer_app_rotate_secret" => NyxIdAssistantActionKind.DeveloperAppRotateSecret,
            "account_mfa_setup" => NyxIdAssistantActionKind.AccountMfaSetup,
            "device_onboard" => NyxIdAssistantActionKind.DeviceOnboard,
            _ => NyxIdAssistantActionKind.Unspecified,
        };
}
