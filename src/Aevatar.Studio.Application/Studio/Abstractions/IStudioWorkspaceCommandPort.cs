using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
//   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
//   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
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
        CancellationToken ct = default);

    Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
        string workflowId,
        long? expectedVersion = null,
        CancellationToken ct = default);

    Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
        string scopeId,
        string workflowId,
        long? expectedVersion = null,
        CancellationToken ct = default);
}

public sealed record StudioWorkspaceCommandReceipt(
    string WorkspaceId,
    string ActorId,
    string CommandId,
    long? ExpectedVersion);
