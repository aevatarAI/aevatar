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

// Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
//   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
//   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
internal sealed class ActorDispatchStudioWorkspaceCommandPort : IStudioWorkspaceCommandPort
{
    private const string PublisherId = "aevatar.studio.infrastructure.workspace";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioActorCommandDispatch _commandDispatch;
    private readonly IAppScopeResolver _scopeResolver;

    public ActorDispatchStudioWorkspaceCommandPort(
        IStudioActorBootstrap bootstrap,
        StudioActorCommandDispatch commandDispatch,
        IAppScopeResolver scopeResolver)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
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

    // Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
    //   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
    //   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
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

    // Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
    //   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
    //   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
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
        // Refactor (iter56/cluster-910-projection-activation-cleanup):
        //   old=command-path pre-dispatch activation
        //   new=committed-state plan provider
        //   workspace commands only provision actor and dispatch accepted work.
        var actor = await _bootstrap.EnsureAsync<StudioWorkspaceGAgent>(actorId, ct);
        SetWorkspace(evt, actorId, scopeId);
        var receipt = await _commandDispatch.DispatchAsync(actor, evt, PublisherId, ct);
        return new StudioWorkspaceCommandReceipt(actorId, actor.Id, receipt.CommandId, expectedVersion);
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
            CreatedAtUtc = Timestamp.FromDateTimeOffset(draft.CreatedAtUtc),
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(draft.UpdatedAtUtc),
            Version = draft.Version,
        };
    }

    private static string NormalizeRequired(string value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");
        return normalized;
    }
}
