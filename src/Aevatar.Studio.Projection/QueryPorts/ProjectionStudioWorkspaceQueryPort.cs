using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Workspace;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.QueryPorts;

// Refactor (iter38/cluster-038-studio-workspace-reuse-existing):
//   Old pattern: Studio scoped workflow drafts 通过 ChronoStorage external storage authority + workspace ports routing 不一致(scopeId routing 显式 vs 隐藏)。
//   New principle: Delete ChronoStorage draft authority。Route scoped workflow drafts through existing IStudioWorkspaceCommandPort / IStudioWorkspaceQueryPort with explicit scopeId。**禁止** new IScopedStudioWorkspacePort / 新 scoped actor / 新 envelope / 新 projection phase / docs/canon change。
public sealed class ProjectionStudioWorkspaceQueryPort : IStudioWorkspaceQueryPort
{
    private readonly IProjectionDocumentReader<StudioWorkspaceCurrentStateDocument, string> _documentReader;
    private readonly IAppScopeResolver _scopeResolver;

    public ProjectionStudioWorkspaceQueryPort(
        IProjectionDocumentReader<StudioWorkspaceCurrentStateDocument, string> documentReader,
        IAppScopeResolver scopeResolver)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
    }

    public async Task<StudioWorkspaceSnapshot> GetAsync(CancellationToken ct = default)
    {
        var scopeId = ResolveScopeIdOrDefault();
        return await GetAsync(scopeId, ct);
    }

    // Refactor (iter38/cluster-038-studio-workspace-reuse-existing):
    //   Old pattern: Studio scoped workflow drafts 通过 ChronoStorage external storage authority + workspace ports routing 不一致(scopeId routing 显式 vs 隐藏)。
    //   New principle: Delete ChronoStorage draft authority。Route scoped workflow drafts through existing IStudioWorkspaceCommandPort / IStudioWorkspaceQueryPort with explicit scopeId。**禁止** new IScopedStudioWorkspacePort / 新 scoped actor / 新 envelope / 新 projection phase / docs/canon change。
    public async Task<StudioWorkspaceSnapshot> GetAsync(string scopeId, CancellationToken ct = default)
    {
        var normalizedScopeId = StudioWorkspaceConventions.NormalizeScopeId(scopeId);
        var actorId = StudioWorkspaceConventions.BuildActorId(normalizedScopeId);
        var document = await _documentReader.GetAsync(actorId, ct);
        var state = document?.StateRoot?.Is(StudioWorkspaceState.Descriptor) == true
            ? document.StateRoot.Unpack<StudioWorkspaceState>()
            : new StudioWorkspaceState
            {
                WorkspaceId = actorId,
                ScopeId = normalizedScopeId,
            };

        var directories = state.Directories.Select(ToApplicationDirectory).ToList();
        var settings = ToApplicationSettings(state.Settings, directories);
        return new StudioWorkspaceSnapshot(
            WorkspaceId: string.IsNullOrWhiteSpace(state.WorkspaceId) ? actorId : state.WorkspaceId,
            ScopeId: string.IsNullOrWhiteSpace(state.ScopeId) ? normalizedScopeId : state.ScopeId,
            Settings: settings,
            Directories: directories,
            Drafts: state.Drafts.Values.Select(ToApplicationDraft).ToList(),
            StateVersion: document?.StateVersion ?? state.LastAppliedEventVersion,
            UpdatedAtUtc: document?.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue);
    }

    private string ResolveScopeIdOrDefault()
    {
        var scope = _scopeResolver.Resolve()?.ScopeId;
        if (!string.IsNullOrWhiteSpace(scope))
            return scope;

        if (_scopeResolver.HasAuthenticatedRequestWithoutScope())
            throw new InvalidOperationException(
                "Authenticated caller has no resolvable scope; refusing to route to the shared default workspace.");

        return "default";
    }

    private static Application.Studio.Abstractions.StudioWorkspaceSettings ToApplicationSettings(
        Aevatar.Studio.Workspace.StudioWorkspaceSettings? settings,
        IReadOnlyList<Application.Studio.Abstractions.StudioWorkspaceDirectory> directories)
    {
        return new Application.Studio.Abstractions.StudioWorkspaceSettings(
            RuntimeBaseUrl: string.IsNullOrWhiteSpace(settings?.RuntimeBaseUrl)
                ? UserConfigRuntimeDefaults.LocalRuntimeBaseUrl
                : settings.RuntimeBaseUrl,
            Directories: directories,
            AppearanceTheme: string.IsNullOrWhiteSpace(settings?.AppearanceTheme) ? "blue" : settings.AppearanceTheme,
            ColorMode: string.IsNullOrWhiteSpace(settings?.ColorMode) ? "light" : settings.ColorMode);
    }

    private static Application.Studio.Abstractions.StudioWorkspaceDirectory ToApplicationDirectory(
        Aevatar.Studio.Workspace.StudioWorkspaceDirectory directory)
    {
        return new Application.Studio.Abstractions.StudioWorkspaceDirectory(
            directory.DirectoryId,
            directory.Label,
            directory.Path,
            directory.IsBuiltIn);
    }

    private static StudioWorkflowDraftRecord ToApplicationDraft(StudioWorkflowDraft draft)
    {
        return new StudioWorkflowDraftRecord(
            draft.WorkflowId,
            draft.Name,
            draft.FileName,
            Path.Combine(draft.DirectoryLabel, draft.FileName),
            draft.DirectoryId,
            draft.DirectoryLabel,
            draft.Yaml,
            draft.Layout is null ? null : ToApplicationLayout(draft.Layout),
            draft.UpdatedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            draft.CreatedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            draft.Version);
    }

    private static WorkflowLayoutDocument ToApplicationLayout(StudioWorkflowLayout layout)
    {
        return new WorkflowLayoutDocument
        {
            NodePositions = layout.Nodes.ToDictionary(
                node => node.NodeId,
                node => new WorkflowNodeLayout(node.X, node.Y),
                StringComparer.Ordinal),
            Groups = layout.Groups.ToDictionary(
                group => group.GroupId,
                group => group.NodeIds.ToList(),
                StringComparer.Ordinal),
            Collapsed = layout.Collapsed.ToList(),
            Viewport = layout.Viewport is null
                ? new WorkflowViewport()
                : new WorkflowViewport(layout.Viewport.X, layout.Viewport.Y, layout.Viewport.Zoom),
            EntryWorkflow = string.IsNullOrWhiteSpace(layout.EntryWorkflow) ? null : layout.EntryWorkflow,
        };
    }
}
