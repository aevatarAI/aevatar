using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter16/cluster-meta-studio-actor-substrate):
//   Old: workspace mutations were coupled to the concrete local JSON workspace store.
//   New principle: application services depend on a command port that dispatches typed workspace events to the authoritative workspace actor.
// Refactor (iter38/cluster-038-studio-workspace-reuse-existing):
//   Old pattern: Studio scoped workflow drafts 通过 ChronoStorage external storage authority + workspace ports routing 不一致(scopeId routing 显式 vs 隐藏)。
//   New principle: Delete ChronoStorage draft authority。Route scoped workflow drafts through existing IStudioWorkspaceCommandPort / IStudioWorkspaceQueryPort with explicit scopeId。**禁止** new IScopedStudioWorkspacePort / 新 scoped actor / 新 envelope / 新 projection phase / docs/canon change。
public interface IStudioWorkspaceCommandPort
{
    Task<StudioWorkspaceCommandReceipt> UpdateSettingsAsync(
        StudioWorkspaceSettings settings,
        long? expectedVersion = null,
        CancellationToken ct = default);

    Task<StudioWorkspaceCommandReceipt> AddDirectoryAsync(
        StudioWorkspaceDirectory directory,
        long? expectedVersion = null,
        CancellationToken ct = default);

    Task<StudioWorkspaceCommandReceipt> RemoveDirectoryAsync(
        string directoryId,
        long? expectedVersion = null,
        CancellationToken ct = default);

    Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(
        StudioWorkflowDraftRecord draft,
        long? expectedVersion = null,
        CancellationToken ct = default);

    Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(
        string scopeId,
        StudioWorkflowDraftRecord draft,
        long? expectedVersion = null,
        CancellationToken ct = default) =>
        SaveDraftAsync(draft, expectedVersion, ct);

    Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
        string workflowId,
        long? expectedVersion = null,
        CancellationToken ct = default);

    Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
        string scopeId,
        string workflowId,
        long? expectedVersion = null,
        CancellationToken ct = default) =>
        DeleteDraftAsync(workflowId, expectedVersion, ct);
}

public sealed record StudioWorkspaceCommandReceipt(
    string WorkspaceId,
    string ActorId,
    string CommandId,
    long? ExpectedVersion);
