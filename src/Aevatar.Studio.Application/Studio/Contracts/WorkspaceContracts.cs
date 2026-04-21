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

[Obsolete("Use WorkflowDraftSummary or WorkflowCommittedSummary.")]
public sealed record WorkflowSummary(
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

public sealed record WorkflowCommittedResponse(
    string WorkflowId,
    string Name,
    string Yaml,
    WorkflowDocument? Document,
    IReadOnlyList<ValidationFinding> Findings,
    DateTimeOffset? UpdatedAtUtc = null);

[Obsolete("Use WorkflowDraftResponse or WorkflowCommittedResponse.")]
public sealed record WorkflowFileResponse(
    string WorkflowId,
    string Name,
    string FileName,
    string FilePath,
    string DirectoryId,
    string DirectoryLabel,
    string Yaml,
    WorkflowDocument? Document,
    WorkflowLayoutDocument? Layout,
    IReadOnlyList<ValidationFinding> Findings,
    DateTimeOffset? UpdatedAtUtc = null);

public sealed record SaveWorkflowDraftRequest(
    string DirectoryId,
    string WorkflowName,
    string? FileName,
    string Yaml,
    WorkflowLayoutDocument? Layout = null);

[Obsolete("Use SaveWorkflowDraftRequest.")]
public sealed record SaveWorkflowFileRequest(
    string? WorkflowId,
    string DirectoryId,
    string WorkflowName,
    string? FileName,
    string Yaml,
    WorkflowLayoutDocument? Layout = null);
