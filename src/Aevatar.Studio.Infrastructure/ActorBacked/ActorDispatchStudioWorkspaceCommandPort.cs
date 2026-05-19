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
        return DispatchAsync(new StudioWorkspaceSettingsUpdated
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
        return DispatchAsync(new StudioWorkspaceDirectoryAdded
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
        return DispatchAsync(new StudioWorkspaceDirectoryRemoved
        {
            DirectoryId = NormalizeRequired(directoryId, nameof(directoryId)),
            RemovedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ExpectedVersion = expectedVersion ?? 0,
        }, expectedVersion, ct);
    }

    public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(
        StudioWorkflowDraftRecord draft,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return DispatchAsync(new StudioWorkflowDraftSaved
        {
            Draft = ToProtoDraft(draft),
            SavedAtUtc = Timestamp.FromDateTimeOffset(draft.UpdatedAtUtc),
            ExpectedVersion = expectedVersion ?? 0,
        }, expectedVersion, ct);
    }

    public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
        string workflowId,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        return DispatchAsync(new StudioWorkflowDraftDeleted
        {
            WorkflowId = NormalizeRequired(workflowId, nameof(workflowId)),
            DeletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ExpectedVersion = expectedVersion ?? 0,
        }, expectedVersion, ct);
    }

    public Task<StudioWorkspaceCommandReceipt> SaveDraftLayoutAsync(
        string workflowId,
        WorkflowLayoutDocument layout,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return DispatchAsync(new StudioWorkflowDraftLayoutSaved
        {
            WorkflowId = NormalizeRequired(workflowId, nameof(workflowId)),
            Layout = ToProtoLayout(layout),
            SavedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ExpectedVersion = expectedVersion ?? 0,
        }, expectedVersion, ct);
    }

    private async Task<StudioWorkspaceCommandReceipt> DispatchAsync<TEvent>(
        TEvent evt,
        long? expectedVersion,
        CancellationToken ct)
        where TEvent : IMessage
    {
        var scopeId = _scopeResolver.ResolveScopeIdOrDefault();
        var actorId = StudioWorkspaceConventions.BuildActorId(scopeId);
        var actor = await _bootstrap.EnsureAsync<StudioWorkspaceGAgent>(actorId, ct);
        SetWorkspace(evt, actorId, scopeId);
        await ActorCommandDispatcher.SendAsync(_dispatchPort, actor, evt, ct);
        return new StudioWorkspaceCommandReceipt(actorId, actor.Id, Guid.NewGuid().ToString("N"), expectedVersion);
    }

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
            case StudioWorkflowDraftLayoutSaved typed:
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
