using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

internal sealed class ProjectionNyxIdChatConversationStateQueryPort
    : INyxIdChatConversationStateQueryPort
{
    private readonly IProjectionDocumentReader<
        NyxIdChatConversationCurrentStateDocument,
        string> _documentReader;

    public ProjectionNyxIdChatConversationStateQueryPort(
        IProjectionDocumentReader<NyxIdChatConversationCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<NyxIdChatConversationStateQueryResult> GetAsync(
        NyxIdChatConversationStateQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var scopeId = NormalizeIdentity(query.ScopeId);
        var actorId = NormalizeIdentity(query.ActorId);
        if (scopeId == null || actorId == null)
        {
            return NyxIdChatConversationStateQueryResult.ReloadRequired(
                0,
                null,
                "invalid_identity");
        }

        if (query.AfterStateVersion is < 0)
        {
            return NyxIdChatConversationStateQueryResult.ReloadRequired(
                0,
                NormalizeOptional(query.TurnId),
                "invalid_state_version");
        }

        var requestedTurnId = NormalizeOptional(query.TurnId);
        if (query.TurnId != null && requestedTurnId == null)
        {
            return NyxIdChatConversationStateQueryResult.ReloadRequired(
                0,
                null,
                "invalid_turn_id");
        }

        var document = await _documentReader.GetAsync(actorId, ct).ConfigureAwait(false);
        if (document == null)
            return NyxIdChatConversationStateQueryResult.NotFound();

        var serverTurnId = ResolveTurnId(document);
        if (!string.Equals(document.ScopeId, scopeId, StringComparison.Ordinal))
            return NyxIdChatConversationStateQueryResult.NotFound();

        if (!string.Equals(document.Id, actorId, StringComparison.Ordinal) ||
            !string.Equals(document.ActorId, actorId, StringComparison.Ordinal) ||
            !string.Equals(document.ConversationActorId, actorId, StringComparison.Ordinal))
            return NyxIdChatConversationStateQueryResult.NotFound();

        if (document.Deleted)
            return NyxIdChatConversationStateQueryResult.NotFound();

        if (document.StateVersion <= 0)
        {
            return NyxIdChatConversationStateQueryResult.ReloadRequired(
                document.StateVersion,
                serverTurnId,
                "invalid_read_model_version");
        }

        if (requestedTurnId != null &&
            !string.Equals(requestedTurnId, serverTurnId, StringComparison.Ordinal))
        {
            return NyxIdChatConversationStateQueryResult.ReloadRequired(
                document.StateVersion,
                serverTurnId,
                "turn_mismatch");
        }

        if (query.AfterStateVersion > document.StateVersion)
        {
            return NyxIdChatConversationStateQueryResult.ReloadRequired(
                document.StateVersion,
                serverTurnId,
                "future_state_version");
        }

        if (query.AfterStateVersion == document.StateVersion)
        {
            return NyxIdChatConversationStateQueryResult.NotModified(
                document.StateVersion,
                serverTurnId);
        }

        return NyxIdChatConversationStateQueryResult.Current(ToSnapshot(document));
    }

    public async Task<IReadOnlyDictionary<string, NyxIdChatConversationAttentionSummary>>
        GetAttentionSummariesAsync(
            string scopeId,
            IReadOnlyCollection<string> actorIds,
            CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeIdentity(scopeId);
        var normalizedActorIds = actorIds?
            .Select(NormalizeIdentity)
            .Where(static actorId => actorId is not null)
            .Select(static actorId => actorId!)
            .Distinct(StringComparer.Ordinal)
            .Take(200)
            .ToArray() ?? [];
        if (normalizedScopeId is null || normalizedActorIds.Length == 0)
            return new Dictionary<string, NyxIdChatConversationAttentionSummary>();

        var result = await _documentReader.QueryAsync(new ProjectionDocumentQuery
        {
            Filters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(NyxIdChatConversationCurrentStateDocument.ScopeId),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(normalizedScopeId),
                },
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(NyxIdChatConversationCurrentStateDocument.ConversationActorId),
                    Operator = ProjectionDocumentFilterOperator.In,
                    Value = ProjectionDocumentValue.FromStrings(normalizedActorIds),
                },
            ],
            Take = normalizedActorIds.Length,
        }, ct).ConfigureAwait(false);

        var requested = normalizedActorIds.ToHashSet(StringComparer.Ordinal);
        return result.Items
            .Where(document =>
                requested.Contains(document.ConversationActorId) &&
                string.Equals(document.Id, document.ActorId, StringComparison.Ordinal) &&
                string.Equals(document.ActorId, document.ConversationActorId, StringComparison.Ordinal) &&
                string.Equals(document.ScopeId, normalizedScopeId, StringComparison.Ordinal) &&
                !document.Deleted &&
                document.StateVersion > 0)
            .ToDictionary(
                static document => document.ConversationActorId,
                static document => new NyxIdChatConversationAttentionSummary(
                    document.ConversationActorId,
                    document.TaskStatus,
                    document.AttentionKind,
                    ToDateTimeOffset(document.AttentionSince),
                    NullIfEmpty(document.ActiveStepSummary),
                    document.StateVersion,
                    ToContextAttachments(document.ContextAttachments)),
                StringComparer.Ordinal);
    }

    private static NyxIdChatConversationStateSnapshot ToSnapshot(
        NyxIdChatConversationCurrentStateDocument document) =>
        new(
            document.ConversationActorId,
            document.ScopeId,
            document.StateVersion,
            document.ProgressSequence,
            ToDateTimeOffset(document.UpdatedAt) ?? DateTimeOffset.MinValue,
            ToTurn(document.ActiveTurn),
            ToTurn(document.LatestTurn),
            document.RecentTerminalTurns.Select(static turn => ToTurn(turn)!).ToArray(),
            ToTask(document.ActiveTask),
            ToPendingApproval(document.PendingApproval),
            document.PendingActions.Select(ToAction).ToArray(),
            ToControlFence(document.ControlFence),
            ToControlFence(document.LatestControlResult),
            ToContinuationAdmission(document.ContinuationAdmission),
            ToPendingInput(document.PendingInput),
            ToInputResolution(document.LatestInputResolution),
            ToApprovalResolution(document.LatestApprovalResolution),
            NullIfEmpty(document.TaskStatus),
            NullIfEmpty(document.AttentionKind),
            ToDateTimeOffset(document.AttentionSince),
            NullIfEmpty(document.ActiveStepSummary),
            document.RecentActions.Select(ToAction).ToArray(),
            ToStepControlResult(document.LatestStepControlResult),
            document.RecentStepControlResults.Select(result => ToStepControlResult(result)!).ToArray(),
            ToCanaryEffectFault(document.CanaryEffectFault),
            ToContextAttachments(document.ContextAttachments),
            ToPendingWorkflowSignal(document.PendingWorkflowSignal));

    private static NyxIdChatPendingWorkflowSignalSnapshot? ToPendingWorkflowSignal(
        NyxIdChatConversationPendingWorkflowSignalDocument? signal) =>
        signal is null
            ? null
            : new NyxIdChatPendingWorkflowSignalSnapshot(
                signal.ActorId,
                signal.RunId,
                signal.SignalName,
                NullIfEmpty(signal.StepId),
                NullIfEmpty(signal.Prompt),
                signal.TimeoutMs,
                ToDateTimeOffset(signal.ObservedAt));

    private static IReadOnlyList<NyxIdChatConversationContextAttachmentSnapshot> ToContextAttachments(
        IEnumerable<NyxIdChatConversationContextAttachmentDocument> attachments) =>
        attachments.Select(static attachment =>
                new NyxIdChatConversationContextAttachmentSnapshot(
                    attachment.ArtifactId,
                    attachment.RevisionMode,
                    attachment.PinnedRevisionId))
            .ToArray();

    private static NyxIdChatCanaryEffectFaultSnapshot? ToCanaryEffectFault(
        NyxIdChatConversationCanaryEffectFaultDocument? fault) =>
        fault is null
            ? null
            : new NyxIdChatCanaryEffectFaultSnapshot(
                fault.ArmId,
                fault.Status,
                ToCanaryOperation(fault.SourceOperation),
                fault.TargetOperation is null
                    ? null
                    : ToCanaryOperation(fault.TargetOperation),
                ToDateTimeOffset(fault.ExpiresAt),
                ToDateTimeOffset(fault.ArmedAt),
                ToDateTimeOffset(fault.ForwardedAt),
                ToDateTimeOffset(fault.ConsumedAt));

    private static NyxIdChatCanaryOperationSnapshot ToCanaryOperation(
        NyxIdChatConversationCanaryOperationDocument operation) =>
        new(
            operation.ConversationActorId,
            operation.TurnId,
            operation.TaskId,
            operation.StepId,
            operation.OperationId,
            operation.OperationGeneration);

    private static NyxIdChatConversationTurnSnapshot? ToTurn(
        NyxIdChatConversationTurnDocument? turn) =>
        turn == null
            ? null
            : new NyxIdChatConversationTurnSnapshot(
                turn.TurnId,
                turn.TaskId,
                turn.Status,
                NullIfEmpty(turn.FailureCode),
                NullIfEmpty(turn.SafeMessage),
                ToDateTimeOffset(turn.CreatedAt),
                ToDateTimeOffset(turn.TerminalAt),
                NullIfEmpty(turn.CommandId));

    private static NyxIdChatConversationTaskSnapshot? ToTask(
        NyxIdChatConversationTaskDocument? task) =>
        task == null
            ? null
            : new NyxIdChatConversationTaskSnapshot(
                task.TaskId,
                task.TurnId,
                task.Status,
                NullIfEmpty(task.ActiveStepId),
                NullIfEmpty(task.ActiveOperationId),
                NullIfEmpty(task.FailureCode),
                NullIfEmpty(task.SafeMessage),
                ToDateTimeOffset(task.CreatedAt),
                ToDateTimeOffset(task.UpdatedAt),
                task.Steps.Select(ToStep).ToArray(),
                task.SchemaVersion,
                NullIfEmpty(task.ActorId),
                NullIfEmpty(task.PlanId),
                task.PlanRevision,
                NullIfEmpty(task.Title),
                task.PlanRevisions.Select(static revision =>
                    new NyxIdChatConversationPlanRevisionSnapshot(
                        revision.PlanRevision,
                        revision.RevisionCause,
                        ToDateTimeOffset(revision.CommittedAt),
                        revision.AddedStepIds.ToArray(),
                        revision.CancelledStepIds.ToArray())).ToArray(),
                task.PlanRevisionHistoryStart);

    private static NyxIdChatConversationStepSnapshot ToStep(
        NyxIdChatConversationStepDocument step) =>
        new(
            step.StepId,
            step.Order,
            step.Kind,
            step.Status,
            step.Required,
            NullIfEmpty(step.Description),
            step.MayChangeExternalState,
            step.ExternalEffect,
            NullIfEmpty(step.ApprovalRequestId),
            NullIfEmpty(step.ActionRequestId),
            NullIfEmpty(step.FailureCode),
            NullIfEmpty(step.SafeMessage),
            step.SafeToSkip,
            step.AvailableActions == null
                ? null
                : new NyxIdChatAvailableActionsSnapshot(
                    step.AvailableActions.Retry,
                    step.AvailableActions.Skip,
                    step.AvailableActions.Stop),
            ToDateTimeOffset(step.UpdatedAt),
            ToOperation(step.Operation),
            ToSource(step.Source),
            NullIfEmpty(step.AddedBy),
            step.DependsOn.ToArray(),
            step.Estimate == null
                ? null
                : new NyxIdChatConversationStepEstimateSnapshot(
                    step.Estimate.Kind,
                    step.Estimate.Seconds),
            step.Substeps.Select(static substep =>
                new NyxIdChatConversationSubstepSnapshot(
                    substep.SubstepId,
                    substep.Title,
                    substep.Status)).ToArray(),
            step.AddedInPlanRevision,
            step.CancelledInPlanRevision,
            step.ApprovalObservation == null
                ? null
                : new NyxIdChatPostReturnApprovalObservationSnapshot(
                    step.ApprovalObservation.ApprovalRequestId,
                    step.ApprovalObservation.DecisionMode,
                    step.ApprovalObservation.ReceiptStatus,
                    ToDateTimeOffset(step.ApprovalObservation.ObservedAt),
                    NullIfEmpty(step.ApprovalObservation.TerminalOutcome),
                    NullIfEmpty(step.ApprovalObservation.SubjectKind)),
            step.Guard == null
                ? null
                : new NyxIdChatStepGuardSnapshot(
                    step.Guard.ConditionStepId,
                    step.Guard.RequiredOutcome));

    private static NyxIdChatConversationStepSourceSnapshot? ToSource(
        NyxIdChatConversationStepSourceDocument? source) =>
        source?.SourceCase switch
        {
            NyxIdChatConversationStepSourceDocument.SourceOneofCase.Llm =>
                new NyxIdChatConversationStepSourceSnapshot(
                    Llm: new NyxIdChatLLMStepSourceSnapshot(source.Llm.Model)),
            NyxIdChatConversationStepSourceDocument.SourceOneofCase.Tool =>
                new NyxIdChatConversationStepSourceSnapshot(
                    Tool: new NyxIdChatToolStepSourceSnapshot(
                        source.Tool.ToolName,
                        NullIfEmpty(source.Tool.ServiceSlug),
                        NullIfEmpty(source.Tool.ServiceId),
                        source.Tool.HasReadinessCapabilityId
                            ? NullIfEmpty(source.Tool.ReadinessCapabilityId)
                            : null,
                        NullIfEmpty(source.Tool.ProviderResourceId),
                        source.Tool.Presentation?.Clone())),
            NyxIdChatConversationStepSourceDocument.SourceOneofCase.BrowserAction =>
                new NyxIdChatConversationStepSourceSnapshot(
                    BrowserAction: new NyxIdChatBrowserActionStepSourceSnapshot(
                        source.BrowserAction.Action,
                        NullIfEmpty(source.BrowserAction.ActionRequestId))),
            NyxIdChatConversationStepSourceDocument.SourceOneofCase.Postcondition =>
                new NyxIdChatConversationStepSourceSnapshot(
                    Postcondition: new NyxIdChatPostconditionStepSourceSnapshot(
                        NullIfEmpty(source.Postcondition.ActionRequestId),
                        NullIfEmpty(source.Postcondition.Check),
                        NullIfEmpty(source.Postcondition.ProviderResourceId))),
            NyxIdChatConversationStepSourceDocument.SourceOneofCase.Input =>
                new NyxIdChatConversationStepSourceSnapshot(
                    Input: new NyxIdChatInputStepSourceSnapshot(
                        NullIfEmpty(source.Input.RequestId))),
            NyxIdChatConversationStepSourceDocument.SourceOneofCase.Approval =>
                new NyxIdChatConversationStepSourceSnapshot(
                    Approval: new NyxIdChatApprovalStepSourceSnapshot(
                        NullIfEmpty(source.Approval.ApprovalRequestId))),
            NyxIdChatConversationStepSourceDocument.SourceOneofCase.Web =>
                new NyxIdChatConversationStepSourceSnapshot(
                    Web: new NyxIdChatWebStepSourceSnapshot()),
            NyxIdChatConversationStepSourceDocument.SourceOneofCase.Condition =>
                new NyxIdChatConversationStepSourceSnapshot(
                    Condition: new NyxIdChatConditionStepSourceSnapshot(
                        new NyxIdChatNumericConditionSnapshot(
                            source.Condition.Condition.ConditionId,
                            source.Condition.Condition.SourceInputRequestId,
                            source.Condition.Condition.SuggestedThreshold,
                            source.Condition.Condition.EffectiveThreshold,
                            source.Condition.Condition.ThresholdOrigin,
                            source.Condition.Condition.ObservedValue,
                            source.Condition.Condition.Comparison,
                            source.Condition.Condition.Outcome,
                            ToDateTimeOffset(source.Condition.Condition.EvaluatedAt),
                            source.Condition.Condition.GuardedToolName))),
            _ => null,
        };

    private static NyxIdChatConversationOperationSnapshot? ToOperation(
        NyxIdChatConversationOperationDocument? operation) =>
        operation == null
            ? null
            : new NyxIdChatConversationOperationSnapshot(
                operation.ConversationActorId,
                operation.TurnId,
                operation.TaskId,
                operation.StepId,
                operation.OperationId,
                operation.OperationGeneration,
                operation.Kind,
                operation.Phase,
                operation.MayChangeExternalState,
                operation.Idempotent,
                operation.LatestProgressSequence,
                NullIfEmpty(operation.TerminalCode),
                NullIfEmpty(operation.SafeMessage),
                ToDateTimeOffset(operation.RequestedAt),
                ToDateTimeOffset(operation.DispatchedAt),
                ToDateTimeOffset(operation.CompletedAt),
                ToDateTimeOffset(operation.LastProgressAt),
                ToDateTimeOffset(operation.StalledAt));

    private static NyxIdChatPendingApprovalSnapshot? ToPendingApproval(
        NyxIdChatConversationPendingApprovalDocument? approval) =>
        approval == null
            ? null
            : new NyxIdChatPendingApprovalSnapshot(
                approval.ApprovalRequestId,
                approval.TurnId,
                approval.TaskId,
                approval.StepId,
                approval.ToolName,
                ToDateTimeOffset(approval.ExpiresAt),
                ToDateTimeOffset(approval.AskedAt),
                NullIfEmpty(approval.Action),
                NullIfEmpty(approval.Target),
                NullIfEmpty(approval.ActorLabel),
                NullIfEmpty(approval.Reversibility),
                NullIfEmpty(approval.GrantBoundary),
                NullIfEmpty(approval.NyxidRequestId));

    private static NyxIdChatPendingInputSnapshot? ToPendingInput(
        NyxIdChatConversationPendingInputDocument? input) =>
        input == null
            ? null
            : new NyxIdChatPendingInputSnapshot(
                input.RequestId,
                input.TurnId,
                input.TaskId,
                input.StepId,
                input.Prompt,
                input.Options.Select(static option =>
                    new NyxIdChatInputOptionSnapshot(
                        option.OptionId,
                        option.Label,
                        NullIfEmpty(option.Description))).ToArray(),
                ToDateTimeOffset(input.AskedAt),
                input.AllowFreeText,
                input.MultiSelect,
                input.NumericThreshold == null
                    ? null
                    : new NyxIdChatNumericThresholdInputSnapshot(
                        input.NumericThreshold.SuggestedValue,
                        input.NumericThreshold.MinimumValue,
                        input.NumericThreshold.MaximumValue));

    private static NyxIdChatInputResolutionSnapshot? ToInputResolution(
        NyxIdChatConversationInputResolutionDocument? resolution) =>
        resolution == null
            ? null
            : new NyxIdChatInputResolutionSnapshot(
                resolution.RequestId,
                resolution.ClientRequestId,
                resolution.Outcome,
                ToDateTimeOffset(resolution.CommittedAt),
                resolution.NumericThreshold == null
                    ? null
                    : new NyxIdChatNumericThresholdResolutionSnapshot(
                        resolution.NumericThreshold.SuggestedValue,
                        resolution.NumericThreshold.EffectiveValue,
                        resolution.NumericThreshold.Origin),
                resolution.Answer?.AnswerCase switch
                {
                    NyxIdChatConversationInputAnswerDocument.AnswerOneofCase.FreeText =>
                        new NyxIdChatInputAnswerSnapshot(
                            FreeText: resolution.Answer.FreeText),
                    NyxIdChatConversationInputAnswerDocument.AnswerOneofCase.Selection =>
                        new NyxIdChatInputAnswerSnapshot(
                            Selection: new NyxIdChatInputSelectionAnswerSnapshot(
                                resolution.Answer.Selection.OptionIds.ToArray())),
                    _ => null,
                });

    private static NyxIdChatApprovalResolutionSnapshot? ToApprovalResolution(
        NyxIdChatConversationApprovalResolutionDocument? resolution) =>
        resolution == null
            ? null
            : new NyxIdChatApprovalResolutionSnapshot(
                resolution.RequestId,
                resolution.ClientRequestId,
                resolution.Outcome,
                resolution.Approved,
                ToDateTimeOffset(resolution.CommittedAt));

    private static NyxIdChatControlFenceSnapshot? ToControlFence(
        NyxIdChatConversationControlFenceDocument? fence) =>
        fence == null
            ? null
            : new NyxIdChatControlFenceSnapshot(
                fence.Kind,
                fence.RequestId,
                fence.ClientRequestId,
                fence.TurnId,
                fence.TaskId,
                fence.OperationGeneration,
                fence.Outcome,
                NullIfEmpty(fence.ReasonCode),
                NullIfEmpty(fence.SafeMessage),
                ToDateTimeOffset(fence.CommittedAt));

    private static NyxIdChatContinuationAdmissionSnapshot? ToContinuationAdmission(
        NyxIdChatConversationContinuationAdmissionDocument? admission) =>
        admission == null
            ? null
            : new NyxIdChatContinuationAdmissionSnapshot(
                admission.Kind,
                admission.RequestId,
                admission.ClientRequestId,
                admission.OriginTurnId,
                admission.ContinuationTurnId,
                admission.Status,
                NullIfEmpty(admission.ReasonCode),
                NullIfEmpty(admission.SafeMessage),
                ToDateTimeOffset(admission.CommittedAt));

    private static NyxIdChatStepControlResultSnapshot? ToStepControlResult(
        NyxIdChatConversationStepControlResultDocument? result) =>
        result == null
            ? null
            : new NyxIdChatStepControlResultSnapshot(
                result.Kind,
                result.RequestId,
                result.ClientRequestId,
                result.TurnId,
                result.TaskId,
                result.StepId,
                result.ExpectedOperationGeneration,
                result.OperationGeneration,
                result.Outcome,
                NullIfEmpty(result.ReasonCode),
                NullIfEmpty(result.SafeMessage),
                result.CommandId,
                result.CorrelationId,
                ToDateTimeOffset(result.CommittedAt),
                result.ExpectedStateVersion,
                result.ScopeId,
                result.ConversationActorId);

    private static NyxIdChatActionSnapshot ToAction(
        NyxIdChatConversationActionDocument action) =>
        new(
            action.SchemaVersion,
            action.ActionRequestId,
            action.OriginTurnId,
            action.TaskId,
            action.StepId,
            action.Action,
            ToDateTimeOffset(action.RequestedAt),
            action.Reports.Select(ToActionReport).ToArray(),
            ToPostcondition(action.PostconditionResult),
            ToActionRequest(action.Request));

    private static NyxIdChatActionRequestSnapshot? ToActionRequest(
        NyxIdChatConversationActionRequestDocument? request)
    {
        if (request?.Params is null)
            return null;
        var parameters = request.Params.ParamsCase switch
        {
            NyxIdChatConversationActionParamsDocument.ParamsOneofCase.CatalogService =>
                new NyxIdChatActionParamsSnapshot(
                    CatalogService: new NyxIdChatCatalogServiceConnectSnapshot(
                        request.Params.CatalogService.ServiceSlug,
                        request.Params.CatalogService.RequestedScopes.ToArray(),
                        NullIfEmpty(request.Params.CatalogService.ViaNodeId),
                        NullIfEmpty(request.Params.CatalogService.TargetOrgId))),
            NyxIdChatConversationActionParamsDocument.ParamsOneofCase.CustomService =>
                new NyxIdChatActionParamsSnapshot(
                    CustomService: new NyxIdChatCustomServiceConnectSnapshot(
                        request.Params.CustomService.Name,
                        request.Params.CustomService.EndpointUrl,
                        request.Params.CustomService.AuthMethod,
                        NullIfEmpty(request.Params.CustomService.AuthKeyName),
                        NullIfEmpty(request.Params.CustomService.ViaNodeId),
                        NullIfEmpty(request.Params.CustomService.TargetOrgId))),
            NyxIdChatConversationActionParamsDocument.ParamsOneofCase.KeyCreate =>
                new NyxIdChatActionParamsSnapshot(
                    Name: request.Params.KeyCreate.Name,
                    Platform: request.Params.KeyCreate.Platform,
                    AllowedServiceIds: request.Params.KeyCreate.AllowedServiceIds.ToArray()),
            NyxIdChatConversationActionParamsDocument.ParamsOneofCase.KeyRotate =>
                new NyxIdChatActionParamsSnapshot(
                    KeyId: request.Params.KeyRotate.KeyId),
            NyxIdChatConversationActionParamsDocument.ParamsOneofCase.ServiceAccessReview =>
                new NyxIdChatActionParamsSnapshot(
                    ServiceAccessReview: new NyxIdChatServiceAccessReviewSnapshot(
                        request.Params.ServiceAccessReview.UserServiceId,
                        request.Params.ServiceAccessReview.ServiceSlug,
                        request.Params.ServiceAccessReview.ResourceUri)),
            _ => null,
        };
        return parameters is null
            ? null
            : new NyxIdChatActionRequestSnapshot(
                request.SchemaVersion,
                request.ActorId,
                request.OriginTurnId,
                request.TaskId,
                request.StepId,
                request.ActionRequestId,
                request.Action,
                parameters);
    }

    private static NyxIdChatActionReportSnapshot ToActionReport(
        NyxIdChatConversationActionReportDocument report) =>
        new(
            report.ActionRequestId,
            report.OriginTurnId,
            report.Disposition,
            ToResource(report.Resource),
            NullIfEmpty(report.SafeMessage),
            ToDateTimeOffset(report.ReportedAt));

    private static NyxIdChatActionPostconditionSnapshot? ToPostcondition(
        NyxIdChatConversationActionPostconditionDocument? result) =>
        result == null
            ? null
            : new NyxIdChatActionPostconditionSnapshot(
                result.ActionRequestId,
                result.Disposition,
                result.Verified,
                ToResource(result.Resource),
                NullIfEmpty(result.FailureCode),
                NullIfEmpty(result.SafeMessage));

    private static NyxIdChatResourceSnapshot? ToResource(
        NyxIdChatConversationResourceDocument? resource)
    {
        if (resource == null ||
            resource.ResourceCase == NyxIdChatConversationResourceDocument.ResourceOneofCase.None)
        {
            return null;
        }

        return new NyxIdChatResourceSnapshot(
            NullIfEmpty(resource.UserServiceId),
            NullIfEmpty(resource.KeyId),
            NullIfEmpty(resource.NodeId),
            NullIfEmpty(resource.ServiceAccountId),
            NullIfEmpty(resource.ClientId),
            NullIfEmpty(resource.DeviceId));
    }

    private static string? ResolveTurnId(
        NyxIdChatConversationCurrentStateDocument document) =>
        NormalizeOptional(document.ActiveTurn?.TurnId) ??
        NormalizeOptional(document.LatestTurn?.TurnId);

    private static string? NormalizeIdentity(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized == null || normalized.Length > 256)
            return null;

        return normalized.All(static character =>
                !char.IsControl(character) &&
                !char.IsWhiteSpace(character) &&
                character is not '/' and not '\\' and not '?' and not '#')
            ? normalized
            : null;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static DateTimeOffset? ToDateTimeOffset(Timestamp? timestamp) =>
        timestamp?.ToDateTimeOffset();
}
