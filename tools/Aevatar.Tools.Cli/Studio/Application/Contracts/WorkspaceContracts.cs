using Aevatar.Tools.Cli.Studio.Domain.Models;

namespace Aevatar.Tools.Cli.Studio.Application.Contracts;

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

public sealed record SaveWorkflowFileRequest(
    string? WorkflowId,
    string DirectoryId,
    string WorkflowName,
    string? FileName,
    string Yaml,
    WorkflowLayoutDocument? Layout = null);
