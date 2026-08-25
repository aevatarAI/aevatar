using System.Text.Json.Serialization;

namespace Aevatar.AIWorkspace.Application.Abstractions;

[JsonConverter(typeof(JsonStringEnumConverter<AIWorkspaceSourceAvailability>))]
public enum AIWorkspaceSourceAvailability
{
    [JsonStringEnumMemberName("available")]
    Available = 0,

    [JsonStringEnumMemberName("not_materialized")]
    NotMaterialized = 1,

    [JsonStringEnumMemberName("unavailable")]
    Unavailable = 2,

    [JsonStringEnumMemberName("not_implemented")]
    NotImplemented = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<AIWorkspaceRunDetailSectionVersionStatus>))]
public enum AIWorkspaceRunDetailSectionVersionStatus
{
    [JsonStringEnumMemberName("unknown")]
    Unknown = 0,

    [JsonStringEnumMemberName("aligned")]
    Aligned = 1,

    [JsonStringEnumMemberName("unavailable")]
    Unavailable = 2,

    [JsonStringEnumMemberName("version_mismatch")]
    VersionMismatch = 3,

    [JsonStringEnumMemberName("disabled")]
    Disabled = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter<AIWorkspaceConversationKind>))]
public enum AIWorkspaceConversationKind
{
    [JsonStringEnumMemberName("assistant")]
    Assistant = 0,

    [JsonStringEnumMemberName("workflow")]
    Workflow = 1,

    [JsonStringEnumMemberName("other")]
    Other = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<AIWorkspaceRunOrigin>))]
public enum AIWorkspaceRunOrigin
{
    [JsonStringEnumMemberName("interactive")]
    Interactive = 0,

    [JsonStringEnumMemberName("integration")]
    Integration = 1,

    [JsonStringEnumMemberName("automation")]
    Automation = 2,

    [JsonStringEnumMemberName("development")]
    Development = 3,

    [JsonStringEnumMemberName("other")]
    Other = 4,
}

public sealed record AIWorkspaceSourceErrorView(string Code, string Message);

public sealed record AIWorkspaceAgentsView(
    string Consistency,
    AIWorkspaceAgentCollectionView Owned,
    AIWorkspaceAgentCollectionView SystemTemplates);

public sealed record AIWorkspaceAgentCollectionView(
    string Source,
    AIWorkspaceSourceAvailability Availability,
    IReadOnlyList<AIWorkspaceAgentSummaryView> Items,
    string? NextCursor,
    int? TotalCount,
    long? AuthorityStateVersion,
    DateTimeOffset? UpdatedAtUtc,
    AIWorkspaceSourceErrorView? Error);

public sealed record AIWorkspaceAgentSummaryView(
    string ProfileId,
    string ProfileSlug,
    string DisplayName,
    string Purpose,
    long PublishedRevision,
    string? PublishedSnapshotSha256,
    bool Published,
    string Status);

public sealed record AIWorkspaceModelsView(
    string Consistency,
    AIWorkspacePersonalModelsView PersonalDefault,
    AIWorkspaceCatalogModelsView Catalog);

public sealed record AIWorkspacePersonalModelsView(
    string Source,
    AIWorkspaceSourceAvailability Availability,
    long? AuthorityStateVersion,
    DateTimeOffset? UpdatedAtUtc,
    AIWorkspaceUserLlmSettingsView? Settings,
    AIWorkspaceSourceErrorView? Error);

public sealed record AIWorkspaceCatalogModelsView(
    string Source,
    AIWorkspaceSourceAvailability Availability,
    long? AuthorityStateVersion,
    DateTimeOffset? UpdatedAtUtc,
    AIWorkspaceModelCatalogPolicyView? Policy,
    AIWorkspaceSourceErrorView? Error);

public sealed record AIWorkspaceModelCatalogPolicyView(
    string Mode,
    bool Configured,
    IReadOnlyList<AIWorkspaceModelSourceView> Sources,
    string EffectiveSource,
    IReadOnlyList<AIWorkspaceModelSourceView> EffectiveSources,
    string? LastMutationId);

public sealed record AIWorkspaceModelSourceView(
    string SourceId,
    string? ServiceSlugSnapshot,
    string? CatalogServiceId,
    string? UserServiceId,
    string ModelSelectionMode,
    IReadOnlyList<string> ModelIds);

public sealed record AIWorkspaceUserLlmSettingsView(
    AIWorkspaceUserLlmSelectionView? SavedSelection,
    string SavedRouteLabel,
    string SelectionStatus,
    string CatalogDiagnostic,
    string Remediation,
    IReadOnlyList<AIWorkspaceUserLlmRouteOptionView> RouteOptions,
    IReadOnlyList<AIWorkspaceUserLlmModelGroupView> ModelGroupsByRoute,
    string CatalogStatus,
    AIWorkspaceUserLlmSettingsCapabilitiesView Capabilities,
    AIWorkspaceUserLlmSetupHintView? SetupHint);

public sealed record AIWorkspaceUserLlmSelectionView(
    string RouteKind,
    string RouteValue,
    string NyxIdUserServiceId,
    string ServiceSlugSnapshot,
    AIWorkspaceUserLlmModelSelectionView? ModelSelection);

public sealed record AIWorkspaceUserLlmModelSelectionView(string Kind, string? ModelId);

public sealed record AIWorkspaceUserLlmRouteOptionView(
    string RouteValue,
    string Label,
    string Source,
    string Status,
    bool Allowed,
    bool Ready,
    string? UserServiceId,
    string? ServiceSlug,
    AIWorkspaceUserLlmModelCatalogView ModelCatalog,
    string? Description);

public sealed record AIWorkspaceUserLlmModelCatalogView(
    string Certainty,
    IReadOnlyList<string> ModelIds,
    string? DefaultModelId,
    string Diagnostic);

public sealed record AIWorkspaceUserLlmModelGroupView(
    string RouteValue,
    string GroupId,
    string Label,
    IReadOnlyList<string> Models);

public sealed record AIWorkspaceUserLlmSettingsCapabilitiesView(
    bool CanEditRoute,
    bool CanEditModel,
    bool CanSave,
    bool CanRetryCatalog);

public sealed record AIWorkspaceUserLlmSetupHintView(
    string SetupUrl,
    IReadOnlyList<AIWorkspaceUserLlmPresetView> Presets);

public sealed record AIWorkspaceUserLlmPresetView(
    string Id,
    string Title,
    string Description,
    AIWorkspaceUserLlmPresetActivationView Activation);

public sealed record AIWorkspaceUserLlmPresetActivationView(
    string Type,
    string? UserServiceId,
    string? RouteValue,
    string? DefaultModel,
    string? ProvisionEndpointId);

public sealed record AIWorkspaceActivityView(
    string Consistency,
    AIWorkspaceConversationCollectionView Conversations,
    AIWorkspaceRunCollectionView Runs);

public sealed record AIWorkspaceConversationCollectionView(
    string Source,
    AIWorkspaceSourceAvailability Availability,
    IReadOnlyList<AIWorkspaceConversationSummaryView> Items,
    string? NextCursor,
    AIWorkspaceSourceErrorView? Error);

public sealed record AIWorkspaceConversationSummaryView(
    string ConversationId,
    string Title,
    AIWorkspaceConversationKind ConversationKind,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int MessageCount,
    string? LlmRoute,
    string? LlmModel,
    string? TaskStatus,
    string? AttentionKind,
    DateTimeOffset? AttentionSinceUtc,
    string? ActiveStepSummary,
    long AuthorityStateVersion);

public sealed record AIWorkspaceRunCollectionView(
    string Source,
    AIWorkspaceSourceAvailability Availability,
    IReadOnlyList<AIWorkspaceRunSummaryView> Items,
    string? NextCursor,
    bool HasMore,
    long? TotalCount,
    AIWorkspaceSourceErrorView? Error);

public sealed record AIWorkspaceRunSummaryView(
    string RunId,
    string? WorkflowId,
    string WorkflowName,
    string Status,
    AIWorkspaceRunOrigin RunOrigin,
    bool? Success,
    string InputSummary,
    AIWorkspaceRunStepSummaryView? CurrentStep,
    AIWorkspaceRunFailureSummaryView? FirstFailure,
    AIWorkspaceRunWaitingSummaryView? Waiting,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    double? DurationMs,
    long AuthorityStateVersion);

public sealed record AIWorkspaceRunStepSummaryView(
    string StepId,
    string InputSummary,
    string Availability);

public sealed record AIWorkspaceRunFailureSummaryView(
    string StepId,
    string Message,
    string Availability);

public sealed record AIWorkspaceRunWaitingSummaryView(
    string StepId,
    string WaitingKind,
    string Availability);

public sealed record AIWorkspaceRunDetailView(
    string Source,
    long AuthorityStateVersion,
    DateTimeOffset UpdatedAtUtc,
    string? ReportVersion,
    AIWorkspaceRunDetailSectionVersionsView Sections,
    AIWorkspaceRunSummaryView Summary,
    string FinalOutput,
    IReadOnlyList<AIWorkspaceRunStepDetailView> Steps,
    IReadOnlyList<AIWorkspaceRunTimelineEventView> Timeline,
    IReadOnlyList<AIWorkspaceRunOperationView> Operations,
    AIWorkspaceRunStatisticsView Statistics,
    AIWorkspaceUsageTotalsView UsageTotals);

public sealed record AIWorkspaceRunDetailSectionVersionsView(
    AIWorkspaceRunDetailSectionVersionView Overview,
    AIWorkspaceRunDetailSectionVersionView Steps,
    AIWorkspaceRunDetailSectionVersionView Timeline,
    AIWorkspaceRunDetailSectionVersionView ExecutionPath);

public sealed record AIWorkspaceRunDetailSectionVersionView(
    long DetailStateVersion,
    long SourceStateVersion,
    AIWorkspaceRunDetailSectionVersionStatus VersionStatus,
    string? Reason);

public sealed record AIWorkspaceRunStepDetailView(
    string StepId,
    string DisplayName,
    DateTimeOffset? RequestedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    bool? Success,
    string Outcome,
    double? DurationMs,
    bool FailureOutputTruncated,
    string NextStepId,
    string BranchKey,
    string SuspensionType,
    int? SuspensionTimeoutSeconds,
    AIWorkspaceUsageTotalsView Usage);

public sealed record AIWorkspaceRunTimelineEventView(
    string Kind,
    DateTimeOffset TimestampUtc,
    string Stage,
    string StepId,
    AIWorkspaceRunToolCallView? ToolCall);

public sealed record AIWorkspaceRunToolCallView(
    string ToolName,
    string CallId,
    bool Success);

public sealed record AIWorkspaceRunOperationView(
    string OperationId,
    string Kind,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string Model,
    string Provider,
    IReadOnlyList<string> AvailableToolNames,
    string FinishReason,
    AIWorkspaceUsageTotalsView Usage,
    bool? Success,
    string ToolCallId,
    string ToolName,
    double? DurationMs);

public sealed record AIWorkspaceRunStatisticsView(
    int TotalSteps,
    int RequestedSteps,
    int CompletedSteps,
    int RoleReplyCount,
    IReadOnlyDictionary<string, int> StepTypeCounts);

public sealed record AIWorkspaceUsageTotalsView(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    double Cost);

public sealed record AIWorkspaceOverviewView(
    string Consistency,
    AIWorkspaceOverviewAgentsView Agents,
    AIWorkspaceConversationCollectionView RecentConversations,
    AIWorkspaceRunCollectionView RecentRuns);

public sealed record AIWorkspaceOverviewAgentsView(
    AIWorkspaceOverviewSourceView Owned,
    AIWorkspaceOverviewSourceView SystemTemplates);

public sealed record AIWorkspaceOverviewSourceView(
    string Source,
    AIWorkspaceSourceAvailability Availability,
    int? ItemCount,
    long? AuthorityStateVersion,
    DateTimeOffset? UpdatedAtUtc,
    AIWorkspaceSourceErrorView? Error);
