using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Workspace;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf;

namespace Aevatar.Studio.Projection.QueryPorts;

// Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
//   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
//   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
public sealed class ProjectionStudioWorkspaceQueryPort : IStudioWorkspaceQueryPort
{
    private static readonly JsonParser StateRootParser = new(
        JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

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
        var scopeId = _scopeResolver.ResolveScopeIdOrDefault();
        return await GetAsync(scopeId, ct);
    }

    // Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
    //   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
    //   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
    public async Task<StudioWorkspaceSnapshot> GetAsync(string scopeId, CancellationToken ct = default)
    {
        var normalizedScopeId = StudioWorkspaceConventions.NormalizeScopeId(scopeId);
        var actorId = StudioWorkspaceConventions.BuildActorId(normalizedScopeId);
        var document = await _documentReader.GetAsync(actorId, ct);
        var state = TryParseStateRoot(document?.StateRootJson) ??
                    new StudioWorkspaceState
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

    private static Application.Studio.Abstractions.StudioWorkspaceSettings ToApplicationSettings(
        Aevatar.Studio.Workspace.StudioWorkspaceSettings? settings,
        IReadOnlyList<Application.Studio.Abstractions.StudioWorkspaceDirectory> directories)
    {
        return new Application.Studio.Abstractions.StudioWorkspaceSettings(
            RuntimeBaseUrl: string.IsNullOrWhiteSpace(settings?.RuntimeBaseUrl)
                ? UserConfigRuntimeDefaults.LocalRuntimeBaseUrl
                : settings.RuntimeBaseUrl,
            Directories: directories,
            AppearanceTheme: "blue",
            ColorMode: "light");
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
            Layout: null,
            draft.UpdatedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            draft.CreatedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            draft.Version);
    }

    private static StudioWorkspaceState? TryParseStateRoot(string? stateRootJson)
    {
        if (string.IsNullOrWhiteSpace(stateRootJson))
            return null;

        try
        {
            return StateRootParser.Parse<StudioWorkspaceState>(stateRootJson);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
        catch (InvalidJsonException)
        {
            return null;
        }
    }
}
