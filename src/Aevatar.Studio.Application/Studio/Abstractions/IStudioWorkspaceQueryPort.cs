using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IStudioWorkspaceQueryPort
{
    Task<StudioWorkspaceSnapshot> GetAsync(CancellationToken ct = default);
}

public sealed record StudioWorkspaceSnapshot(
    string WorkspaceId,
    string ScopeId,
    StudioWorkspaceSettings Settings,
    IReadOnlyList<StudioWorkspaceDirectory> Directories,
    IReadOnlyList<StudioWorkflowDraftRecord> Drafts,
    long StateVersion,
    DateTimeOffset UpdatedAtUtc);

public sealed record StudioWorkflowDraftRecord(
    string WorkflowId,
    string Name,
    string FileName,
    string FilePath,
    string DirectoryId,
    string DirectoryLabel,
    string Yaml,
    WorkflowLayoutDocument? Layout,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset CreatedAtUtc,
    long Version);
