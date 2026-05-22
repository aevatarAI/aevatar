using Aevatar.Foundation.Abstractions;
using Aevatar.Studio.Workspace;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Domain.Studio.Models;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using ApplicationWorkspaceDirectory = Aevatar.Studio.Application.Studio.Abstractions.StudioWorkspaceDirectory;
using ApplicationWorkspaceSettings = Aevatar.Studio.Application.Studio.Abstractions.StudioWorkspaceSettings;
using ProtoWorkspaceDirectory = Aevatar.Studio.Workspace.StudioWorkspaceDirectory;
using ProtoWorkspaceSettings = Aevatar.Studio.Workspace.StudioWorkspaceSettings;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

// Refactor (iter16/cluster-meta-studio-actor-substrate):
//   Old: workspace services wrote directly to a file store and treated that file as authoritative state.
//   New principle: this adapter is only the dispatch boundary; it ensures the workspace actor exists and sends typed commands into its inbox.
// Refactor (iter38/cluster-038-studio-workspace-reuse-existing):
//   Old pattern: Studio scoped workflow drafts 通过 ChronoStorage external storage authority + workspace ports routing 不一致(scopeId routing 显式 vs 隐藏)。
//   New principle: Delete ChronoStorage draft authority。Route scoped workflow drafts through existing IStudioWorkspaceCommandPort / IStudioWorkspaceQueryPort with explicit scopeId。**禁止** new IScopedStudioWorkspacePort / 新 scoped actor / 新 envelope / 新 projection phase / docs/canon change。
internal sealed class ActorDispatchStudioWorkspaceCommandPort : IStudioWorkspaceCommandPort
{
    private readonly IStudioActorBootstrap _bootstrap;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAppScopeResolver _scopeResolver;

    public ActorDispatchStudioWorkspaceCommandPort(
        IStudioActorBootstrap bootstrap,
        IActorDispatchPort dispatchPort,
        IAppScopeResolver scopeResolver)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
    }

    public Task<StudioWorkspaceCommandReceipt> UpdateSettingsAsync(
        ApplicationWorkspaceSettings settings,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return DispatchAsync(ResolveScopeIdOrDefault(), new StudioWorkspaceSettingsUpdated
        {
            Settings = new ProtoWorkspaceSettings
            {
                RuntimeBaseUrl = settings.RuntimeBaseUrl,
                AppearanceTheme = settings.AppearanceTheme,
                ColorMode = settings.ColorMode,
            },
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ExpectedVersion = expectedVersion ?? 0,
        }, expectedVersion, ct);
    }

    public Task<StudioWorkspaceCommandReceipt> AddDirectoryAsync(
        ApplicationWorkspaceDirectory directory,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return DispatchAsync(ResolveScopeIdOrDefault(), new StudioWorkspaceDirectoryAdded
        {
            Directory = new ProtoWorkspaceDirectory
            {
                DirectoryId = directory.DirectoryId,
                Label = directory.Label,
                Path = directory.Path,
                IsBuiltIn = directory.IsBuiltIn,
            },
            AddedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ExpectedVersion = expectedVersion ?? 0,
        }, expectedVersion, ct);
    }

    public Task<StudioWorkspaceCommandReceipt> RemoveDirectoryAsync(
        string directoryId,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        return DispatchAsync(ResolveScopeIdOrDefault(), new StudioWorkspaceDirectoryRemoved
        {
            DirectoryId = NormalizeRequired(directoryId, nameof(directoryId)),
            RemovedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ExpectedVersion = expectedVersion ?? 0,
        }, expectedVersion, ct);
    }

    // Refactor (iter38/cluster-038-studio-workspace-reuse-existing):
    //   Old pattern: Studio scoped workflow drafts 通过 ChronoStorage external storage authority + workspace ports routing 不一致(scopeId routing 显式 vs 隐藏)。
    //   New principle: Delete ChronoStorage draft authority。Route scoped workflow drafts through existing IStudioWorkspaceCommandPort / IStudioWorkspaceQueryPort with explicit scopeId。**禁止** new IScopedStudioWorkspacePort / 新 scoped actor / 新 envelope / 新 projection phase / docs/canon change。
    public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(
        StudioWorkflowDraftRecord draft,
        long? expectedVersion = null,
        CancellationToken ct = default)
        => SaveDraftAsync(ResolveScopeIdOrDefault(), draft, expectedVersion, ct);

    public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(
        string scopeId,
        StudioWorkflowDraftRecord draft,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return DispatchAsync(NormalizeRequired(scopeId, nameof(scopeId)), new StudioWorkflowDraftSaved
        {
            Draft = ToProtoDraft(draft),
            SavedAtUtc = Timestamp.FromDateTimeOffset(draft.UpdatedAtUtc),
            ExpectedVersion = expectedVersion ?? 0,
        }, expectedVersion, ct);
    }

    // Refactor (iter38/cluster-038-studio-workspace-reuse-existing):
    //   Old pattern: Studio scoped workflow drafts 通过 ChronoStorage external storage authority + workspace ports routing 不一致(scopeId routing 显式 vs 隐藏)。
    //   New principle: Delete ChronoStorage draft authority。Route scoped workflow drafts through existing IStudioWorkspaceCommandPort / IStudioWorkspaceQueryPort with explicit scopeId。**禁止** new IScopedStudioWorkspacePort / 新 scoped actor / 新 envelope / 新 projection phase / docs/canon change。
    public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
        string workflowId,
        long? expectedVersion = null,
        CancellationToken ct = default)
        => DeleteDraftAsync(ResolveScopeIdOrDefault(), workflowId, expectedVersion, ct);

    public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
        string scopeId,
        string workflowId,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        return DispatchAsync(NormalizeRequired(scopeId, nameof(scopeId)), new StudioWorkflowDraftDeleted
        {
            WorkflowId = NormalizeRequired(workflowId, nameof(workflowId)),
            DeletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ExpectedVersion = expectedVersion ?? 0,
        }, expectedVersion, ct);
    }

    private async Task<StudioWorkspaceCommandReceipt> DispatchAsync<TEvent>(
        string scopeId,
        TEvent evt,
        long? expectedVersion,
        CancellationToken ct)
        where TEvent : IMessage
    {
        var actorId = StudioWorkspaceConventions.BuildActorId(scopeId);
        var actor = await _bootstrap.EnsureAsync<StudioWorkspaceGAgent>(actorId, ct);
        SetWorkspace(evt, actorId, scopeId);
        await ActorCommandDispatcher.SendAsync(_dispatchPort, actor, evt, ct);
        return new StudioWorkspaceCommandReceipt(actorId, actor.Id, Guid.NewGuid().ToString("N"), expectedVersion);
    }

    private string ResolveScopeIdOrDefault() =>
        NormalizeRequired(_scopeResolver.ResolveScopeIdOrDefault(), "scopeId");

    private static void SetWorkspace(IMessage evt, string workspaceId, string scopeId)
    {
        switch (evt)
        {
            case StudioWorkspaceSettingsUpdated typed:
                typed.WorkspaceId = workspaceId;
                typed.ScopeId = scopeId;
                break;
            case StudioWorkspaceDirectoryAdded typed:
                typed.WorkspaceId = workspaceId;
                typed.ScopeId = scopeId;
                break;
            case StudioWorkspaceDirectoryRemoved typed:
                typed.WorkspaceId = workspaceId;
                typed.ScopeId = scopeId;
                break;
            case StudioWorkflowDraftSaved typed:
                typed.WorkspaceId = workspaceId;
                typed.ScopeId = scopeId;
                break;
            case StudioWorkflowDraftDeleted typed:
                typed.WorkspaceId = workspaceId;
                typed.ScopeId = scopeId;
                break;
        }
    }

    private static StudioWorkflowDraft ToProtoDraft(StudioWorkflowDraftRecord draft)
    {
        return new StudioWorkflowDraft
        {
            WorkflowId = draft.WorkflowId,
            Name = draft.Name,
            FileName = draft.FileName,
            DirectoryId = draft.DirectoryId,
            DirectoryLabel = draft.DirectoryLabel,
            Yaml = draft.Yaml,
            Layout = draft.Layout is null ? null : ToProtoLayout(draft.Layout),
            CreatedAtUtc = Timestamp.FromDateTimeOffset(draft.CreatedAtUtc),
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(draft.UpdatedAtUtc),
            Version = draft.Version,
        };
    }

    internal static StudioWorkflowLayout ToProtoLayout(WorkflowLayoutDocument layout)
    {
        var proto = new StudioWorkflowLayout
        {
            Viewport = new StudioWorkflowViewport
            {
                X = layout.Viewport.X,
                Y = layout.Viewport.Y,
                Zoom = layout.Viewport.Zoom,
            },
            EntryWorkflow = layout.EntryWorkflow ?? string.Empty,
        };
        proto.Nodes.AddRange(layout.NodePositions.Select(item => new StudioWorkflowNodeLayout
        {
            NodeId = item.Key,
            X = item.Value.X,
            Y = item.Value.Y,
        }));
        proto.Groups.AddRange(layout.Groups.Select(item =>
        {
            var group = new StudioWorkflowLayoutGroup { GroupId = item.Key };
            group.NodeIds.AddRange(item.Value);
            return group;
        }));
        proto.Collapsed.AddRange(layout.Collapsed);
        return proto;
    }

    private static string NormalizeRequired(string value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");
        return normalized;
    }
}
