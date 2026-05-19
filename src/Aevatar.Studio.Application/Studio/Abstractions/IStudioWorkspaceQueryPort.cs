using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter16/cluster-meta-studio-actor-substrate):
//   Old: workspace reads could observe local store files that doubled as business state.
//   New principle: queries read the projected current-state replica for the workspace actor; write-side state is not side-read.
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
