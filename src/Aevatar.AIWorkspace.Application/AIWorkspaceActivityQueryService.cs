using Aevatar.AIWorkspace.Application.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Security;
using Aevatar.Workflow.Application.Abstractions.Observatory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AIWorkspace.Application;

public sealed class AIWorkspaceActivityQueryService(
    IChatHistoryQueryPort chatHistory,
    IWorkflowRunObservatoryQueryService observatory,
    ILogger<AIWorkspaceActivityQueryService>? logger = null)
    : IAIWorkspaceActivityQueryService
{
    private const string LegacyChatRunOrigin = "chat";
    private const int RunInputSummaryMaxLength = 240;
    private const int RunStepInputSummaryMaxLength = 160;
    private const int RunFailureMessageMaxLength = 240;

    private readonly ILogger<AIWorkspaceActivityQueryService> _logger =
        logger ?? NullLogger<AIWorkspaceActivityQueryService>.Instance;

    public async Task<AIWorkspaceQueryResult<AIWorkspaceActivityView>> QueryAsync(
        string scopeId,
        AIWorkspaceActivityQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!AIWorkspaceQueryPolicy.IsValidPageSize(query.Take))
            return AIWorkspaceQueryPolicy.InvalidPageSize<AIWorkspaceActivityView>();

        try
        {
            var conversationsTask = ReadConversationsAsync(
                scopeId,
                new AIWorkspacePageQuery(query.Take, query.ConversationCursor),
                ct);
            var runsTask = ReadRunsAsync(
                scopeId,
                new AIWorkspaceRunsQuery(Take: query.Take, Cursor: query.RunCursor),
                ct);
            await Task.WhenAll(conversationsTask, runsTask).ConfigureAwait(false);
            return AIWorkspaceQueryResult<AIWorkspaceActivityView>.Success(new AIWorkspaceActivityView(
                "independent_read_models",
                await conversationsTask.ConfigureAwait(false),
                await runsTask.ConfigureAwait(false)));
        }
        catch (AIWorkspaceCursorException ex)
        {
            return InvalidCursor<AIWorkspaceActivityView>(ex.Message);
        }
    }

    public async Task<AIWorkspaceQueryResult<AIWorkspaceConversationCollectionView>> QueryConversationsAsync(
        string scopeId,
        AIWorkspacePageQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!AIWorkspaceQueryPolicy.IsValidPageSize(query.Take))
            return AIWorkspaceQueryPolicy.InvalidPageSize<AIWorkspaceConversationCollectionView>();

        try
        {
            return AIWorkspaceQueryResult<AIWorkspaceConversationCollectionView>.Success(
                await ReadConversationsAsync(scopeId, query, ct).ConfigureAwait(false));
        }
        catch (AIWorkspaceCursorException ex)
        {
            return InvalidCursor<AIWorkspaceConversationCollectionView>(ex.Message);
        }
    }

    public async Task<AIWorkspaceQueryResult<AIWorkspaceRunCollectionView>> QueryRunsAsync(
        string scopeId,
        AIWorkspaceRunsQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!AIWorkspaceQueryPolicy.IsValidPageSize(query.Take))
            return AIWorkspaceQueryPolicy.InvalidPageSize<AIWorkspaceRunCollectionView>();

        try
        {
            return AIWorkspaceQueryResult<AIWorkspaceRunCollectionView>.Success(
                await ReadRunsAsync(scopeId, query, ct).ConfigureAwait(false));
        }
        catch (AIWorkspaceCursorException ex)
        {
            return InvalidCursor<AIWorkspaceRunCollectionView>(ex.Message);
        }
    }

    public async Task<AIWorkspaceQueryResult<AIWorkspaceRunDetailView>> GetRunAsync(
        string scopeId,
        string runId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return AIWorkspaceQueryResult<AIWorkspaceRunDetailView>.Fail(
                AIWorkspaceQueryFailureKind.NotFound,
                "WORKFLOW_RUN_NOT_FOUND",
                "Workflow run was not found.");
        }

        try
        {
            var detail = await observatory.GetRunForScopeAsync(scopeId, runId.Trim(), ct)
                .ConfigureAwait(false);
            return detail is null
                ? AIWorkspaceQueryResult<AIWorkspaceRunDetailView>.Fail(
                    AIWorkspaceQueryFailureKind.NotFound,
                    "WORKFLOW_RUN_NOT_FOUND",
                    "Workflow run was not found.")
                : AIWorkspaceQueryResult<AIWorkspaceRunDetailView>.Success(ToRunDetail(detail));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AI workspace run detail source is unavailable for scope {ScopeId} and run {RunId}.",
                scopeId,
                runId.Trim());
            return AIWorkspaceQueryResult<AIWorkspaceRunDetailView>.Fail(
                AIWorkspaceQueryFailureKind.Unavailable,
                "WORKFLOW_RUNS_UNAVAILABLE",
                "Workflow run activity is temporarily unavailable.");
        }
    }

    private async Task<AIWorkspaceConversationCollectionView> ReadConversationsAsync(
        string scopeId,
        AIWorkspacePageQuery query,
        CancellationToken ct)
    {
        try
        {
            var page = await chatHistory.GetIndexAsync(
                new ChatHistoryIndexPageRequest(scopeId, query.Take, query.Cursor),
                ct).ConfigureAwait(false);
            return new AIWorkspaceConversationCollectionView(
                "chat_history",
                AIWorkspaceSourceAvailability.Available,
                page.Conversations.Select(ToConversation).ToArray(),
                page.NextCursor,
                null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ProjectionDocumentQueryCursorException) when (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            throw new AIWorkspaceCursorException("Conversation cursor is malformed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AI workspace conversation activity source is unavailable for scope {ScopeId}.",
                scopeId);
            return UnavailableConversations();
        }
    }

    private async Task<AIWorkspaceRunCollectionView> ReadRunsAsync(
        string scopeId,
        AIWorkspaceRunsQuery query,
        CancellationToken ct)
    {
        try
        {
            var page = await observatory.ListActivityRunsForScopeAsync(
                scopeId,
                new WorkflowActivityRunFeedFilter
                {
                    Status = query.Status,
                    Origins = ToWorkflowRunOrigins(query.Origins),
                    WorkflowId = query.WorkflowId,
                    SearchText = query.SearchText,
                    FromUtc = query.FromUtc,
                    ToUtc = query.ToUtc,
                    Take = query.Take,
                    Cursor = query.Cursor,
                    IncludeTotalCount = query.IncludeTotalCount,
                },
                ct).ConfigureAwait(false);
            return new AIWorkspaceRunCollectionView(
                "workflow_run_observatory",
                AIWorkspaceSourceAvailability.Available,
                page.Items.Select(ToRunSummary).ToArray(),
                page.NextCursor,
                page.HasMore,
                page.TotalCount,
                null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ProjectionDocumentQueryCursorException) when (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            throw new AIWorkspaceCursorException("Workflow run cursor is malformed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AI workspace workflow run activity source is unavailable for scope {ScopeId}.",
                scopeId);
            return UnavailableRuns();
        }
    }

    internal static AIWorkspaceConversationCollectionView UnavailableConversations() =>
        new(
            "chat_history",
            AIWorkspaceSourceAvailability.Unavailable,
            [],
            null,
            new AIWorkspaceSourceErrorView(
                "CONVERSATIONS_UNAVAILABLE",
                "Conversation activity is temporarily unavailable."));

    internal static AIWorkspaceRunCollectionView UnavailableRuns() =>
        new(
            "workflow_run_observatory",
            AIWorkspaceSourceAvailability.Unavailable,
            [],
            null,
            false,
            null,
            new AIWorkspaceSourceErrorView(
                "WORKFLOW_RUNS_UNAVAILABLE",
                "Workflow run activity is temporarily unavailable."));

    private static AIWorkspaceConversationSummaryView ToConversation(ConversationMeta conversation) =>
        new(
            conversation.Id,
            conversation.Title,
            ToConversationKind(conversation.ServiceKind),
            conversation.CreatedAt,
            conversation.UpdatedAt,
            conversation.MessageCount,
            conversation.LlmRoute,
            conversation.LlmModel,
            conversation.TaskStatus,
            conversation.AttentionKind,
            conversation.AttentionSince,
            conversation.ActiveStepSummary,
            conversation.StateVersion);

    private static AIWorkspaceRunSummaryView ToRunSummary(WorkflowActivityRunFeedRow run) =>
        new(
            run.RunId,
            string.IsNullOrWhiteSpace(run.WorkflowId) ? null : run.WorkflowId,
            run.WorkflowName,
            run.Status,
            ToRunOrigin(run.RunOrigin),
            run.Success,
            WorkflowAuditTextSanitizer.SanitizeForDisplay(
                run.InputSummary,
                RunInputSummaryMaxLength),
            ToCurrentStep(run.CurrentStep),
            ToFailure(run.FirstFailure),
            ToWaiting(run.Waiting),
            run.StartedAtUtc,
            run.CompletedAtUtc,
            run.UpdatedAtUtc,
            run.DurationMs,
            run.StateVersion);

    private static AIWorkspaceRunDetailView ToRunDetail(ObservatoryRunDetail detail) =>
        new(
            "workflow_run_observatory",
            detail.Summary.StateVersion,
            detail.Summary.UpdatedAtUtc,
            EmptyToNull(detail.ReportVersion),
            ToSectionVersions(detail.Sections),
            new AIWorkspaceRunSummaryView(
                detail.Summary.RunId,
                string.IsNullOrWhiteSpace(detail.Summary.WorkflowId) ? null : detail.Summary.WorkflowId,
                detail.Summary.WorkflowName,
                detail.Summary.Status,
                ToRunOrigin(detail.Summary.RunOrigin),
                detail.Summary.Success,
                WorkflowAuditTextSanitizer.SanitizeForDisplay(
                    detail.InputSummary,
                    RunInputSummaryMaxLength),
                null,
                ToFailure(detail.FirstFailure),
                null,
                detail.Summary.StartedAtUtc,
                detail.Summary.CompletedAtUtc,
                detail.Summary.UpdatedAtUtc,
                detail.Summary.DurationMs,
                detail.Summary.StateVersion),
            WorkflowAuditTextSanitizer.SanitizeForStorage(detail.FinalOutput),
            detail.Steps.Select(ToStep).ToArray(),
            detail.Timeline.Select(ToTimelineEvent).ToArray(),
            detail.Operations.Select(ToOperation).ToArray(),
            ToStatistics(detail.Statistics),
            ToUsage(detail.UsageTotals));

    private static AIWorkspaceRunDetailSectionVersionsView ToSectionVersions(
        ObservatoryRunDetailSectionVersions sections) =>
        new(
            ToSectionVersion(sections.Overview),
            ToSectionVersion(sections.Steps),
            ToSectionVersion(sections.Timeline),
            ToSectionVersion(sections.ExecutionPath));

    private static AIWorkspaceRunDetailSectionVersionView ToSectionVersion(
        ObservatoryRunDetailSectionVersion section) =>
        new(
            section.DetailStateVersion,
            section.SourceStateVersion,
            section.VersionStatus switch
            {
                ObservatoryRunDetailSectionVersionStatus.Aligned =>
                    AIWorkspaceRunDetailSectionVersionStatus.Aligned,
                ObservatoryRunDetailSectionVersionStatus.Unavailable =>
                    AIWorkspaceRunDetailSectionVersionStatus.Unavailable,
                ObservatoryRunDetailSectionVersionStatus.VersionMismatch =>
                    AIWorkspaceRunDetailSectionVersionStatus.VersionMismatch,
                ObservatoryRunDetailSectionVersionStatus.Disabled =>
                    AIWorkspaceRunDetailSectionVersionStatus.Disabled,
                _ => AIWorkspaceRunDetailSectionVersionStatus.Unknown,
            },
            EmptyToNull(section.Reason));

    private static AIWorkspaceRunStepSummaryView? ToCurrentStep(WorkflowActivityRunStepSummary step) =>
        string.Equals(step.Availability, "unavailable", StringComparison.Ordinal)
            ? null
            : new AIWorkspaceRunStepSummaryView(
                step.StepId,
                WorkflowAuditTextSanitizer.SanitizeForDisplay(
                    step.InputSummary,
                    RunStepInputSummaryMaxLength),
                step.Availability);

    private static AIWorkspaceRunFailureSummaryView? ToFailure(WorkflowActivityRunFailureSummary failure) =>
        string.Equals(failure.Availability, "unavailable", StringComparison.Ordinal)
            ? null
            : new AIWorkspaceRunFailureSummaryView(
                failure.StepId,
                WorkflowAuditTextSanitizer.SanitizeForDisplay(
                    failure.Message,
                    RunFailureMessageMaxLength),
                failure.Availability);

    private static AIWorkspaceRunWaitingSummaryView? ToWaiting(WorkflowActivityRunWaitingSummary waiting) =>
        string.Equals(waiting.Availability, "unavailable", StringComparison.Ordinal)
            ? null
            : new AIWorkspaceRunWaitingSummaryView(
                waiting.StepId,
                waiting.WaitingKind,
                waiting.Availability);

    private static AIWorkspaceRunStepDetailView ToStep(ObservatoryStepDetail step) =>
        new(
            step.StepId,
            step.DisplayName,
            step.RequestedAtUtc,
            step.CompletedAtUtc,
            step.Success,
            step.Outcome.ToString().ToLowerInvariant(),
            step.DurationMs,
            step.FailureOutputTruncated,
            step.NextStepId,
            step.BranchKey,
            step.SuspensionType,
            step.SuspensionTimeoutSeconds,
            ToUsage(step.Usage));

    private static AIWorkspaceRunTimelineEventView ToTimelineEvent(ObservatoryViewEvent evt) =>
        new(
            evt.Kind,
            evt.TimestampUtc,
            evt.Stage,
            evt.StepId,
            evt.ToolCall is null
                ? null
                : new AIWorkspaceRunToolCallView(
                    evt.ToolCall.ToolName,
                    evt.ToolCall.CallId,
                    evt.ToolCall.Success));

    private static AIWorkspaceRunOperationView ToOperation(ObservatoryOperationDetail operation) =>
        new(
            operation.OperationId,
            operation.Kind,
            operation.StartedAtUtc,
            operation.CompletedAtUtc,
            operation.Model,
            operation.Provider,
            operation.AvailableToolNames,
            operation.FinishReason,
            ToUsage(operation.Usage),
            operation.Success,
            operation.ToolCallId,
            operation.ToolName,
            operation.DurationMs);

    private static AIWorkspaceRunStatisticsView ToStatistics(ObservatoryRunStatistics statistics) =>
        new(
            statistics.TotalSteps,
            statistics.RequestedSteps,
            statistics.CompletedSteps,
            statistics.RoleReplyCount,
            statistics.StepTypeCounts);

    private static AIWorkspaceUsageTotalsView ToUsage(ObservatoryUsageTotals usage) =>
        new(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens, usage.Cost);

    private static AIWorkspaceConversationKind ToConversationKind(string? serviceKind) =>
        serviceKind?.Trim() switch
        {
            "assistant" or "nyxid.chat" => AIWorkspaceConversationKind.Assistant,
            "workflow" => AIWorkspaceConversationKind.Workflow,
            _ => AIWorkspaceConversationKind.Other,
        };

    private static AIWorkspaceRunOrigin ToRunOrigin(string? runOrigin) =>
        runOrigin?.Trim() switch
        {
            WorkflowRunOrigins.Draft => AIWorkspaceRunOrigin.Development,
            WorkflowRunOrigins.MemberInvoke or
                WorkflowRunOrigins.DefaultInvoke or
                WorkflowRunOrigins.TeamInvoke or
                WorkflowRunOrigins.AdHocChat or
                LegacyChatRunOrigin => AIWorkspaceRunOrigin.Interactive,
            WorkflowRunOrigins.ServiceInvoke or
                WorkflowRunOrigins.Webhook => AIWorkspaceRunOrigin.Integration,
            WorkflowRunOrigins.WorkOrder or
                WorkflowRunOrigins.Provisioned => AIWorkspaceRunOrigin.Automation,
            _ => AIWorkspaceRunOrigin.Other,
        };

    private static IReadOnlyList<string> ToWorkflowRunOrigins(
        IReadOnlyList<AIWorkspaceRunOriginFilter>? origins) =>
        origins is null
            ? []
            : origins
                .SelectMany(static origin => ToWorkflowRunOrigins(origin))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    private static IReadOnlyList<string> ToWorkflowRunOrigins(AIWorkspaceRunOriginFilter origin) =>
        origin switch
        {
            AIWorkspaceRunOriginFilter.Interactive =>
            [
                WorkflowRunOrigins.MemberInvoke,
                WorkflowRunOrigins.DefaultInvoke,
                WorkflowRunOrigins.TeamInvoke,
                WorkflowRunOrigins.AdHocChat,
                LegacyChatRunOrigin,
            ],
            AIWorkspaceRunOriginFilter.Integration =>
            [WorkflowRunOrigins.ServiceInvoke, WorkflowRunOrigins.Webhook],
            AIWorkspaceRunOriginFilter.Automation =>
            [WorkflowRunOrigins.WorkOrder, WorkflowRunOrigins.Provisioned],
            AIWorkspaceRunOriginFilter.Development => [WorkflowRunOrigins.Draft],
            _ => [],
        };

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static AIWorkspaceQueryResult<T> InvalidCursor<T>(string message) =>
        AIWorkspaceQueryResult<T>.Fail(
            AIWorkspaceQueryFailureKind.InvalidCursor,
            "INVALID_CURSOR",
            message);

    private sealed class AIWorkspaceCursorException(string message) : ArgumentException(message);
}
