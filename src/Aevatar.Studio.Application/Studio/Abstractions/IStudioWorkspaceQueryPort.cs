using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
//   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
//   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
public interface IStudioWorkspaceQueryPort
{
    Task<StudioWorkspaceSnapshot> GetAsync(CancellationToken ct = default);

    Task<StudioWorkspaceSnapshot> GetAsync(string scopeId, CancellationToken ct = default);
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
