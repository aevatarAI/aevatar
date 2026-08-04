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
        {
            return NyxIdChatConversationStateQueryResult.ReloadRequired(
                document.StateVersion,
                serverTurnId,
                "scope_mismatch");
        }

        if (!string.Equals(document.Id, actorId, StringComparison.Ordinal) ||
            !string.Equals(document.ActorId, actorId, StringComparison.Ordinal) ||
            !string.Equals(document.ConversationActorId, actorId, StringComparison.Ordinal))
        {
            return NyxIdChatConversationStateQueryResult.ReloadRequired(
                document.StateVersion,
                serverTurnId,
                "conversation_mismatch");
        }

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
                document.StateVersion > 0)
            .ToDictionary(
                static document => document.ConversationActorId,
                static document => new NyxIdChatConversationAttentionSummary(
                    document.ConversationActorId,
                    document.TaskStatus,
                    document.AttentionKind,
                    ToDateTimeOffset(document.AttentionSince),
                    NullIfEmpty(document.ActiveStepSummary),
                    document.StateVersion),
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
            NullIfEmpty(document.ActiveStepSummary));

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
                task.Steps.Select(ToStep).ToArray());

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
            new NyxIdChatAvailableActionsSnapshot(
                step.AvailableActions?.Retry ?? false,
                step.AvailableActions?.Skip ?? false,
                step.AvailableActions?.Stop ?? false),
            ToDateTimeOffset(step.UpdatedAt),
            ToOperation(step.Operation));

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
                ToDateTimeOffset(operation.CompletedAt));

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
                input.MultiSelect);

    private static NyxIdChatInputResolutionSnapshot? ToInputResolution(
        NyxIdChatConversationInputResolutionDocument? resolution) =>
        resolution == null
            ? null
            : new NyxIdChatInputResolutionSnapshot(
                resolution.RequestId,
                resolution.ClientRequestId,
                resolution.Outcome,
                ToDateTimeOffset(resolution.CommittedAt));

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
            ToPostcondition(action.PostconditionResult));

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
