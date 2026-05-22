using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter16/cluster-meta-studio-actor-substrate):
//   Old: workspace mutations were coupled to the concrete local JSON workspace store.
//   New principle: application services depend on a command port that dispatches typed workspace events to the authoritative workspace actor.
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
