using Aevatar.AI.Abstractions;
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
            Deleted = state.Deleted,
            DeletedAt = state.DeletedAt?.Clone(),
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
            LatestStepControlResult = ToStepControlResult(state.LatestStepControlResult),
            ContinuationAdmission = ToContinuationAdmission(state.ContinuationAdmission),
            CanaryEffectFault = ToCanaryEffectFault(state.CanaryEffectFault),
        };
        document.RecentTerminalTurns.AddRange(state.RecentTerminalTurns.Select(ToTurn));
        document.PendingActions.AddRange(state.PendingActions.Select(ToAction));
        document.RecentActions.AddRange(state.RecentActions.Select(ToAction));
        document.RecentStepControlResults.AddRange(
            state.RecentStepControlResults.Select(result => ToStepControlResult(result)!));
        if (state.ContextAttachments is not null)
        {
            document.ContextAttachments.AddRange(state.ContextAttachments.Attachments.Select(static attachment =>
                new NyxIdChatConversationContextAttachmentDocument
                {
                    ArtifactId = attachment.ArtifactId,
                    RevisionMode = attachment.RevisionMode switch
                    {
                        ConversationContextAttachmentRevisionMode.FollowCurrent => "follow_current",
                        ConversationContextAttachmentRevisionMode.PinnedRevision => "pinned_revision",
                        _ => "unspecified",
                    },
                    PinnedRevisionId = attachment.PinnedRevisionId,
                }));
        }

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

    private static NyxIdChatConversationCanaryEffectFaultDocument? ToCanaryEffectFault(
        NyxIdChatCanaryEffectFaultState? fault)
    {
        var intent = fault?.ArmIntent;
        var sourceOperation = intent?.SourceOperationKey;
        if (intent is null || sourceOperation is null)
            return null;

        var document = new NyxIdChatConversationCanaryEffectFaultDocument
        {
            ArmId = intent.ArmId,
            Status = ToWireName(fault!.Status),
            SourceOperation = ToCanaryOperation(sourceOperation),
            ExpiresAt = intent.ExpiresAt?.Clone(),
            ArmedAt = fault.ArmedAt?.Clone(),
            ConsumedAt = fault.ConsumedAt?.Clone(),
            ForwardedAt = fault.ForwardedAt?.Clone(),
        };

        if (fault.Directive?.Key is { } targetOperation)
            document.TargetOperation = ToCanaryOperation(targetOperation);

        return document;
    }

    private static NyxIdChatConversationCanaryOperationDocument ToCanaryOperation(
        NyxIdChatOperationKey operation) =>
        new()
        {
            ConversationActorId = operation.ConversationActorId,
            TurnId = operation.TurnId,
            TaskId = operation.TaskId,
            StepId = operation.StepId,
            OperationId = operation.OperationId,
            OperationGeneration = operation.OperationGeneration,
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
            SchemaVersion = task.SchemaVersion,
            ActorId = task.ActorId,
            PlanId = task.PlanId,
            PlanRevision = task.PlanRevision,
            PlanRevisionHistoryStart = task.PlanRevisionHistoryStart,
            Title = task.Title,
        };
        document.Steps.AddRange(task.Steps
            .OrderBy(static step => step.Order)
            .ThenBy(static step => step.StepId, StringComparer.Ordinal)
            .Select(ToStep));
        document.PlanRevisions.AddRange(task.PlanRevisions.Select(static revision =>
        {
            var result = new NyxIdChatConversationPlanRevisionDocument
            {
                PlanRevision = revision.PlanRevision,
                RevisionCause = ToWireName(revision.RevisionCause),
                CommittedAt = revision.CommittedAt?.Clone(),
            };
            result.AddedStepIds.AddRange(revision.AddedStepIds);
            result.CancelledStepIds.AddRange(revision.CancelledStepIds);
            return result;
        }));
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
            Source = ToSource(step.Source),
            AddedBy = ToWireName(step.AddedBy),
            AddedInPlanRevision = step.AddedInPlanRevision,
            CancelledInPlanRevision = step.CancelledInPlanRevision,
            ApprovalObservation = step.ApprovalObservation == null
                ? null
                : new NyxIdChatConversationPostReturnApprovalObservationDocument
                {
                    ApprovalRequestId = step.ApprovalObservation.ApprovalRequestId,
                    DecisionMode = ToWireName(step.ApprovalObservation.DecisionMode),
                    ReceiptStatus = ToWireName(step.ApprovalObservation.ReceiptStatus),
                    ObservedAt = step.ApprovalObservation.ObservedAt?.Clone(),
                    TerminalOutcome = ToWireName(step.ApprovalObservation.TerminalOutcome),
                    SubjectKind = step.ApprovalObservation.SubjectKind,
                },
            Guard = step.Guard == null
                ? null
                : new NyxIdChatConversationStepGuardDocument
                {
                    ConditionStepId = step.Guard.ConditionStepId,
                    RequiredOutcome = ToWireName(step.Guard.RequiredOutcome),
                },
            DependsOn = { step.DependsOn },
            Estimate = step.Estimate == null
                ? null
                : new NyxIdChatConversationStepEstimateDocument
                {
                    Kind = ToWireName(step.Estimate.Kind),
                    Seconds = step.Estimate.Seconds,
                },
            Substeps =
            {
                step.Substeps.Select(static substep => new NyxIdChatConversationSubstepDocument
                {
                    SubstepId = substep.SubstepId,
                    Title = substep.Title,
                    Status = ToWireName(substep.Status),
                }),
            },
        };

    private static NyxIdChatConversationStepSourceDocument? ToSource(
        NyxIdChatStepSource? source) =>
        source?.SourceCase switch
        {
            NyxIdChatStepSource.SourceOneofCase.Llm =>
                new NyxIdChatConversationStepSourceDocument
                {
                    Llm = new NyxIdChatConversationLLMStepSourceDocument
                    {
                        Model = source.Llm.Model,
                    },
                },
            NyxIdChatStepSource.SourceOneofCase.Tool =>
                new NyxIdChatConversationStepSourceDocument
                {
                    Tool = ToToolSource(source.Tool),
                },
            NyxIdChatStepSource.SourceOneofCase.BrowserAction =>
                new NyxIdChatConversationStepSourceDocument
                {
                    BrowserAction = new NyxIdChatConversationBrowserActionStepSourceDocument
                    {
                        Action = ToWireName(source.BrowserAction.Action),
                        ActionRequestId = source.BrowserAction.ActionRequestId,
                    },
                },
            NyxIdChatStepSource.SourceOneofCase.Postcondition =>
                new NyxIdChatConversationStepSourceDocument
                {
                    Postcondition = new NyxIdChatConversationPostconditionStepSourceDocument
                    {
                        ActionRequestId = source.Postcondition.ActionRequestId,
                        Check = source.Postcondition.Check,
                        ProviderResourceId = source.Postcondition.ProviderResourceId,
                    },
                },
            NyxIdChatStepSource.SourceOneofCase.Input =>
                new NyxIdChatConversationStepSourceDocument
                {
                    Input = new NyxIdChatConversationInputStepSourceDocument
                    {
                        RequestId = source.Input.RequestId,
                    },
                },
            NyxIdChatStepSource.SourceOneofCase.Approval =>
                new NyxIdChatConversationStepSourceDocument
                {
                    Approval = new NyxIdChatConversationApprovalStepSourceDocument
                    {
                        ApprovalRequestId = source.Approval.ApprovalRequestId,
                    },
                },
            NyxIdChatStepSource.SourceOneofCase.Web =>
                new NyxIdChatConversationStepSourceDocument
                {
                    Web = new NyxIdChatConversationWebStepSourceDocument(),
                },
            NyxIdChatStepSource.SourceOneofCase.Condition =>
                new NyxIdChatConversationStepSourceDocument
                {
                    Condition = new NyxIdChatConversationConditionStepSourceDocument
                    {
                        Condition = ToCondition(source.Condition.Condition),
                    },
                },
            _ => null,
        };

    private static NyxIdChatConversationNumericConditionDocument ToCondition(
        NyxIdChatNumericConditionState condition) =>
        new()
        {
            ConditionId = condition.ConditionId,
            SourceInputRequestId = condition.SourceInputRequestId,
            SuggestedThreshold = condition.SuggestedThreshold,
            EffectiveThreshold = condition.EffectiveThreshold,
            ThresholdOrigin = ToWireName(condition.ThresholdOrigin),
            ObservedValue = condition.ObservedValue,
            Comparison = ToWireName(condition.Comparison),
            Outcome = ToWireName(condition.Outcome),
            EvaluatedAt = condition.EvaluatedAt?.Clone(),
            GuardedToolName = condition.GuardedToolName,
        };

    private static NyxIdChatConversationToolStepSourceDocument ToToolSource(
        NyxIdChatToolStepSource source)
    {
        var document = new NyxIdChatConversationToolStepSourceDocument
        {
            ToolName = source.ToolName,
            ServiceSlug = source.ServiceSlug,
            ServiceId = source.ServiceId,
            ProviderResourceId = source.ProviderResourceId,
            Presentation = source.Presentation?.Clone(),
        };
        if (source.HasReadinessCapabilityId)
            document.ReadinessCapabilityId = source.ReadinessCapabilityId;
        return document;
    }

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
        if (operation == null)
            return null;

        var key = operation.Key;
        return new NyxIdChatConversationOperationDocument
        {
            ConversationActorId = key?.ConversationActorId ?? string.Empty,
            TurnId = key?.TurnId ?? string.Empty,
            TaskId = key?.TaskId ?? string.Empty,
            StepId = key?.StepId ?? string.Empty,
            OperationId = key?.OperationId ?? string.Empty,
            OperationGeneration = key?.OperationGeneration ?? 0,
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
            LastProgressAt = operation.LastProgressAt?.Clone(),
            StalledAt = operation.StalledAt?.Clone(),
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
        if (input.NumericThreshold is not null)
        {
            document.NumericThreshold = new NyxIdChatConversationNumericThresholdInputDocument
            {
                SuggestedValue = input.NumericThreshold.SuggestedValue,
                MinimumValue = input.NumericThreshold.MinimumValue,
                MaximumValue = input.NumericThreshold.MaximumValue,
            };
        }
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
        NyxIdChatInputResolutionState? resolution)
    {
        if (resolution is null)
            return null;
        var document = new NyxIdChatConversationInputResolutionDocument
        {
            RequestId = resolution.RequestId,
            ClientRequestId = resolution.ClientRequestId,
            Outcome = ToWireName(resolution.Outcome),
            CommittedAt = resolution.CommittedAt?.Clone(),
        };
        if (resolution.NumericThreshold is not null)
        {
            document.NumericThreshold =
                new NyxIdChatConversationNumericThresholdResolutionDocument
                {
                    SuggestedValue = resolution.NumericThreshold.SuggestedValue,
                    EffectiveValue = resolution.NumericThreshold.EffectiveValue,
                    Origin = ToWireName(resolution.NumericThreshold.Origin),
                };
        }
        if (resolution.Answer is not null)
        {
            switch (resolution.Answer.AnswerCase)
            {
                case NyxIdChatInputAnswer.AnswerOneofCase.FreeText:
                    document.Answer = new NyxIdChatConversationInputAnswerDocument
                    {
                        FreeText = resolution.Answer.FreeText,
                    };
                    break;
                case NyxIdChatInputAnswer.AnswerOneofCase.Selection:
                    document.Answer = new NyxIdChatConversationInputAnswerDocument
                    {
                        Selection = new NyxIdChatConversationInputSelectionAnswerDocument
                        {
                            OptionIds = { resolution.Answer.Selection.OptionIds },
                        },
                    };
                    break;
            }
        }
        return document;
    }

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

    private static NyxIdChatConversationStepControlResultDocument? ToStepControlResult(
        NyxIdChatStepControlResultState? result) =>
        result == null
            ? null
            : new NyxIdChatConversationStepControlResultDocument
            {
                Kind = ToWireName(result.Kind),
                RequestId = result.RequestId,
                ClientRequestId = result.ClientRequestId,
                TurnId = result.TurnId,
                TaskId = result.TaskId,
                StepId = result.StepId,
                ExpectedOperationGeneration = result.ExpectedOperationGeneration,
                OperationGeneration = result.OperationGeneration,
                Outcome = ToWireName(result.Outcome),
                ReasonCode = result.ReasonCode,
                SafeMessage = result.SafeMessage,
                CommandId = result.CommandId,
                CorrelationId = result.CorrelationId,
                CommittedAt = result.CommittedAt?.Clone(),
                ExpectedStateVersion = result.ExpectedStateVersion,
                ScopeId = result.ScopeId,
                ConversationActorId = result.ConversationActorId,
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
            Request = ToActionRequest(action),
        };
        document.Reports.AddRange(action.Reports.Select(ToActionReport));
        return document;
    }

    private static NyxIdChatConversationActionRequestDocument? ToActionRequest(
        NyxIdChatActionRequestState action)
    {
        var parameters = action.Params?.ParamsCase switch
        {
            NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect =>
                new NyxIdChatConversationActionParamsDocument
                {
                    CatalogService = new NyxIdChatConversationCatalogServiceConnectDocument
                    {
                        ServiceSlug = action.Params.CatalogServiceConnect.ServiceSlug,
                        RequestedScopes = { action.Params.CatalogServiceConnect.RequestedScopes },
                        ViaNodeId = action.Params.CatalogServiceConnect.ViaNodeId,
                        TargetOrgId = action.Params.CatalogServiceConnect.TargetOrgId,
                    },
                },
            NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect =>
                ToCustomServiceConnectParams(action.Params.CustomServiceConnect),
            NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate =>
                new NyxIdChatConversationActionParamsDocument
                {
                    KeyCreate = new NyxIdChatConversationKeyCreateDocument
                    {
                        Name = action.Params.KeyCreate.Name,
                        Platform = action.Params.KeyCreate.Platform,
                        AllowedServiceIds = { action.Params.KeyCreate.AllowedServiceIds },
                    },
                },
            NyxIdAssistantActionParams.ParamsOneofCase.KeyRotate =>
                new NyxIdChatConversationActionParamsDocument
                {
                    KeyRotate = new NyxIdChatConversationKeyRotateDocument
                    {
                        KeyId = action.Params.KeyRotate.KeyId,
                    },
                },
            NyxIdAssistantActionParams.ParamsOneofCase.ServiceAccessReview =>
                new NyxIdChatConversationActionParamsDocument
                {
                    ServiceAccessReview =
                        new NyxIdChatConversationServiceAccessReviewDocument
                        {
                            UserServiceId = action.Params.ServiceAccessReview.UserServiceId,
                            ServiceSlug = action.Params.ServiceAccessReview.ServiceSlug,
                            ResourceUri = action.Params.ServiceAccessReview.ResourceUri,
                        },
                },
            _ => null,
        };
        return parameters is null
            ? null
            : new NyxIdChatConversationActionRequestDocument
            {
                SchemaVersion = action.SchemaVersion,
                ActorId = action.ConversationActorId,
                OriginTurnId = action.OriginTurnId,
                TaskId = action.TaskId,
                StepId = action.StepId,
                ActionRequestId = action.ActionRequestId,
                Action = ToWireName(action.Action),
                Params = parameters,
            };
    }

    private static NyxIdChatConversationActionParamsDocument? ToCustomServiceConnectParams(
        NyxIdCustomServiceConnectParams parameters)
    {
        if (!TryNormalizeSafeActionUrl(parameters.EndpointUrl, out var endpointUrl))
            return null;

        return new NyxIdChatConversationActionParamsDocument
        {
            CustomService = new NyxIdChatConversationCustomServiceConnectDocument
            {
                Name = parameters.Name,
                EndpointUrl = endpointUrl,
                AuthMethod = parameters.AuthMethod,
                AuthKeyName = parameters.AuthKeyName,
                ViaNodeId = parameters.ViaNodeId,
                TargetOrgId = parameters.TargetOrgId,
            },
        };
    }

    private static bool TryNormalizeSafeActionUrl(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
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

    private static string ToWireName(NyxIdApprovalDecisionMode mode) => mode switch
    {
        NyxIdApprovalDecisionMode.PerRequest => "per_request",
        NyxIdApprovalDecisionMode.Grant => "grant",
        _ => "unknown",
    };

    private static string ToWireName(NyxIdApprovalTerminalOutcome outcome) => outcome switch
    {
        NyxIdApprovalTerminalOutcome.Rejected => "rejected",
        NyxIdApprovalTerminalOutcome.Expired => "expired",
        NyxIdApprovalTerminalOutcome.TimedOut => "timed_out",
        _ => string.Empty,
    };

    private static string ToWireName(AgentToolReceiptStatus status) => status switch
    {
        AgentToolReceiptStatus.ApprovalRequired => "approval_required",
        AgentToolReceiptStatus.Denied => "denied",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatStepKind kind) => kind switch
    {
        NyxIdChatStepKind.Llm => "llm",
        NyxIdChatStepKind.Tool => "tool",
        NyxIdChatStepKind.BrowserAction => "browser_action",
        NyxIdChatStepKind.Postcondition => "postcondition",
        NyxIdChatStepKind.Input => "input",
        NyxIdChatStepKind.Approval => "approval",
        NyxIdChatStepKind.Web => "web",
        NyxIdChatStepKind.Condition => "condition",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatConditionOutcome outcome) => outcome switch
    {
        NyxIdChatConditionOutcome.True => "true",
        NyxIdChatConditionOutcome.False => "false",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatThresholdOrigin origin) => origin switch
    {
        NyxIdChatThresholdOrigin.Suggested => "suggested",
        NyxIdChatThresholdOrigin.UserOverride => "user_override",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatIntegerComparison comparison) => comparison switch
    {
        NyxIdChatIntegerComparison.Gte => "gte",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatStepAddedBy addedBy) => addedBy switch
    {
        NyxIdChatStepAddedBy.Initial => "initial",
        NyxIdChatStepAddedBy.Replan => "replan",
        NyxIdChatStepAddedBy.Steering => "steering",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatPlanRevisionCause cause) => cause switch
    {
        NyxIdChatPlanRevisionCause.Initial => "initial",
        NyxIdChatPlanRevisionCause.ScopeResolution => "scope_resolution",
        NyxIdChatPlanRevisionCause.FailureRecovery => "failure_recovery",
        NyxIdChatPlanRevisionCause.Steering => "steering",
        NyxIdChatPlanRevisionCause.UserRevision => "user_revision",
        _ => "unspecified",
    };

    private static string ToWireName(NyxIdChatStepEstimateKind kind) => kind switch
    {
        NyxIdChatStepEstimateKind.Duration => "duration",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatSubstepStatus status) => status switch
    {
        NyxIdChatSubstepStatus.Running => "running",
        NyxIdChatSubstepStatus.Done => "done",
        NyxIdChatSubstepStatus.Failed => "failed",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatAttentionKind kind) => kind switch
    {
        NyxIdChatAttentionKind.None => "none",
        NyxIdChatAttentionKind.Input => "input",
        NyxIdChatAttentionKind.Approval => "approval",
        NyxIdChatAttentionKind.Stalled => "stalled",
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
        NyxIdChatNeedsYouResolutionOutcome.Expired => "expired",
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

    private static string ToWireName(NyxIdChatStepControlKind kind) => kind switch
    {
        NyxIdChatStepControlKind.Retry => "retry",
        NyxIdChatStepControlKind.Skip => "skip",
        _ => string.Empty,
    };

    private static string ToWireName(NyxIdChatTransitionOutcome outcome) => outcome switch
    {
        NyxIdChatTransitionOutcome.Accepted => "accepted",
        NyxIdChatTransitionOutcome.Idempotent => "idempotent",
        NyxIdChatTransitionOutcome.Rejected => "rejected",
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

    private static string ToWireName(NyxIdChatCanaryEffectFaultStatus status) => status switch
    {
        NyxIdChatCanaryEffectFaultStatus.Armed => "armed",
        NyxIdChatCanaryEffectFaultStatus.Forwarded => "forwarded",
        NyxIdChatCanaryEffectFaultStatus.Consumed => "consumed",
        NyxIdChatCanaryEffectFaultStatus.Expired => "expired",
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
        NyxIdAssistantActionKind.ServiceAccessReview => "service.access_review",
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
