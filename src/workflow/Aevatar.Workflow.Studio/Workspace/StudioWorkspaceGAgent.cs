using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Workspace;

// Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
//   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
//   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
[GAgent("workflow.studio.workspace")]
public sealed class StudioWorkspaceGAgent : GAgentBase<StudioWorkspaceState>, IProjectedActor
{
    public static string ProjectionKind => StudioWorkspaceConventions.ProjectionKindValue;

    [EventHandler(EndpointName = "updateWorkspaceSettings")]
    public Task HandleSettingsUpdated(StudioWorkspaceSettingsUpdated evt)
    {
        ValidateWorkspace(evt.WorkspaceId, evt.ScopeId);
        ValidateExpectedVersion(evt.ExpectedVersion);
        return PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "addWorkspaceDirectory")]
    public Task HandleDirectoryAdded(StudioWorkspaceDirectoryAdded evt)
    {
        ValidateWorkspace(evt.WorkspaceId, evt.ScopeId);
        ArgumentNullException.ThrowIfNull(evt.Directory);
        ValidateExpectedVersion(evt.ExpectedVersion);
        if (string.IsNullOrWhiteSpace(evt.Directory.DirectoryId))
            throw new InvalidOperationException("directory_id is required.");
        return PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "removeWorkspaceDirectory")]
    public Task HandleDirectoryRemoved(StudioWorkspaceDirectoryRemoved evt)
    {
        ValidateWorkspace(evt.WorkspaceId, evt.ScopeId);
        ValidateExpectedVersion(evt.ExpectedVersion);
        if (string.IsNullOrWhiteSpace(evt.DirectoryId))
            throw new InvalidOperationException("directory_id is required.");
        return PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "saveWorkflowDraft")]
    public Task HandleDraftSaved(StudioWorkflowDraftSaved evt)
    {
        ValidateWorkspace(evt.WorkspaceId, evt.ScopeId);
        ArgumentNullException.ThrowIfNull(evt.Draft);
        ValidateExpectedVersion(evt.ExpectedVersion);
        if (string.IsNullOrWhiteSpace(evt.Draft.WorkflowId))
            throw new InvalidOperationException("workflow_id is required.");
        return PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "deleteWorkflowDraft")]
    public Task HandleDraftDeleted(StudioWorkflowDraftDeleted evt)
    {
        ValidateWorkspace(evt.WorkspaceId, evt.ScopeId);
        ValidateExpectedVersion(evt.ExpectedVersion);
        if (string.IsNullOrWhiteSpace(evt.WorkflowId))
            throw new InvalidOperationException("workflow_id is required.");
        return PersistDomainEventAsync(evt);
    }

    [EventHandler(EndpointName = "repairWorkspaceProjection")]
    public async Task HandleProjectionRepairAsync(RepairStudioWorkspaceProjectionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateWorkspace(command.WorkspaceId, command.ScopeId);
        var currentVersion = EventSourcing?.CurrentVersion
            ?? throw new InvalidOperationException("Workspace event sourcing is unavailable.");
        if (command.MinimumStateVersion <= 0 ||
            currentVersion < command.MinimumStateVersion)
        {
            throw new InvalidOperationException("Workspace projection repair source version changed.");
        }
        if (string.IsNullOrWhiteSpace(State.WorkspaceId) ||
            !string.Equals(State.WorkspaceId, command.WorkspaceId, StringComparison.Ordinal) ||
            !string.Equals(State.ScopeId, command.ScopeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Workspace projection repair identity does not match current state.");
        }

        await RepublishCommittedStateAsync(new StudioWorkspaceSettingsUpdated
        {
            WorkspaceId = State.WorkspaceId,
            ScopeId = State.ScopeId,
            Settings = State.Settings?.Clone() ?? new StudioWorkspaceSettings(),
            UpdatedAtUtc = State.UpdatedAtUtc?.Clone() ?? new Timestamp(),
            ExpectedVersion = State.LastAppliedEventVersion,
        });
    }

    protected override StudioWorkspaceState TransitionState(
        StudioWorkspaceState current,
        IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<StudioWorkspaceSettingsUpdated>(ApplySettingsUpdated)
            .On<StudioWorkspaceDirectoryAdded>(ApplyDirectoryAdded)
            .On<StudioWorkspaceDirectoryRemoved>(ApplyDirectoryRemoved)
            .On<StudioWorkflowDraftSaved>(ApplyDraftSaved)
            .On<StudioWorkflowDraftDeleted>(ApplyDraftDeleted)
            .OrCurrent();
    }

    private void ValidateWorkspace(string workspaceId, string scopeId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new InvalidOperationException("workspace_id is required.");
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new InvalidOperationException("scope_id is required.");
        if (!string.IsNullOrEmpty(State.WorkspaceId) &&
            !string.Equals(State.WorkspaceId, workspaceId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"workspace actor already initialized with workspace_id '{State.WorkspaceId}'.");
        if (!string.IsNullOrEmpty(State.ScopeId) &&
            !string.Equals(State.ScopeId, scopeId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"workspace actor already initialized with scope_id '{State.ScopeId}'.");
    }

    private void ValidateExpectedVersion(long expectedVersion)
    {
        if (expectedVersion <= 0)
            return;
        if (State.LastAppliedEventVersion != expectedVersion)
            throw new InvalidOperationException(
                $"workspace expected_version {expectedVersion} does not match current version {State.LastAppliedEventVersion}.");
    }

    private static StudioWorkspaceState ApplySettingsUpdated(
        StudioWorkspaceState state,
        StudioWorkspaceSettingsUpdated evt)
    {
        var next = EnsureIdentity(state, evt.WorkspaceId, evt.ScopeId);
        next.Settings = evt.Settings?.Clone() ?? new StudioWorkspaceSettings();
        MarkUpdated(next, evt.UpdatedAtUtc);
        return next;
    }

    private static StudioWorkspaceState ApplyDirectoryAdded(
        StudioWorkspaceState state,
        StudioWorkspaceDirectoryAdded evt)
    {
        var next = EnsureIdentity(state, evt.WorkspaceId, evt.ScopeId);
        var directory = evt.Directory.Clone();
        RemoveDirectory(next.Directories, directory.DirectoryId);
        next.Directories.Add(directory);
        MarkUpdated(next, evt.AddedAtUtc);
        return next;
    }

    private static StudioWorkspaceState ApplyDirectoryRemoved(
        StudioWorkspaceState state,
        StudioWorkspaceDirectoryRemoved evt)
    {
        var next = EnsureIdentity(state, evt.WorkspaceId, evt.ScopeId);
        var existing = FindDirectory(next.Directories, evt.DirectoryId);
        if (existing?.IsBuiltIn == true)
        {
            MarkUpdated(next, evt.RemovedAtUtc);
            return next;
        }

        RemoveDirectory(next.Directories, evt.DirectoryId);
        MarkUpdated(next, evt.RemovedAtUtc);
        return next;
    }

    private static StudioWorkspaceState ApplyDraftSaved(
        StudioWorkspaceState state,
        StudioWorkflowDraftSaved evt)
    {
        var next = EnsureIdentity(state, evt.WorkspaceId, evt.ScopeId);
        var draft = evt.Draft.Clone();
        var previousVersion = next.Drafts.TryGetValue(draft.WorkflowId, out var existing)
            ? existing.Version
            : 0;
        draft.Version = previousVersion + 1;
        draft.UpdatedAtUtc = evt.SavedAtUtc;
        if (draft.CreatedAtUtc == null)
            draft.CreatedAtUtc = existing?.CreatedAtUtc ?? evt.SavedAtUtc;
        next.Drafts[draft.WorkflowId] = draft;
        MarkUpdated(next, evt.SavedAtUtc);
        return next;
    }

    private static StudioWorkspaceState ApplyDraftDeleted(
        StudioWorkspaceState state,
        StudioWorkflowDraftDeleted evt)
    {
        var next = EnsureIdentity(state, evt.WorkspaceId, evt.ScopeId);
        next.Drafts.Remove(evt.WorkflowId);
        MarkUpdated(next, evt.DeletedAtUtc);
        return next;
    }

    private static StudioWorkspaceState EnsureIdentity(
        StudioWorkspaceState state,
        string workspaceId,
        string scopeId)
    {
        var next = state.Clone();
        if (string.IsNullOrEmpty(next.WorkspaceId))
            next.WorkspaceId = workspaceId;
        if (string.IsNullOrEmpty(next.ScopeId))
            next.ScopeId = scopeId;
        return next;
    }

    private static void MarkUpdated(StudioWorkspaceState state, Timestamp timestamp)
    {
        state.UpdatedAtUtc = timestamp ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        state.LastAppliedEventVersion++;
    }

    private static StudioWorkspaceDirectory? FindDirectory(
        RepeatedField<StudioWorkspaceDirectory> directories,
        string directoryId)
    {
        for (var i = 0; i < directories.Count; i++)
        {
            if (string.Equals(directories[i].DirectoryId, directoryId, StringComparison.Ordinal))
                return directories[i];
        }

        return null;
    }

    private static void RemoveDirectory(
        RepeatedField<StudioWorkspaceDirectory> directories,
        string directoryId)
    {
        for (var i = 0; i < directories.Count; i++)
        {
            if (string.Equals(directories[i].DirectoryId, directoryId, StringComparison.Ordinal))
            {
                directories.RemoveAt(i);
                return;
            }
        }
    }
}
