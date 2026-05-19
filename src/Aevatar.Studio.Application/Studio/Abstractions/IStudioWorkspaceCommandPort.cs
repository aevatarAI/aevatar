using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Abstractions;

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

    Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
        string workflowId,
        long? expectedVersion = null,
        CancellationToken ct = default);
}

public sealed record StudioWorkspaceCommandReceipt(
    string WorkspaceId,
    string ActorId,
    string CommandId,
    long? ExpectedVersion);
