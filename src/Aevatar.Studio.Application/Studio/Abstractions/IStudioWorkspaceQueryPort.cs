using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter16/cluster-meta-studio-actor-substrate):
//   Old: workspace reads could observe local store files that doubled as business state.
//   New principle: queries read the projected current-state replica for the workspace actor; write-side state is not side-read.
// Refactor (iter38/cluster-038-studio-workspace-reuse-existing):
//   Old pattern: Studio scoped workflow drafts 通过 ChronoStorage external storage authority + workspace ports routing 不一致(scopeId routing 显式 vs 隐藏)。
//   New principle: Delete ChronoStorage draft authority。Route scoped workflow drafts through existing IStudioWorkspaceCommandPort / IStudioWorkspaceQueryPort with explicit scopeId。**禁止** new IScopedStudioWorkspacePort / 新 scoped actor / 新 envelope / 新 projection phase / docs/canon change。
public interface IStudioWorkspaceQueryPort
{
    Task<StudioWorkspaceSnapshot> GetAsync(CancellationToken ct = default);

    Task<StudioWorkspaceSnapshot> GetAsync(string scopeId, CancellationToken ct = default) =>
        throw new NotImplementedException(
            "Explicit workspace scope routing must be implemented by the query port.");
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
