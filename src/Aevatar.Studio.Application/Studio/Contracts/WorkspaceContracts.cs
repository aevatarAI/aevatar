using System.Text.Json.Serialization;
using Aevatar.GAgentService.Abstractions;
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

public sealed record SaveWorkflowDraftRequest(
    string DirectoryId,
    string WorkflowName,
    string? FileName,
    string Yaml,
    WorkflowLayoutDocument? Layout = null)
{
    [JsonIgnore]
    public WorkflowCapabilityAdmissionContext? CapabilityAdmission { get; init; }
}
