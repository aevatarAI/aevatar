using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Contracts;

public sealed record WorkspaceSettingsResponse(
    string RuntimeBaseUrl,
    IReadOnlyList<WorkflowDirectorySummary> Directories);

public sealed record WorkflowDirectorySummary(
    string DirectoryId,
    string Label,
    string Path,
    bool IsBuiltIn);

public sealed record UpdateWorkspaceSettingsRequest(string RuntimeBaseUrl);

public sealed record AddWorkflowDirectoryRequest(string Path, string? Label = null);

public sealed record WorkflowDraftSummary(
    string WorkflowId,
    string Name,
    string Description,
    string FileName,
    string FilePath,
    string DirectoryId,
    string DirectoryLabel,
    int StepCount,
    bool HasLayout,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkflowCommittedSummary(
    string WorkflowId,
    string Name,
    string Description,
    int StepCount,
    DateTimeOffset? UpdatedAtUtc = null);

public sealed record WorkflowDraftResponse(
    string WorkflowId,
    string Name,
    string FileName,
    string FilePath,
    string DirectoryId,
    string DirectoryLabel,
    string Yaml,
    WorkflowLayoutDocument? Layout,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkflowDraftCreateAcceptedResponse(
    bool Accepted,
    string WorkflowId,
    string CommandId,
    string AckStage,
    string ActorId,
    string WorkspaceId,
    long? ExpectedVersion,
    DateTimeOffset AckedAtUtc,
    WorkflowDraftReadinessResponse Readiness);

public sealed record WorkflowDraftReadinessResponse(
    bool Readable,
    string Stage,
    string Message);

public sealed record WorkflowCommittedResponse(
    string WorkflowId,
    string Name,
    string Yaml,
    WorkflowDocument? Document,
    IReadOnlyList<ValidationFinding> Findings,
    DateTimeOffset? UpdatedAtUtc = null);

public sealed record ScopeWorkflowCatalogueQuery(
    string ScopeId,
    ScopeWorkflowCatalogueView View = ScopeWorkflowCatalogueView.All,
    string? Query = null,
    string? Cursor = null,
    int Take = 50);

public enum ScopeWorkflowCatalogueView
{
    All = 0,
    Drafts = 1,
    Archived = 2,
}

public sealed record ScopeWorkflowCatalogueResponse(
    IReadOnlyList<ScopeWorkflowCatalogueRow> Items,
    string? NextPageToken,
    ScopeWorkflowCatalogueFreshness Freshness,
    ScopeWorkflowCatalogueSearchContract Search);

public sealed record ScopeWorkflowCatalogueRow(
    string ScopeId,
    string WorkflowId,
    string Name,
    string Description,
    bool HasDraftSource,
    bool HasCommittedSource,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedAtSource,
    ScopeWorkflowCatalogueRowCapabilities Capabilities,
    DateTimeOffset SourceWatermarkUtc,
    ScopeWorkflowCatalogueCommittedFacts? Committed = null,
    string? PublishedServiceId = null);

public sealed record ScopeWorkflowCatalogueCommittedFacts(
    string ServiceKey,
    string WorkflowName,
    string ActorId,
    string ActiveRevisionId,
    string DeploymentId,
    string DeploymentStatus,
    string ServiceAppId,
    string ServiceNamespace);

public sealed record ScopeWorkflowCatalogueRowCapabilities(
    ScopeWorkflowCatalogueActionCapability Open,
    ScopeWorkflowCatalogueActionCapability Activity,
    ScopeWorkflowCatalogueActionCapability Rename,
    ScopeWorkflowCatalogueActionCapability Delete);

public sealed record ScopeWorkflowCatalogueActionCapability(
    bool Available,
    string? UnavailableReason = null);

public sealed record ScopeWorkflowCatalogueFreshness(
    DateTimeOffset? RefreshWatermarkUtc,
    string SourceVersionSemantics);

public sealed record ScopeWorkflowCatalogueSearchContract(
    IReadOnlyList<string> SearchableFields,
    string CaseSemantics,
    string UnicodeNormalization,
    int MaximumQueryLength,
    string EmptyQuerySemantics,
    string WorkflowIdSemantics);

public sealed record SaveWorkflowDraftRequest(
    string DirectoryId,
    string WorkflowName,
    string? FileName,
    string Yaml,
    WorkflowLayoutDocument? Layout = null);
