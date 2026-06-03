using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Tests.Shared;

// Refactor (iter38/cluster-038-studio-workspace-reuse-existing):
//   Old pattern: Studio scoped workflow drafts 通过 ChronoStorage external storage authority + workspace ports routing 不一致(scopeId routing 显式 vs 隐藏)。
//   New principle: Delete ChronoStorage draft authority。Route scoped workflow drafts through existing IStudioWorkspaceCommandPort / IStudioWorkspaceQueryPort with explicit scopeId。**禁止** new IScopedStudioWorkspacePort / 新 scoped actor / 新 envelope / 新 projection phase / docs/canon change。
internal sealed class RecordingStudioWorkspacePorts : IStudioWorkspaceQueryPort, IStudioWorkspaceCommandPort
{
    private readonly Dictionary<string, Dictionary<string, StudioWorkflowDraftRecord>> _drafts =
        new(StringComparer.Ordinal);

    public RecordingStudioWorkspacePorts()
    {
    }

    public RecordingStudioWorkspacePorts(params StudioWorkflowDraftRecord[] drafts)
        : this(drafts.Select(static draft => new ScopedDraft("scope-1", draft)))
    {
    }

    public RecordingStudioWorkspacePorts(IEnumerable<ScopedDraft> drafts)
    {
        foreach (var draft in drafts)
        {
            GetOrCreateScope(draft.ScopeId)[draft.Draft.WorkflowId] = draft.Draft;
        }
    }

    public List<ScopedWorkflowUpload> SavedDrafts { get; } = [];

    public List<ScopedWorkflowDelete> DeletedDrafts { get; } = [];

    public List<string> QueriedScopes { get; } = [];

    public ScopedWorkflowUpload? LastUpload => SavedDrafts.LastOrDefault();

    public IReadOnlyList<ScopedWorkflowUpload> SavedWorkflows => SavedDrafts;

    public IReadOnlyList<ScopedWorkflowDelete> DeletedWorkflows => DeletedDrafts;

    public Task<StudioWorkspaceSnapshot> GetAsync(CancellationToken ct = default) =>
        GetAsync("scope-1", ct);

    public Task<StudioWorkspaceSnapshot> GetAsync(string scopeId, CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeScopeId(scopeId);
        QueriedScopes.Add(normalizedScopeId);
        _drafts.TryGetValue(normalizedScopeId, out var scopeDrafts);
        return Task.FromResult(new StudioWorkspaceSnapshot(
            $"studio-workspace:{normalizedScopeId}",
            normalizedScopeId,
            new StudioWorkspaceSettings(
                UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
                [new StudioWorkspaceDirectory($"scope:{normalizedScopeId}", normalizedScopeId, $"scope://{normalizedScopeId}", true)],
                "blue",
                "light"),
            [new StudioWorkspaceDirectory($"scope:{normalizedScopeId}", normalizedScopeId, $"scope://{normalizedScopeId}", true)],
            scopeDrafts?.Values.ToList() ?? [],
            11,
            DateTimeOffset.UtcNow));
    }

    public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(
        StudioWorkflowDraftRecord draft,
        long? expectedVersion = null,
        CancellationToken ct = default) =>
        SaveDraftAsync("scope-1", draft, expectedVersion, ct);

    public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(
        string scopeId,
        StudioWorkflowDraftRecord draft,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var normalizedScopeId = NormalizeScopeId(scopeId);
        SavedDrafts.Add(new ScopedWorkflowUpload(
            normalizedScopeId,
            draft.WorkflowId,
            draft.Name,
            draft.Yaml,
            draft.UpdatedAtUtc,
            expectedVersion));
        GetOrCreateScope(normalizedScopeId)[draft.WorkflowId] = draft;
        return Task.FromResult(Receipt(normalizedScopeId, expectedVersion));
    }

    public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
        string workflowId,
        long? expectedVersion = null,
        CancellationToken ct = default) =>
        DeleteDraftAsync("scope-1", workflowId, expectedVersion, ct);

    public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
        string scopeId,
        string workflowId,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeScopeId(scopeId);
        DeletedDrafts.Add(new ScopedWorkflowDelete(normalizedScopeId, workflowId, expectedVersion));
        if (_drafts.TryGetValue(normalizedScopeId, out var scopeDrafts))
        {
            scopeDrafts.Remove(workflowId);
        }

        return Task.FromResult(Receipt(normalizedScopeId, expectedVersion));
    }

    public Task<StudioWorkspaceCommandReceipt> UpdateSettingsAsync(
        StudioWorkspaceSettings settings,
        long? expectedVersion = null,
        CancellationToken ct = default) =>
        Task.FromResult(Receipt("scope-1", expectedVersion));

    public Task<StudioWorkspaceCommandReceipt> AddDirectoryAsync(
        StudioWorkspaceDirectory directory,
        long? expectedVersion = null,
        CancellationToken ct = default) =>
        Task.FromResult(Receipt("scope-1", expectedVersion));

    public Task<StudioWorkspaceCommandReceipt> RemoveDirectoryAsync(
        string directoryId,
        long? expectedVersion = null,
        CancellationToken ct = default) =>
        Task.FromResult(Receipt("scope-1", expectedVersion));

    private static StudioWorkspaceCommandReceipt Receipt(string scopeId, long? expectedVersion) =>
        new($"studio-workspace:{scopeId}", $"studio-workspace:{scopeId}", Guid.NewGuid().ToString("N"), expectedVersion);

    private Dictionary<string, StudioWorkflowDraftRecord> GetOrCreateScope(string scopeId)
    {
        if (_drafts.TryGetValue(scopeId, out var scopeDrafts))
        {
            return scopeDrafts;
        }

        scopeDrafts = new Dictionary<string, StudioWorkflowDraftRecord>(StringComparer.Ordinal);
        _drafts[scopeId] = scopeDrafts;
        return scopeDrafts;
    }

    private static string NormalizeScopeId(string scopeId)
    {
        var normalized = scopeId.Trim();
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("scopeId is required.");
        }

        return normalized;
    }
}

internal sealed record ScopedWorkflowUpload(
    string ScopeId,
    string WorkflowId,
    string WorkflowName,
    string Yaml,
    DateTimeOffset UploadedAtUtc,
    long? ExpectedVersion);

internal sealed record ScopedWorkflowDelete(string ScopeId, string WorkflowId, long? ExpectedVersion);

internal sealed record ScopedDraft(string ScopeId, StudioWorkflowDraftRecord Draft);
